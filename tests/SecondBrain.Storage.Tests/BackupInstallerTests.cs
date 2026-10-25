using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Operations;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Backup;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Sources;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Infrastructure.Security;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class BackupInstallerTests
{
    [Fact]
    public async Task PreparationRemapsOnlySelectedMetadataAndLeavesOtherPayloadBytesIntact()
    {
        await using var f = await Fixture.Create();
        var note = await f.Note("Keep the literal path " + f.Root);
        var selectedIds = new[] { "sb_mutation_probe_plan", "sb_mutation_probe_conflict", "sb_mutation_probe_external-projection" };
        var preserved = new Dictionary<string, byte[]>
        {
            ["unrelated-payload"] = System.Text.Encoding.UTF8.GetBytes("Not JSON: " + f.Root),
            ["sb_mutation_redacted_plan"] = JsonSerializer.SerializeToUtf8Bytes(new { Path = f.Root + "/vault/redacted.md" }),
            ["sb_mutation_not_a_plan_suffix"] = JsonSerializer.SerializeToUtf8Bytes(new { Path = f.Root + "/vault/unrelated.md" })
        };
        await f.Stores.State.QueueWriteAsync(async (connection, transaction, token) =>
        {
            foreach (var id in selectedIds.Concat(preserved.Keys))
            {
                var bytes = preserved.GetValueOrDefault(id) ?? JsonSerializer.SerializeToUtf8Bytes(new
                { Path = f.Root + "/vault/selected.md", Literal = f.Root, RemovalTargets = new[] { new { Path = f.Root + "/trash/selected.md" } } });
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO payloads(id,sha256,bytes,created_at,redacted_at) VALUES(@id,@hash,@bytes,@now,@redacted)
                    """, new { id, hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes,
                        now = DateTimeOffset.UtcNow.ToString("O"), redacted = id == "sb_mutation_redacted_plan" ? DateTimeOffset.UtcNow.ToString("O") : null },
                    transaction, cancellationToken: token));
            }
            return 0;
        });
        await using var backup = await f.Backup();
        using var target = new MigrationTestDataRoot();
        var roots = RestorePreparation.MapRoots(backup.Metadata, target.Path, new Dictionary<string, string>(), []);
        var path = Path.Combine(backup.ContentPath, "state", "state.db");
        var prepared = await RestorePreparation.PrepareStateAsync(path, backup.Metadata, roots, target.Path, 0);
        Assert.True(prepared.RemappedValues >= selectedIds.Length + 1);
        await using var state = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await state.OpenAsync();
        foreach (var id in selectedIds)
        {
            var bytes = await state.ExecuteScalarAsync<byte[]>("SELECT bytes FROM payloads WHERE id=@id", new { id });
            using var value = JsonDocument.Parse(bytes!);
            Assert.StartsWith(target.Path + "/vault/", value.RootElement.GetProperty("Path").GetString());
            Assert.Equal(f.Root, value.RootElement.GetProperty("Literal").GetString());
            Assert.StartsWith(target.Path + "/trash/", value.RootElement.GetProperty("RemovalTargets")[0].GetProperty("Path").GetString());
        }
        foreach (var (id, expected) in preserved)
            Assert.Equal(expected, await state.ExecuteScalarAsync<byte[]>("SELECT bytes FROM payloads WHERE id=@id", new { id }));
        Assert.Equal(0, await state.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name='restore_payload_rows'"));
        Assert.Equal("Keep the literal path " + f.Root, (await f.Documents.GetAsync(note.Id, true))!.Text);
    }

    [Fact]
    public async Task InstallsVerifiedFilesRemapsPathsInvalidatesBothEpochsAndRebuildsSearch()
    {
        await using var f = await Fixture.Create();
        var body = "Keep this literal old root in the note: " + f.Root;
        var note = await f.Note(body);
        var trash = await f.Note("Retained trash");
        await f.Documents.MutateAsync(trash.Id, trash.Revision, "delete", JsonSerializer.SerializeToElement(new { }), Fixture.Admin);
        await f.AddExternal();
        await using var backup = await f.Backup();
        using var target = new MigrationTestDataRoot();
        await SeedTarget(target.Path);
        var external = Path.Combine(target.Path + "-sources", "restored"); Directory.CreateDirectory(Path.GetDirectoryName(external)!);
        try
        {
            using var lease = DataRootLock.Acquire(target.Path);
            var result = await new BackupInstaller(new Disk(), crashPoint: (_, _) =>
            {
                Assert.Throws<DataRootLockedException>(() => DataRootLock.Acquire(target.Path, allowPendingRestore: true));
                return ValueTask.CompletedTask;
            }).InstallAsync(backup, lease,
                new Dictionary<string, string> { [f.External] = external }, [Path.GetDirectoryName(external)!], ct => Rebuild(target.Path, ct));
            Assert.Equal(10, result.AccountEpoch); Assert.True(result.IndexedDocuments >= 2); Assert.True(result.RemappedValues > 0);
            Assert.False(File.Exists(Path.Combine(target.Path, RestoreState.PendingFileName)));
            Assert.Equal("External source", await File.ReadAllTextAsync(Path.Combine(external, "source.txt")));
            await using var state = OpenState(target.Path); await state.OpenAsync();
            Assert.Equal(10, await state.ExecuteScalarAsync<long>("SELECT value FROM meta WHERE key='account_epoch'"));
            Assert.Equal(0, await state.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM credentials WHERE revoked_at IS NULL"));
            var projection = await state.ExecuteScalarAsync<byte[]>("SELECT p.bytes FROM document_projections d JOIN payloads p ON p.id=d.payload_id WHERE d.document_id=@id", new { id = note.Id });
            var decoded = JsonSerializer.Deserialize<TextProjection>(projection)!;
            Assert.Equal(body, decoded.LosslessText); Assert.StartsWith(target.Path + "/vault/", decoded.Path);
            Assert.Equal(external, await state.ExecuteScalarAsync<string>("SELECT json_extract(config_json,'$.path') FROM sources WHERE id='external'"));
            Assert.Equal("deleted", await state.ExecuteScalarAsync<string>("SELECT status FROM documents WHERE id=@id", new { id = trash.Id }));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(target.Path, "trash"), "*", SearchOption.AllDirectories));
            Assert.True(File.Exists(result.RecoveryRecord));
            Assert.NotEmpty(Directory.EnumerateDirectories(target.Path, ".sb-restore-*.old"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(external)!, true); }
    }

    [Theory]
    [InlineData("restore_after_prepare_record")]
    [InlineData("restore_after_previous:state")]
    [InlineData("restore_after_install:state")]
    [InlineData("restore_after_previous:vault")]
    [InlineData("restore_after_install:trash")]
    [InlineData("restore_after_install:external/0")]
    [InlineData("restore_after_install:index")]
    [InlineData("restore_after_rebuild")]
    public async Task InterruptedInstallRollsBackEveryRootIncludingExternal(string boundary)
    {
        await using var f = await Fixture.Create(); await f.Note("Restored note"); await f.AddExternal();
        await using var backup = await f.Backup(); using var target = new MigrationTestDataRoot(); await SeedTarget(target.Path);
        var external = target.Path + "-external"; Directory.CreateDirectory(external); await File.WriteAllTextAsync(Path.Combine(external, "old.txt"), "Old source");
        try
        {
            var lease = new Lease(target.Path);
            var installer = new BackupInstaller(new Disk(), crashPoint: (point, _) => point == boundary ? throw new SimulatedCrash() : ValueTask.CompletedTask);
            await Assert.ThrowsAsync<SimulatedCrash>(() => installer.InstallAsync(backup, lease,
                new Dictionary<string, string> { [f.External] = external }, [external], _ => Task.FromResult(0)));
            Assert.True(File.Exists(Path.Combine(target.Path, RestoreState.PendingFileName)));
            var recovery = await new BackupInstaller(new Disk()).RollbackAsync(lease);
            Assert.True(File.Exists(recovery.RecoveryRecord)); Assert.False(File.Exists(Path.Combine(target.Path, RestoreState.PendingFileName)));
            Assert.Equal("Old source", await File.ReadAllTextAsync(Path.Combine(external, "old.txt")));
            Assert.False(File.Exists(Path.Combine(external, "source.txt")));
            Assert.Equal("Original vault", await File.ReadAllTextAsync(Path.Combine(target.Path, "vault", "old.txt")));
            await using var old = OpenState(target.Path); await old.OpenAsync();
            Assert.Equal(9, await old.ExecuteScalarAsync<int>("SELECT value FROM meta WHERE key='account_epoch'"));
            Assert.Equal(0, await old.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM documents"));
        }
        finally { Directory.Delete(external, true); }
    }

    [Fact]
    public async Task InterruptedPreparationKeepsOriginalsAndRecordsPartialRootsForReview()
    {
        await using var f = await Fixture.Create(); await f.Note("Restore preparation");
        await using var backup = await f.Backup();
        using var target = new MigrationTestDataRoot(); await SeedTarget(target.Path);
        var lease = new Lease(target.Path);
        await Assert.ThrowsAsync<SimulatedCrash>(() => new BackupInstaller(new Disk(), crashPoint: (point, _) =>
            point == "restore_after_prepare_record" ? throw new SimulatedCrash() : ValueTask.CompletedTask)
            .InstallAsync(backup, lease, new Dictionary<string, string>(), [], _ => Task.FromResult(0)));
        var pending = Path.Combine(target.Path, RestoreState.PendingFileName);
        using var marker = JsonDocument.Parse(await File.ReadAllBytesAsync(pending));
        Assert.Equal("preparing", marker.RootElement.GetProperty("Phase").GetString());
        var prepared = marker.RootElement.GetProperty("Roots")[0].GetProperty("Prepared").GetString()!;
        Directory.CreateDirectory(prepared);
        await File.WriteAllTextAsync(Path.Combine(prepared, "partial.dat"), "Interrupted prepared bytes");
        Assert.Throws<IOException>(() => DataRootLock.Acquire(target.Path));
        var recovered = await new BackupInstaller(new Disk()).RollbackAsync(lease);
        Assert.False(File.Exists(pending));
        Assert.True(File.Exists(recovered.RecoveryRecord));
        Assert.True(File.Exists(Path.Combine(prepared, "partial.dat")));
        Assert.Equal("Original vault", await File.ReadAllTextAsync(Path.Combine(target.Path, "vault", "old.txt")));
        await using var state = OpenState(target.Path); await state.OpenAsync();
        Assert.Equal(9, await state.ExecuteScalarAsync<int>("SELECT value FROM meta WHERE key='account_epoch'"));
    }

    [Fact]
    public async Task RollbackCanResumeAfterDisplacingTheNewDirectory()
    {
        await using var f = await Fixture.Create(); await f.Note("New");
        await using var backup = await f.Backup(); using var target = new MigrationTestDataRoot(); await SeedTarget(target.Path);
        var lease = new Lease(target.Path);
        await Assert.ThrowsAsync<SimulatedCrash>(() => new BackupInstaller(new Disk(), crashPoint: (point, _) => point == "restore_after_install:vault" ? throw new SimulatedCrash() : ValueTask.CompletedTask)
            .InstallAsync(backup, lease, new Dictionary<string, string>(), [], _ => Task.FromResult(0)));
        var pending = Path.Combine(target.Path, RestoreState.PendingFileName);
        var savedPlan = await File.ReadAllBytesAsync(pending);
        await Assert.ThrowsAsync<SimulatedCrash>(() => new BackupInstaller(new Disk(), crashPoint: (point, _) => point == "rollback_after_displace:vault" ? throw new SimulatedCrash() : ValueTask.CompletedTask).RollbackAsync(lease));
        await new BackupInstaller(new Disk()).RollbackAsync(lease);
        Assert.Equal("Original vault", await File.ReadAllTextAsync(Path.Combine(target.Path, "vault", "old.txt")));
        // Simulate a crash after the completed recovery record was flushed but before the pending marker was removed.
        await File.WriteAllBytesAsync(pending, savedPlan);
        await new BackupInstaller(new Disk()).RollbackAsync(lease);
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public async Task PartialArchivesCannotInstallOrDisturbTheExistingInstance()
    {
        await using var f = await Fixture.Create(); await f.AddExternal();
        await using var backup = await f.Backup(partial: true);
        Assert.True(backup.Summary.Partial > 0);
        using var target = new MigrationTestDataRoot(); await SeedTarget(target.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupInstaller(new Disk()).InstallAsync(backup, new Lease(target.Path),
            new Dictionary<string, string>(), [f.External], _ => Task.FromResult(0)));
        Assert.False(File.Exists(Path.Combine(target.Path, RestoreState.PendingFileName)));
        Assert.Equal("Original vault", await File.ReadAllTextAsync(Path.Combine(target.Path, "vault", "old.txt")));
    }

    [Fact]
    public async Task InsufficientDiskClosedLeaseAndTamperedVerifiedContentCannotReplaceExistingFiles()
    {
        await using var f = await Fixture.Create(); await f.Note("New");
        await using var backup = await f.Backup(); using var target = new MigrationTestDataRoot(); await SeedTarget(target.Path);
        var lease = new Lease(target.Path);
        await Assert.ThrowsAsync<IOException>(() => new BackupInstaller(new Disk(0)).InstallAsync(backup, lease, new Dictionary<string, string>(), [], _ => Task.FromResult(0)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BackupInstaller(new Disk()).InstallAsync(backup, lease with { IsHeld = false }, new Dictionary<string, string>(), [], _ => Task.FromResult(0)));
        var file = Directory.GetFiles(Path.Combine(backup.ContentPath, "vault"), "*", SearchOption.AllDirectories).First();
        await File.AppendAllTextAsync(file, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupInstaller(new Disk()).InstallAsync(backup, lease, new Dictionary<string, string>(), [], _ => Task.FromResult(0)));
        Assert.False(File.Exists(Path.Combine(target.Path, RestoreState.PendingFileName)));
        Assert.Equal("Original vault", await File.ReadAllTextAsync(Path.Combine(target.Path, "vault", "old.txt")));
        Assert.Empty(Directory.EnumerateDirectories(target.Path, ".sb-restore-*"));
    }

    [Fact]
    public async Task ExternalDestinationsRequireAllowedNonoverlappingRootsAndCannotPointThroughLinks()
    {
        await using var f = await Fixture.Create(); await f.AddExternal();
        await using var backup = await f.Backup(); using var target = new MigrationTestDataRoot(); await SeedTarget(target.Path);
        Assert.Throws<InvalidDataException>(() => RestorePreparation.MapRoots(backup.Metadata, target.Path, new Dictionary<string, string>(), []));
        Assert.Throws<InvalidDataException>(() => RestorePreparation.MapRoots(backup.Metadata, target.Path,
            new Dictionary<string, string> { [f.External] = target.Path }, [target.Path]));
        Assert.Throws<InvalidDataException>(() => RestorePreparation.MapRoots(backup.Metadata, target.Path,
            new Dictionary<string, string> { [f.External + "-unknown"] = f.External }, [f.External]));
        var link = target.Path + "-link"; Directory.CreateSymbolicLink(link, f.External);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new BackupInstaller(new Disk()).InstallAsync(backup, new Lease(target.Path),
                new Dictionary<string, string> { [f.External] = Path.Combine(link, "nested") }, [link], _ => Task.FromResult(0)));
        }
        finally { Directory.Delete(link); }
    }

    private sealed class SimulatedCrash : Exception;
    private sealed record Lease(string DataRoot, bool IsHeld = true) : IRestoreLease;
    private sealed class Disk(long available = long.MaxValue) : IDiskCapacity { public long? AvailableBytes(string path) => available; }
    private static SqliteConnection OpenState(string root) => new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "state", "state.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    private static async Task SeedTarget(string root)
    {
        await using (var stores = new MigrationTestStores(root))
        {
            await stores.Runner.MigrateAsync();
            await stores.State.QueueWriteAsync(async (c, t, ct) => { await c.ExecuteAsync(new CommandDefinition("INSERT INTO meta(key,value) VALUES('account_epoch','9'),('instance_id','previous-instance')", transaction: t, cancellationToken: ct)); return 0; });
        }
        foreach (var name in new[] { "vault", "trash", "keyring" }) Directory.CreateDirectory(Path.Combine(root, name));
        await File.WriteAllTextAsync(Path.Combine(root, "vault", "old.txt"), "Original vault");
    }
    private static async Task<int> Rebuild(string root, CancellationToken token)
    {
        await using var stores = new MigrationTestStores(root); await stores.Runner.MigrateAsync(token);
        var docs = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index));
        return await docs.RebuildAsync(token);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly IngestionAuthority Admin = new("restore-test-admin", 1, 1, "restore test", "session");
        private static readonly byte[] Key = SHA256.HashData("restore-test-key"u8);
        private readonly MigrationTestDataRoot root = new();
        internal string Root => root.Path;
        internal string External => root.Path + "-source";
        internal MigrationTestStores Stores { get; }
        private MutationJournal Journal { get; }
        private ManagedDocumentFiles Files { get; }
        internal SqliteDocumentRepository Documents { get; }
        private bool external;
        private Fixture()
        {
            Stores = new(Root); Journal = new(Stores.State, Root); Files = new(Root, Journal);
            Documents = new(Stores.State, Stores.Index, new PublicationCoordinator(Stores.State, Stores.Index), Files, Options.Create(new SecondBrainOptions { DataRoot = Root }));
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            foreach (var name in new[] { "vault", "trash", "keyring", "verify" }) Directory.CreateDirectory(Path.Combine(f.Root, name));
            await f.Stores.Runner.MigrateAsync();
            await f.Stores.State.QueueWriteAsync(async (c, t, ct) => { await c.ExecuteAsync(new CommandDefinition("INSERT INTO meta(key,value) VALUES('account_epoch','1'),('instance_id','restore-instance')", transaction: t, cancellationToken: ct)); return 0; });
            await new AuthRepository(f.Stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id = Admin.CredentialId, Name = Admin.CredentialName, Kind = "session", Generation = 1, AccountEpoch = 1, Scopes = "read,write,admin", Verifier = "test", Kid = "test", CreatedAt = DateTimeOffset.UtcNow.ToString("O"), SteppedUpAt = DateTimeOffset.UtcNow.ToString("O") });
            return f;
        }
        internal async Task<DocumentRecord> Note(string text) => (await Documents.CaptureAsync(new(text, "Restore note"), Admin)).Document;
        internal async Task AddExternal()
        {
            external = true; Directory.CreateDirectory(External); await File.WriteAllTextAsync(Path.Combine(External, "source.txt"), "External source");
            await new SqliteSourceRepository(Stores.State).AddAsync(new("external", "folder", "External", new(External, ["**"], [], true, "index", false, null, new Dictionary<string, string>(), "poll", 3600), "disabled", null, null, DateTimeOffset.UtcNow));
        }
        internal async Task<VerifiedBackupPackage> Backup(bool partial = false)
        {
            var roots = new List<BackupRoot> { new("vault", Path.Combine(Root, "vault")), new("trash", Path.Combine(Root, "trash")), new("keyring", Path.Combine(Root, "keyring")) };
            if (external) roots.Add(new("external/0", External));
            await using var pins = await BackupFilePins.CaptureAsync(Journal, Stores.State, Root, roots);
            var metadata = await BackupPackage.DescribeAsync(pins, 1, []);
            if (partial) await File.AppendAllTextAsync(Path.Combine(External, "source.txt"), "changed after capture");
            using var output = new MemoryStream(); await BackupPackage.WriteAsync(pins, output, Key, metadata); output.Position = 0;
            return await BackupPackage.VerifyAndStageAsync(output, Key, Path.Combine(Root, "verify"));
        }
        public async ValueTask DisposeAsync() { Files.Dispose(); Journal.Dispose(); await Stores.DisposeAsync(); root.Dispose(); if (Directory.Exists(External)) Directory.Delete(External, true); }
    }
}
