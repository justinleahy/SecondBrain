using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using SecondBrain.Storage.Connections;

namespace SecondBrain.Storage.Backup;

public static partial class BackupPackage
{
    /// <summary>Authenticates and verifies in a new private workspace; it never changes live state or external roots.</summary>
    public static async Task<VerifiedBackupPackage> VerifyAndStageAsync(Stream archive, ReadOnlyMemory<byte> backupKey,
        string stagingParent, BackupRestoreLimits? limits = null, CancellationToken token = default)
    {
        limits ??= new();
        if (limits.MaximumPlaintextBytes < 1 || limits.MaximumFiles < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        if (!Path.IsPathFullyQualified(stagingParent) || !Directory.Exists(stagingParent) || new DirectoryInfo(stagingParent).LinkTarget is not null)
            throw new ArgumentException("Restore staging requires an existing absolute directory without links.", nameof(stagingParent));
        var owned = OperationWorkspace.Create(stagingParent, "restore");
        var workspace = owned.Path;
        var filesRoot = Path.Combine(workspace, "content");
        var plaintextPath = Path.Combine(workspace, "package.data");
        try
        {
            MakeDirectory(filesRoot);
            await using (var plaintext = OpenPrivateFile(plaintextPath, FileMode.CreateNew))
            {
                await BackupCipher.DecryptAsync(archive, new BoundedOutput(plaintext, limits.MaximumPlaintextBytes), backupKey, token);
                plaintext.Flush(flushToDisk: true);
            }
            await using var source = File.OpenRead(plaintextPath);
            var magic = new byte[8]; await source.ReadExactlyAsync(magic, token);
            if (!magic.AsSpan().SequenceEqual(Magic)) throw InvalidPackage();
            using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var metadata = ParseRecord<BackupPackageMetadata>(await ReadRecordAsync(source, MaximumMetadataBytes, manifest, token), "metadata");
            ValidateMetadata(metadata);
            var manifestPath = Path.Combine(workspace, "manifest.db");
            using (OpenPrivateFile(manifestPath, FileMode.CreateNew)) { }
            BackupPackageSummary summary;
            await using (var catalog = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = manifestPath, Pooling = false }.ToString()))
            {
                await catalog.OpenAsync(token);
                await catalog.ExecuteAsync(Sql("""
                    -- This is a disposable verification workspace. The installer
                    -- separately flushes prepared files before publishing its plan.
                    PRAGMA journal_mode=DELETE; PRAGMA synchronous=OFF; PRAGMA cache_size=-2048;
                    CREATE TABLE entries(root TEXT NOT NULL,path TEXT NOT NULL,length INTEGER NOT NULL,status TEXT NOT NULL,sha256 TEXT,error TEXT,
                      document_id TEXT,revision INTEGER,PRIMARY KEY(root,path));
                    """, null, token));
                long count = 0, complete = 0, partial = 0, bytes = 0; var stateFound = false;
                while (true)
                {
                    var record = await ReadRecordAsync(source, MaximumRecordBytes, null, token);
                    using var parsed = JsonDocument.Parse(record);
                    if (parsed.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "end")
                    {
                        summary = ParseRecord<BackupPackageSummary>(record, "end");
                        if (summary.Files != count || summary.Complete != complete || summary.Partial != partial || summary.Bytes != bytes
                            || summary.ManifestSha256 != Convert.ToHexStringLower(manifest.GetHashAndReset())) throw InvalidPackage();
                        if (await source.ReadAsync(new byte[1], token) != 0 || !stateFound) throw InvalidPackage();
                        break;
                    }
                    var entry = ParseRecord<BackupPackageEntry>(record, "file"); ValidateEntry(entry, metadata);
                    var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, record.Length);
                    manifest.AppendData(length); manifest.AppendData(record);
                    if (++count > limits.MaximumFiles) throw InvalidPackage();
                    await catalog.ExecuteAsync(Sql("""
                        INSERT INTO entries(root,path,length,status,sha256,error,document_id,revision) VALUES(@Root,@Path,@Length,@Status,@Sha256,@Error,@DocumentId,@Revision)
                        """, entry, token));
                    if (entry.Status == "partial") { partial++; continue; }
                    if (entry.Length > limits.MaximumPlaintextBytes - bytes || entry.Length > source.Length - source.Position) throw InvalidPackage();
                    var target = ContainedPath(filesRoot, entry.Root, entry.Path);
                    MakeParents(filesRoot, Path.GetDirectoryName(target)!);
                    await using (var destination = OpenPrivateFile(target, FileMode.CreateNew))
                    {
                        await CopyVerifiedAsync(source, destination, entry.Length, entry.Sha256!, token, requireEnd: false);
                        await destination.FlushAsync(token);
                    }
                    if (entry.Root == "state") stateFound = true;
                    complete++; bytes = checked(bytes + entry.Length);
                }
            }
            await using (var state = OpenState(ContainedPath(filesRoot, "state", "state.db")))
            { await state.OpenAsync(token); await VerifyStateAsync(state, metadata, token); }
            await source.DisposeAsync(); File.Delete(plaintextPath);
            return new VerifiedBackupPackage(owned, filesRoot, manifestPath, metadata, summary);
        }
        catch (Exception error) when (error is JsonException or EndOfStreamException or OverflowException or SqliteException)
        {
            owned.Dispose(); throw InvalidPackage();
        }
        catch { owned.Dispose(); throw; }
    }

    private static T ParseRecord<T>(byte[] record, string kind)
    {
        using var document = JsonDocument.Parse(record, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidPackage();
        RejectDuplicateProperties(document.RootElement);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) if (!properties.Add(property.Name)) throw InvalidPackage();
        if (!document.RootElement.TryGetProperty("kind", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() != kind) throw InvalidPackage();
        var required = kind switch
        {
            "metadata" => new[] { "version", "captured_at", "data_root", "instance_id", "account_epoch", "state_schema_version", "state_schema_sha256", "index_schema_version", "processing_generations", "embedding_spaces", "roots" },
            "file" => new[] { "root", "path", "length", "status" },
            "end" => new[] { "files", "complete", "partial", "bytes", "manifest_sha256" },
            _ => throw InvalidPackage()
        };
        if (required.Any(property => !properties.Contains(property))) throw InvalidPackage();
        return JsonSerializer.Deserialize<T>(record, Json) ?? throw InvalidPackage();
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw InvalidPackage();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private static string ContainedPath(string root, string name, string relative)
    {
        var result = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar), relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw InvalidPackage();
        return result;
    }
    private static void MakeDirectory(string path)
    {
        Directory.CreateDirectory(path); if (new DirectoryInfo(path).LinkTarget is not null) throw InvalidPackage();
        ConnectionFactory.MakePrivate(path, directory: true);
    }
    private static void MakeParents(string root, string destination)
    {
        var current = root;
        foreach (var part in Path.GetRelativePath(root, destination).Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            if (part == "..") throw InvalidPackage();
            current = Path.Combine(current, part); MakeDirectory(current);
        }
    }
    private static FileStream OpenPrivateFile(string path, FileMode mode)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Restore requires Unix filesystem primitives.");
        return File.Open(path, new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    }
    private sealed class BoundedOutput(Stream output, long maximum) : Stream
    {
        private long written;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length > maximum - written) throw new InvalidDataException("Restore exceeds the configured staging limit.");
            await output.WriteAsync(buffer, token); written += buffer.Length;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken token) => output.FlushAsync(token);
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Only a fully authenticated and manifest-verified workspace is returned; installation remains a separate operation.</summary>
public sealed class VerifiedBackupPackage : IAsyncDisposable
{
    private readonly OperationWorkspace workspace;
    private readonly string content, manifest;
    private readonly BackupPackageMetadata metadata;
    private readonly BackupPackageSummary summary;
    internal VerifiedBackupPackage(OperationWorkspace workspace, string content, string manifest, BackupPackageMetadata metadata, BackupPackageSummary summary)
    { this.workspace = workspace; this.content = content; this.manifest = manifest; this.metadata = metadata; this.summary = summary; }
    public string WorkspacePath => workspace.Path;
    public string ContentPath => content;
    public string ManifestPath => manifest;
    public BackupPackageMetadata Metadata => metadata;
    public BackupPackageSummary Summary => summary;
    public async IAsyncEnumerable<BackupPackageEntry> FilesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        await using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = manifest, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await database.OpenAsync(token); long after = 0;
        while (true)
        {
            var rows = (await database.QueryAsync<StoredFile>(new CommandDefinition("""
                SELECT rowid Id,root Root,path Path,length Length,status Status,sha256 Sha256,error Error,document_id DocumentId,revision Revision
                FROM entries WHERE rowid>@after ORDER BY rowid LIMIT 128
                """, new { after }, cancellationToken: token))).ToArray();
            if (rows.Length == 0) yield break;
            foreach (var row in rows)
            {
                after = row.Id; yield return new(row.Root, row.Path, row.Length, row.Status, row.Sha256, row.Error, row.DocumentId, row.Revision);
            }
        }
    }
    public ValueTask DisposeAsync() { workspace.Dispose(); return ValueTask.CompletedTask; }
    private sealed class StoredFile
    {
        public long Id { get; set; } public string Root { get; set; } = ""; public string Path { get; set; } = "";
        public long Length { get; set; } public string Status { get; set; } = ""; public string? Sha256 { get; set; }
        public string? Error { get; set; } public string? DocumentId { get; set; } public long? Revision { get; set; }
    }
}
