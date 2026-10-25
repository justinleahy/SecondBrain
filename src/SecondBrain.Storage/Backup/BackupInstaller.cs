using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Operations;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Migrations;

namespace SecondBrain.Storage.Backup;

public sealed record RestoreInstalled(string RecoveryRecord, long AccountEpoch, long RemappedValues, int IndexedDocuments);
public sealed record RestoreRollback(string RecoveryRecord);

/// <summary>
/// Offline installation leaves the root lock in place. A durable marker prevents normal startup until
/// every directory and the rebuilt index is installed, or a rollback restores every previous directory.
/// </summary>
public sealed class BackupInstaller(IDiskCapacity capacity, long minimumFreeBytes = 0,
    Func<string, CancellationToken, ValueTask>? crashPoint = null)
{
    public async Task<RestoreInstalled> InstallAsync(VerifiedBackupPackage backup, IRestoreLease lease,
        IReadOnlyDictionary<string, string> externalMappings, IReadOnlyList<string> allowedExternalRoots,
        Func<CancellationToken, Task<int>> rebuildIndex, CancellationToken token = default)
    {
        RequireLease(lease);
        var root = RestorePreparation.Absolute(lease.DataRoot);
        var pending = Path.Combine(root, RestoreState.PendingFileName);
        if (Path.Exists(pending)) throw new InvalidOperationException("Recover the previous restore before installing another archive.");
        if (backup.Summary.Partial != 0) throw new InvalidDataException("An incomplete backup cannot be installed.");
        var roots = RestorePreparation.MapRoots(backup.Metadata, root, externalMappings, allowedExternalRoots);
        var id = Guid.NewGuid().ToString("N");
        var changes = roots.Select((item, ordinal) => Change(item.Name, item.Destination, id, ordinal)).Append(Change("index", Path.Combine(root, "index"), id, roots.Count)).ToArray();
        foreach (var item in changes)
        {
            RequireDirectoryChain(Path.GetDirectoryName(item.Destination)!);
            if (Path.Exists(item.Destination) && (!Directory.Exists(item.Destination) || new DirectoryInfo(item.Destination).LinkTarget is not null)) throw Invalid();
            if (Path.Exists(item.Prepared) || Path.Exists(item.Previous)) throw Invalid();
            CheckCapacity(Path.GetDirectoryName(item.Destination)!, checked(backup.Summary.Bytes * 2 + 1024 * 1024));
        }
        var created = new List<string>(); var published = false;
        var plan = new InstallPlan(1, id, root, changes, "preparing");
        token.ThrowIfCancellationRequested();
        // Record ownership before creating any prepared root. A process crash during
        // copy/preparation must leave a recoverable plan, not untracked private trees.
        Save(pending, plan);
        await Hit("restore_after_prepare_record", token);
        try
        {
            foreach (var item in changes) { MakePrivateDirectory(item.Prepared); created.Add(item.Prepared); }
            await foreach (var entry in backup.FilesAsync(token))
            {
                RequireLease(lease);
                var target = changes.Single(item => item.Name == entry.Root);
                var destination = Path.Combine(target.Prepared, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                MakeParents(target.Prepared, Path.GetDirectoryName(destination)!);
                using var source = new ManagedMutationFiles(backup.ContentPath).Open(Path.Combine(backup.ContentPath, entry.Root, entry.Path), "restore-copy");
                using var read = source.OpenSnapshotRead();
                await CopyAsync(read.Stream, destination, entry.Length, entry.Sha256!, token);
                if (!read.Unchanged) throw Invalid();
            }
            var epoch = await ExistingEpochAsync(root, token);
            var state = changes.Single(item => item.Name == "state");
            var prepared = await RestorePreparation.PrepareStateAsync(Path.Combine(state.Prepared, "state.db"), backup.Metadata, roots, root, epoch, token);
            foreach (var directory in created) SyncTree(directory);
            token.ThrowIfCancellationRequested(); RequireLease(lease);
            plan = plan with { Phase = "installing" };
            Save(pending, plan, replace: true); published = true;
            foreach (var item in changes)
            {
                RequireLease(lease); RequireDirectoryChain(Path.GetDirectoryName(item.Destination)!);
                if (Directory.Exists(item.Destination) != item.HadPrevious) throw Invalid();
                if (item.HadPrevious) Directory.Move(item.Destination, item.Previous);
                MigrationFiles.SyncDirectory(Path.GetDirectoryName(item.Destination)!);
                await Hit("restore_after_previous:" + item.Name, token);
                Directory.Move(item.Prepared, item.Destination);
                MigrationFiles.SyncDirectory(Path.GetDirectoryName(item.Destination)!);
                await Hit("restore_after_install:" + item.Name, token);
            }
            var indexed = await rebuildIndex(token);
            RequireLease(lease);
            await Hit("restore_after_rebuild", token);
            var record = Complete(pending, plan with { Phase = "complete" });
            return new(record, prepared.AccountEpoch, prepared.RemappedValues, indexed);
        }
        finally
        {
            // Once published, the marker and previous directories are the recovery mechanism.
            if (!published)
            {
                foreach (var directory in created) if (Directory.Exists(directory)) Directory.Delete(directory, true);
                File.Delete(pending);
                MigrationFiles.SyncDirectory(root);
            }
        }
    }

    public async Task<RestoreRollback> RollbackAsync(IRestoreLease lease, CancellationToken token = default)
    {
        RequireLease(lease);
        var pending = Path.Combine(lease.DataRoot, RestoreState.PendingFileName);
        if (new FileInfo(pending).LinkTarget is not null || new FileInfo(pending).Length > 1024 * 1024) throw Invalid();
        var plan = JsonSerializer.Deserialize<InstallPlan>(await File.ReadAllBytesAsync(pending, token)) ?? throw Invalid();
        Validate(plan, lease);
        if (plan.Phase == "preparing")
        {
            // No destination can have been moved before the durable installing phase.
            // Retain any partial prepared roots in the completed recovery record for review.
            foreach (var item in plan.Roots)
            {
                token.ThrowIfCancellationRequested(); RequireLease(lease);
                RequireDirectoryChain(Path.GetDirectoryName(item.Destination)!);
                if (Path.Exists(item.Previous) || Directory.Exists(item.Destination) != item.HadPrevious) throw Invalid();
                foreach (var path in new[] { item.Destination, item.Prepared })
                    if (Path.Exists(path) && (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)) throw Invalid();
            }
            return new(Complete(pending, plan with { Phase = "rolled_back" }));
        }
        foreach (var item in plan.Roots.Reverse())
        {
            RequireLease(lease); RequireDirectoryChain(Path.GetDirectoryName(item.Destination)!);
            var previous = Directory.Exists(item.Previous); var current = Directory.Exists(item.Destination); var prepared = Directory.Exists(item.Prepared);
            foreach (var path in new[] { item.Previous, item.Destination, item.Prepared })
                if (Path.Exists(path) && (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)) throw Invalid();
            if (previous)
            {
                if (current)
                {
                    if (prepared) throw Invalid();
                    Directory.Move(item.Destination, item.Prepared);
                    MigrationFiles.SyncDirectory(Path.GetDirectoryName(item.Destination)!);
                    await Hit("rollback_after_displace:" + item.Name, token);
                }
                Directory.Move(item.Previous, item.Destination);
            }
            else if (!item.HadPrevious && current)
            {
                if (prepared) throw Invalid();
                Directory.Move(item.Destination, item.Prepared);
            }
            else if (item.HadPrevious && (!current || !prepared)) throw Invalid();
            MigrationFiles.SyncDirectory(Path.GetDirectoryName(item.Destination)!);
            await Hit("rollback_after_restore:" + item.Name, token);
        }
        return new(Complete(pending, plan with { Phase = "rolled_back" }));
    }

    private static RootChange Change(string name, string destination, string id, int ordinal)
    {
        var prefix = Path.Combine(Path.GetDirectoryName(destination)!, ".sb-restore-" + id + "-" + ordinal);
        return new(name, destination, prefix + ".new", prefix + ".old", Directory.Exists(destination));
    }
    private static void Validate(InstallPlan plan, IRestoreLease lease)
    {
        if (plan.Version != 1 || plan.Root != RestorePreparation.Absolute(lease.DataRoot) || plan.Id.Length != 32 || !plan.Id.All(char.IsAsciiHexDigit)
            || plan.Phase is not ("preparing" or "installing") || plan.Roots.Length is < 5 or > 130) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Roots.Length; i++)
        {
            var item = plan.Roots[i]; var expected = Change(item.Name, RestorePreparation.Absolute(item.Destination), plan.Id, i);
            if (!names.Add(item.Name) || item.Prepared != expected.Prepared || item.Previous != expected.Previous
                || item.Name is "state" or "index" or "vault" or "trash" or "keyring" && item.Destination != Path.Combine(plan.Root, item.Name)
                || item.Name is not ("state" or "index" or "vault" or "trash" or "keyring") && !item.Name.StartsWith("external", StringComparison.Ordinal)) throw Invalid();
        }
        if (new[] { "state", "index", "vault", "trash", "keyring" }.Any(name => !names.Contains(name))) throw Invalid();
        foreach (var item in plan.Roots)
            if (plan.Roots.Any(other => item != other && (RestorePreparation.Within(item.Destination, other.Destination) || RestorePreparation.Within(other.Destination, item.Destination)))) throw Invalid();
    }
    private static async Task<long> ExistingEpochAsync(string root, CancellationToken token)
    {
        var path = Path.Combine(root, "state", "state.db");
        if (!File.Exists(path)) return 0;
        using var safe = new ManagedMutationFiles(root).Open(path, "restore-epoch"); using var pinned = safe.OpenSnapshotRead();
        await using var state = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await state.OpenAsync(token);
        return await state.ExecuteScalarAsync<long>(new CommandDefinition("SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'", cancellationToken: token));
    }
    private static async Task CopyAsync(Stream source, string target, long length, string expected, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await using var output = new FileStream(target, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token); if (read == 0) break;
            copied = checked(copied + read); if (copied > length) throw Invalid();
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (copied != length || Convert.ToHexStringLower(hash.GetHashAndReset()) != expected) throw Invalid();
        output.Flush(true);
    }
    private static void Save(string path, InstallPlan plan, bool replace = false)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { JsonSerializer.Serialize(output, plan); output.Flush(true); }
            File.Move(temporary, path, replace); MigrationFiles.SyncDirectory(Path.GetDirectoryName(path)!);
        }
        finally { File.Delete(temporary); }
    }
    private static string Complete(string pending, InstallPlan plan)
    {
        var record = Path.Combine(plan.Root, ".restore-" + plan.Phase + "-" + plan.Id + ".json");
        if (Path.Exists(record))
        {
            if (new FileInfo(record).LinkTarget is not null || new FileInfo(record).Length > 1024 * 1024
                || !File.ReadAllBytes(record).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(plan))) throw Invalid();
        }
        else Save(record, plan);
        File.Delete(pending); MigrationFiles.SyncDirectory(plan.Root); return record;
    }
    private void CheckCapacity(string path, long needed)
    {
        var free = capacity.AvailableBytes(path);
        if (free is null || free < needed || free - needed < minimumFreeBytes) throw new IOException("Insufficient free space for restore preparation.");
    }
    private static void RequireLease(IRestoreLease lease)
    { if (!lease.IsHeld) throw new InvalidOperationException("Restore requires the exclusive data-root lock."); }
    private static void RequireDirectoryChain(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (!current.Exists || current.LinkTarget is not null) throw Invalid();
    }
    private static void MakePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void MakeParents(string root, string path)
    {
        if (!RestorePreparation.Within(path, root)) throw Invalid();
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        { if (part == ".") continue; current = Path.Combine(current, part); MakePrivateDirectory(current); }
    }
    private static void SyncTree(string root)
    { foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Reverse()) MigrationFiles.SyncDirectory(directory); MigrationFiles.SyncDirectory(root); }
    private ValueTask Hit(string name, CancellationToken token) => crashPoint?.Invoke(name, token) ?? ValueTask.CompletedTask;
    private static InvalidDataException Invalid() => new("Restore filesystem or recovery metadata changed; previous files have been preserved.");
    private sealed record RootChange(string Name, string Destination, string Prepared, string Previous, bool HadPrevious);
    private sealed record InstallPlan(int Version, string Id, string Root, RootChange[] Roots, string Phase);
}
