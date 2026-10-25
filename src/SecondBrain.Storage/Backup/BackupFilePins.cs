using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using SecondBrain.Storage.Connections;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Backup;

public sealed record BackupRoot(string Name, string Path);
public sealed record BackupPinnedFile(long Id, string Root, string RelativePath, long Length, string Status, string? Sha256, string? Error, string? CopyPath);

/// <summary>
/// A disk-backed file manifest and SQLite snapshot at one publication barrier. Before the journal changes a
/// captured file, its old bytes are copied lazily. The copier and mutator share fixed striped locks;
/// neither the list of files nor their content grows resident memory with corpus size.
/// </summary>
public sealed class BackupFilePins : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, BackupFilePins> Active = new(StringComparer.Ordinal);
    private readonly string dataRoot;
    private readonly OperationWorkspace workspace;
    private readonly SqliteConnection catalog;
    private readonly SemaphoreSlim database = new(1, 1);
    private readonly SemaphoreSlim[] stripes = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private int closed;
    public string WorkspacePath { get; }
    public string DataRoot => dataRoot;
    public DateTimeOffset CapturedAt { get; private set; }
    public IReadOnlyList<BackupRoot> Roots { get; private set; } = Array.Empty<BackupRoot>();
    public string StateSnapshotPath => Path.Combine(WorkspacePath, "state.db");
    public string ManifestPath => Path.Combine(WorkspacePath, "files.db");

    private BackupFilePins(string root, OperationWorkspace workspace, SqliteConnection connection)
    { dataRoot = root; this.workspace = workspace; WorkspacePath = workspace.Path; catalog = connection; }

    public static async Task<BackupFilePins> CaptureAsync(MutationJournal journal, IStoreSnapshot snapshot, string dataRoot,
        IReadOnlyList<BackupRoot> roots, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(journal); ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(roots);
        if (!Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException("Data root must be absolute.", nameof(dataRoot));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (roots.Count > 128 || roots.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != roots.Count)
            throw new ArgumentException("Backup roots must have distinct names, with at most 128 roots.", nameof(roots));
        foreach (var item in roots)
        {
            ValidateName(item.Name);
            if (!Path.IsPathFullyQualified(item.Path) || !Directory.Exists(item.Path) || new DirectoryInfo(item.Path).LinkTarget is not null)
                throw new ArgumentException("Backup roots must be existing absolute directories without links.", nameof(roots));
            var path = Path.GetFullPath(item.Path);
            new ManagedMutationFiles(path, requireSafeRoot: true).ValidateRoot();
            if (root == path || root.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("A backup root must not include the backup workspace.", nameof(roots));
        }
        var paths = roots.Select(item => Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
        if (paths.Any(path => paths.Count(other => path == other || path.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.Ordinal)) != 1))
            throw new ArgumentException("Backup roots must not overlap.", nameof(roots));
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        if (new DirectoryInfo(staging).LinkTarget is not null) throw new InvalidDataException("Backup staging must not be a link.");
        ConnectionFactory.MakePrivate(staging, directory: true);
        var workspace = OperationWorkspace.Create(staging, "backup");
        SqliteConnection? connection = null;
        BackupFilePins? result = null;
        try
        {
            var pathToCatalog = Path.Combine(workspace.Path, "files.db");
            using (OpenPrivate(pathToCatalog, FileMode.CreateNew)) { }
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = pathToCatalog, Pooling = false, DefaultTimeout = 5 }.ToString());
            result = new BackupFilePins(root, workspace, connection);
            await connection.OpenAsync(token);
            await connection.ExecuteAsync(new CommandDefinition("""
                -- This catalog is disposable. A crash abandons the entire workspace;
                -- only the final encrypted archive is published durably.
                PRAGMA journal_mode=DELETE; PRAGMA synchronous=OFF; PRAGMA cache_size=-2048;
                CREATE TABLE files(id INTEGER PRIMARY KEY,root TEXT NOT NULL,relative_path TEXT NOT NULL,disk_path TEXT NOT NULL UNIQUE,
                  root_path TEXT NOT NULL,fingerprint TEXT,length INTEGER NOT NULL,status TEXT NOT NULL,sha256 TEXT,error TEXT,copy_path TEXT,
                  UNIQUE(root,relative_path));
                """, cancellationToken: token));
            using var barrier = await journal.AcquireBackupBarrierAsync(token);
            if (Active.ContainsKey(root)) throw new InvalidOperationException("A backup is already capturing this data root.");
            await snapshot.CreateSnapshotAsync(result.StateSnapshotPath, token);
            result.CapturedAt = DateTimeOffset.UtcNow;
            result.Roots = Array.AsReadOnly(roots.Select(item => item with { Path = Path.GetFullPath(item.Path) }).ToArray());
            using var transaction = connection.BeginTransaction();
            foreach (var item in roots)
                await result.CaptureRootAsync(item with { Path = Path.GetFullPath(item.Path) }, transaction, token);
            transaction.Commit();
            if (!Active.TryAdd(root, result)) throw new InvalidOperationException("A backup is already capturing this data root.");
            return result;
        }
        catch
        {
            if (result is not null) await result.DisposeAsync();
            else { if (connection is not null) await connection.DisposeAsync(); workspace.Dispose(); }
            throw;
        }
    }

    private async Task CaptureRootAsync(BackupRoot root, SqliteTransaction transaction, CancellationToken token)
    {
        // Do not recurse through a link, and retain unreadable entries as partial instead of omitting them.
        var pending = new Stack<(string Directory, IEnumerator<string> Entries)>();
        pending.Push((root.Path, Directory.EnumerateFileSystemEntries(root.Path).GetEnumerator()));
        var files = new ManagedMutationFiles(root.Path, requireSafeRoot: true);
        try
        {
            while (pending.TryPeek(out var level))
            {
                string path;
                try
                {
                    if (!level.Entries.MoveNext()) { pending.Pop().Entries.Dispose(); continue; }
                    path = level.Entries.Current;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    pending.Pop().Entries.Dispose();
                    await InsertAsync(root, level.Directory, null, 0, "directory_unreadable", transaction, token); continue;
                }
                token.ThrowIfCancellationRequested();
                string? fingerprint = null, failure = null; long length = 0;
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) failure = "linked_entry";
                    else if ((attributes & FileAttributes.Directory) != 0)
                    { pending.Push((path, Directory.EnumerateFileSystemEntries(path).GetEnumerator())); continue; }
                    else
                    {
                        using var lease = files.Open(path, "backup-capture"); using var read = lease.OpenSnapshotRead();
                        fingerprint = read.Identity.Version; length = read.Identity.Length;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { failure = "file_unreadable"; }
                await InsertAsync(root, path, fingerprint, length, failure, transaction, token);
            }
        }
        finally { while (pending.TryPop(out var level)) level.Entries.Dispose(); }
    }

    private Task InsertAsync(BackupRoot root, string path, string? fingerprint, long length, string? failure, SqliteTransaction transaction, CancellationToken token) =>
        catalog.ExecuteAsync(new CommandDefinition("""
            INSERT INTO files(root,relative_path,disk_path,root_path,fingerprint,length,status,error)
            VALUES(@name,@relative,@path,@rootPath,@fingerprint,@length,@status,@failure)
            """, new { name = root.Name, relative = Path.GetRelativePath(root.Path, path).Replace(Path.DirectorySeparatorChar, '/'), path,
                rootPath = root.Path, fingerprint, length, status = failure is null ? "pending" : "partial", failure }, transaction, cancellationToken: token));

    internal static async ValueTask PreserveBeforeChangeAsync(string path, CancellationToken token)
    {
        foreach (var capture in Active.Values)
        {
            if (!path.StartsWith(capture.dataRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            try { await capture.EnsureCopiedAsync(path, token, tolerateClosed: true); }
            // Backup failures must not destroy or indefinitely prevent a separately authorized edit.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException) { }
        }
    }

    public async Task CopyAllAsync(CancellationToken token = default)
    {
        await foreach (var row in FilesAsync(token))
            if (row.Status == "pending") await EnsureCopiedAsync(await DiskPathAsync(row.Id, token), token);
    }

    public async IAsyncEnumerable<BackupPinnedFile> FilesAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        long after = 0;
        while (true)
        {
            BackupPinnedFile[] rows;
            await database.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(closed != 0, this);
                rows = (await catalog.QueryAsync<BackupPinnedFile>(new CommandDefinition("""
                    SELECT id Id,root Root,relative_path RelativePath,length Length,status Status,sha256 Sha256,error Error,copy_path CopyPath
                    FROM files WHERE id>@after ORDER BY id LIMIT 128
                    """, new { after }, cancellationToken: token))).ToArray();
            }
            finally { database.Release(); }
            if (rows.Length == 0) yield break;
            foreach (var row in rows) { after = row.Id; yield return row; }
        }
    }

    private async Task<string> DiskPathAsync(long id, CancellationToken token)
    {
        await database.WaitAsync(token);
        try { return await catalog.ExecuteScalarAsync<string>(new CommandDefinition("SELECT disk_path FROM files WHERE id=@id", new { id }, cancellationToken: token)) ?? throw new InvalidDataException("Missing backup file."); }
        finally { database.Release(); }
    }

    private async Task EnsureCopiedAsync(string path, CancellationToken token, bool tolerateClosed = false)
    {
        var gate = stripes[(uint)StringComparer.Ordinal.GetHashCode(path) % stripes.Length];
        await gate.WaitAsync(token);
        try
        {
            if (closed != 0) { if (tolerateClosed) return; throw new ObjectDisposedException(nameof(BackupFilePins)); }
            Pin? pin;
            await database.WaitAsync(token);
            try { pin = await catalog.QuerySingleOrDefaultAsync<Pin>(new CommandDefinition("SELECT id Id,root_path RootPath,fingerprint Fingerprint,length Length,status Status FROM files WHERE disk_path=@path", new { path }, cancellationToken: token)); }
            finally { database.Release(); }
            if (pin is null || pin.Status != "pending") return;
            var destination = Path.Combine(WorkspacePath, pin.Id + ".file");
            var buffer = new byte[65536];
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using var parent = new ManagedMutationFiles(pin.RootPath, requireSafeRoot: true).Open(path, "backup-copy");
                        using var read = parent.OpenSnapshotRead();
                        if (read.Identity.Version != pin.Fingerprint || read.Identity.Length != pin.Length) throw new InvalidDataException("Captured file changed.");
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await using (var output = OpenPrivate(destination, FileMode.Create))
                        {
                            long copied = 0;
                            while (true)
                            {
                                var count = await read.Stream.ReadAsync(buffer, token); if (count == 0) break;
                                copied += count; if (copied > pin.Length) throw new InvalidDataException("Captured file grew.");
                                hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
                            }
                            if (copied != pin.Length || !read.Unchanged) throw new InvalidDataException("Captured file changed.");
                            // The copy is consumed before the encrypted archive's durable
                            // publication. An interrupted copy is never resumed or published.
                            await output.FlushAsync(token);
                        }
                        await SetResultAsync(pin.Id, destination, Convert.ToHexStringLower(hash.GetHashAndReset()), null, token);
                        return;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        File.Delete(destination);
                        if (attempt == 2) await SetResultAsync(pin.Id, null, null, "file_changed_or_unreadable", token);
                    }
                }
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        finally { gate.Release(); }
    }

    private async Task SetResultAsync(long id, string? path, string? hash, string? error, CancellationToken token)
    {
        await database.WaitAsync(token);
        try { await catalog.ExecuteAsync(new CommandDefinition("UPDATE files SET status=@status,sha256=@hash,error=@error,copy_path=@path WHERE id=@id", new { id, path, hash, error, status = error is null ? "copied" : "partial" }, cancellationToken: token)); }
        finally { database.Release(); }
    }
    private static FileStream OpenPrivate(string path, FileMode mode)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Backup capture requires Unix filesystem primitives.");
        return File.Open(path, new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 128 || name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            throw new ArgumentException("Invalid backup root name.");
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        ((ICollection<KeyValuePair<string, BackupFilePins>>)Active).Remove(new(dataRoot, this));
        foreach (var gate in stripes) await gate.WaitAsync();
        try
        {
            await database.WaitAsync();
            try { await catalog.DisposeAsync(); }
            finally { database.Release(); }
            workspace.Dispose();
        }
        finally { foreach (var gate in stripes) gate.Release(); }
    }
    private sealed class Pin
    {
        public long Id { get; set; }
        public string RootPath { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public long Length { get; set; }
        public string Status { get; set; } = "";
    }
}
