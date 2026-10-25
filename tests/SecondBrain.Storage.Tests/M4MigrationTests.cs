using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Operations;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Backup;
using SecondBrain.Storage.Migrations;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class M4MigrationTests
{
    private static readonly byte[] Key = SHA256.HashData("M4 migration test key"u8);

    [Fact]
    public async Task IndividualEnrichmentSchemaIsBackedUpBeforeNativeBatchMetadata()
    {
        using var root = new MigrationTestDataRoot(); await Legacy(root.Path);
        await using (var first = Services(root.Path))
        {
            await first.GetRequiredService<IMigrationRunner>().MigrateAsync();
            await first.GetRequiredService<IStateStore>().QueueWriteAsync(async (c, t, ct) =>
                await c.ExecuteAsync(new CommandDefinition("ALTER TABLE batch_jobs DROP COLUMN metadata_json; UPDATE app_migrations SET version=7 WHERE version=8", transaction: t, cancellationToken: ct)));
        }
        await using var second = Services(root.Path);
        Assert.Equal(8, (await second.GetRequiredService<IMigrationRunner>().MigrateAsync()).StateVersion);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root.Path, "backups"), "*.sbk").Length);
        await using var read = await second.GetRequiredService<IStateStore>().OpenReadConnectionAsync();
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM pragma_table_info('batch_jobs') WHERE name='metadata_json'"));
    }

    [Fact]
    public async Task EarlierEnrichmentSchemaIsBackedUpBeforeSourceHashTracking()
    {
        using var root = new MigrationTestDataRoot(); await Legacy(root.Path);
        await using (var first = Services(root.Path))
        {
            await first.GetRequiredService<IMigrationRunner>().MigrateAsync();
            await first.GetRequiredService<IStateStore>().QueueWriteAsync(async (c, t, ct) =>
                await c.ExecuteAsync(new CommandDefinition("ALTER TABLE occurrences DROP COLUMN source_content_hash; UPDATE app_migrations SET version=6 WHERE version=8", transaction: t, cancellationToken: ct)));
        }
        await using var second = Services(root.Path);
        Assert.Equal(8, (await second.GetRequiredService<IMigrationRunner>().MigrateAsync()).StateVersion);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root.Path, "backups"), "*.sbk").Length);
        await using var read = await second.GetRequiredService<IStateStore>().OpenReadConnectionAsync();
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM pragma_table_info('occurrences') WHERE name='source_content_hash'"));
    }

    [Fact]
    public async Task UsageSchemaIsBackedUpBeforeAddingEnrichment()
    {
        using var root = new MigrationTestDataRoot(); await Legacy(root.Path);
        await using (var first = Services(root.Path))
        {
            await first.GetRequiredService<IMigrationRunner>().MigrateAsync();
            await first.GetRequiredService<IStateStore>().QueueWriteAsync(async (c, t, ct) =>
                await c.ExecuteAsync(new CommandDefinition("DROP INDEX jobs_enrichment_document; UPDATE app_migrations SET version=5 WHERE version=8", transaction: t, cancellationToken: ct)));
            await first.GetRequiredService<IIndexStore>().QueueWriteAsync(async (c, t, ct) =>
                await c.ExecuteAsync(new CommandDefinition("DROP TABLE enrichment_links; UPDATE app_migrations SET version=5 WHERE version=8", transaction: t, cancellationToken: ct)));
        }
        await using var second = Services(root.Path);
        Assert.Equal(8, (await second.GetRequiredService<IMigrationRunner>().MigrateAsync()).StateVersion);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root.Path, "backups"), "*.sbk").Length);
        await using var read = await second.GetRequiredService<IIndexStore>().OpenReadConnectionAsync();
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name='enrichment_links'"));
    }

    [Fact]
    public async Task EarlierM4StateGetsAnEncryptedBackupBeforeUsageMigration()
    {
        using var root = new MigrationTestDataRoot(); await Legacy(root.Path);
        await using (var first = Services(root.Path))
        {
            await first.GetRequiredService<IMigrationRunner>().MigrateAsync();
            await first.GetRequiredService<IStateStore>().QueueWriteAsync(async (c, t, ct) =>
                await c.ExecuteAsync(new CommandDefinition("DROP TABLE usage_calls; UPDATE app_migrations SET version=4 WHERE version=8", transaction: t, cancellationToken: ct)));
        }
        await using var second = Services(root.Path);
        Assert.Equal(8, (await second.GetRequiredService<IMigrationRunner>().MigrateAsync()).StateVersion);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root.Path, "backups"), "*.sbk").Length);
        await using var read = await second.GetRequiredService<IStateStore>().OpenReadConnectionAsync();
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name='usage_calls'"));
    }

    [Fact]
    public async Task ExistingAccountIsBackedUpBeforeM4AndTheArchiveCanBeRestored()
    {
        using var root = new MigrationTestDataRoot(); await Legacy(root.Path);
        var obsolete = Path.Combine(root.Path, "state", "migration-snapshots", "pre-v1.db");
        Directory.CreateDirectory(Path.GetDirectoryName(obsolete)!);
        File.Copy(Path.Combine(root.Path, "state", "state.db"), obsolete);
        await File.WriteAllTextAsync(obsolete + ".sha256", "obsolete bootstrap checksum");
        await using (var services = Services(root.Path))
        {
            Assert.Equal(8, (await services.GetRequiredService<IMigrationRunner>().MigrateAsync()).StateVersion);
            Assert.False(File.Exists(obsolete));
            Assert.False(File.Exists(obsolete + ".sha256"));
            var archive = Assert.Single(Directory.GetFiles(Path.Combine(root.Path, "backups"), "*.sbk"));
            Assert.DoesNotContain("legacy-password-secret", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(archive)));
            var staging = Directory.CreateDirectory(Path.Combine(root.Path, "verify")).FullName;
            await using var input = File.OpenRead(archive);
            await using var verified = await BackupPackage.VerifyAndStageAsync(input, Key, staging);
            Assert.Equal(1, verified.Metadata.StateSchemaVersion);
            await using (var before = Open(Path.Combine(verified.ContentPath, "state", "state.db")))
            {
                await before.OpenAsync();
                Assert.Equal(0, await before.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name='calendar_subscriptions'"));
                Assert.Equal("legacy-password-secret", await before.ExecuteScalarAsync<string>("SELECT password_hash FROM account"));
            }
            await using var read = await services.GetRequiredService<IStateStore>().OpenReadConnectionAsync();
            Assert.Equal(Path.GetFileName(archive), await read.Connection.ExecuteScalarAsync<string>("SELECT backup_name FROM app_migrations WHERE version=8 AND phase='applied'"));
            using var target = new MigrationTestDataRoot();
            await new BackupInstaller(new Disk()).InstallAsync(verified, new Lease(target.Path), new Dictionary<string, string>(), [], _ => Task.FromResult(0));
            await using var restored = Open(Path.Combine(target.Path, "state", "state.db")); await restored.OpenAsync();
            Assert.Equal("applied", await restored.ExecuteScalarAsync<string>("SELECT phase FROM app_migrations WHERE version=8"));
            Assert.Equal(2, await restored.ExecuteScalarAsync<int>("SELECT value FROM meta WHERE key='account_epoch'"));
        }
        // Once applied, ordinary startup doesn't require the backup key.
        await using var restarted = Services(root.Path, missingKey: true);
        Assert.Equal(8, (await restarted.GetRequiredService<IMigrationRunner>().MigrateAsync()).StateVersion);
        Assert.Single(Directory.GetFiles(Path.Combine(root.Path, "backups"), "*.sbk"));
    }

    [Fact]
    public async Task MissingKeyPreventsAnyM4SchemaChange()
    {
        using var root = new MigrationTestDataRoot(); await Legacy(root.Path);
        await using var services = Services(root.Path, missingKey: true);
        await Assert.ThrowsAsync<BackupException>(() => services.GetRequiredService<IMigrationRunner>().MigrateAsync().AsTask());
        await using var read = await services.GetRequiredService<IStateStore>().OpenReadConnectionAsync();
        Assert.Equal(0, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('app_migrations','calendar_subscriptions')"));
        Assert.Equal("legacy-password-secret", await read.Connection.ExecuteScalarAsync<string>("SELECT password_hash FROM account"));
    }

    [Theory]
    [InlineData("before_m4_schema", 2)]
    [InlineData("after_m4_state", 1)]
    public async Task InterruptedM4UpgradeRetriesWithoutLosingThePreUpgradeBackup(string point, int archives)
    {
        using var root = new MigrationTestDataRoot(); await Legacy(root.Path);
        await using (var services = Services(root.Path, crash: new Crash(point)))
            await Assert.ThrowsAsync<MigrationTestCrashException>(() => services.GetRequiredService<IMigrationRunner>().MigrateAsync().AsTask());
        await using var restarted = Services(root.Path);
        Assert.Equal(8, (await restarted.GetRequiredService<IMigrationRunner>().MigrateAsync()).StateVersion);
        Assert.Equal(archives, Directory.GetFiles(Path.Combine(root.Path, "backups"), "*.sbk").Length);
        await using var state = await restarted.GetRequiredService<IStateStore>().OpenReadConnectionAsync();
        Assert.Equal("applied", await state.Connection.ExecuteScalarAsync<string>("SELECT phase FROM app_migrations WHERE version=8"));
        await using var index = await restarted.GetRequiredService<IIndexStore>().OpenReadConnectionAsync();
        Assert.Equal("applied", await index.Connection.ExecuteScalarAsync<string>("SELECT phase FROM app_migrations WHERE version=8"));
    }

    [Fact]
    public async Task PartialBackupCannotAuthorizeTheUpgrade()
    {
        using var root = new MigrationTestDataRoot(); await Legacy(root.Path);
        var parent = Directory.CreateDirectory(root.Path + "-external").FullName;
        try
        {
            var external = Directory.CreateDirectory(Path.Combine(parent, "source")).FullName;
            var outside = Path.Combine(parent, "outside.txt"); await File.WriteAllTextAsync(outside, "outside");
            File.CreateSymbolicLink(Path.Combine(external, "linked.txt"), outside);
            await using (var database = Open(Path.Combine(root.Path, "state", "state.db")))
            {
                await database.OpenAsync();
                await database.ExecuteAsync("INSERT INTO sources(id,kind,name,config_json,status,created_at) VALUES('external','folder','external',@config,'active','2026-01-01')",
                    new { config = JsonSerializer.Serialize(new { path = external, mode = "index" }) });
            }
            await using var services = Services(root.Path, allowed: [parent]);
            await Assert.ThrowsAsync<InvalidDataException>(() => services.GetRequiredService<IMigrationRunner>().MigrateAsync().AsTask());
            Assert.Single(Directory.GetFiles(Path.Combine(root.Path, "backups"), "*.partial.sbk"));
            await using var read = await services.GetRequiredService<IStateStore>().OpenReadConnectionAsync();
            Assert.Equal(0, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_schema WHERE name='app_migrations'"));
        }
        finally { Directory.Delete(parent, true); }
    }

    private static async Task Legacy(string root)
    {
        foreach (var name in new[] { "vault", "trash", "keyring" })
        {
            var path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "vault", "legacy.txt"), "Legacy private file");
        await using var state = new SqliteStateStore(root); await using var index = new SqliteIndexStore(root);
        const string ledger = "CREATE TABLE migration_state(version INTEGER PRIMARY KEY,phase TEXT NOT NULL,started_at TEXT,finished_at TEXT); INSERT INTO migration_state(version,phase) VALUES(1,'applied');";
        await state.QueueWriteAsync(async (c, t, ct) =>
        {
            await c.ExecuteAsync(new CommandDefinition(ledger + Schema("M0Schema", "StateDdl") + Schema("M1Schema", "StateDdl") + Schema("M2Schema", "StateDdl") + """
                INSERT INTO meta(key,value) VALUES('account_epoch','1'),('instance_id','legacy-instance'),('time_zone','UTC');
                INSERT INTO account(id,password_hash,password_version,updated_at) VALUES(1,'legacy-password-secret',1,'2026-01-01');
                """, transaction: t, cancellationToken: ct)); return 0;
        });
        await index.QueueWriteAsync(async (c, t, ct) => { await c.ExecuteAsync(new CommandDefinition(ledger + Schema("M0Schema", "IndexDdl") + Schema("M1Schema", "IndexDdl"), transaction: t, cancellationToken: ct)); return 0; });
    }
    private static string Schema(string name, string field) => (string)typeof(MigrationRunner).Assembly.GetType("SecondBrain.Storage.Migrations." + name)!
        .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
    private static ServiceProvider Services(string root, bool missingKey = false, ICrashPoints? crash = null, List<string>? allowed = null)
    {
        var options = new SecondBrainOptions { DataRoot = root }; options.Sources.AllowedRoots = allowed ?? [];
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IOptions<SecondBrainOptions>>(Options.Create(options)); services.AddSingleton<IOptionsMonitor<SecondBrainOptions>>(new Monitor(options));
        services.AddSingleton<IDiskCapacity>(new Disk()); services.AddSingleton<IBackupKeyProvider>(new Keys(missingKey));
        if (crash is not null) services.AddSingleton(crash);
        services.AddSecondBrainStorage(); return services.BuildServiceProvider();
    }
    private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    private sealed class Keys(bool missing) : IBackupKeyProvider
    { public ValueTask<BackupKey> ReadAsync(CancellationToken token = default) => missing ? throw new BackupException("backup_key_unavailable") : ValueTask.FromResult(new BackupKey(Key)); }
    private sealed class Disk : IDiskCapacity { public long? AvailableBytes(string path) => 1L << 40; }
    private sealed record Lease(string DataRoot, bool IsHeld = true) : IRestoreLease;
    private sealed class Crash(string point) : ICrashPoints { public ValueTask HitAsync(string name, CancellationToken token = default) => name == point ? throw new MigrationTestCrashException() : ValueTask.CompletedTask; }
    private sealed class Monitor(SecondBrainOptions options) : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue => options;
        public SecondBrainOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
}
