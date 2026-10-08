using System.Data.Common;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Migrations;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class Migrations
{
    [Fact]
    public async Task CrashBetweenStoresRecovers()
    {
        using var root = new MigrationTestDataRoot();
        await using (var stores = new MigrationTestStores(root.Path, new MigrationTestCrashPoints(MigrationCrashPointNames.AfterStateMigration)))
        {
            await Assert.ThrowsAsync<MigrationTestCrashException>(() => stores.Runner.MigrateAsync().AsTask());
            Assert.Equal("applied", await MigrationTestSql.ScalarAsync(stores.State, "SELECT phase FROM migration_state WHERE version = 1;"));
            Assert.Equal("pending", await MigrationTestSql.ScalarAsync(stores.Index, "SELECT phase FROM migration_state WHERE version = 1;"));
            Assert.True(File.Exists(stores.Runner.GetStateSnapshotPath(1)));
        }

        await using (var restarted = new MigrationTestStores(root.Path))
        {
            var report = await restarted.Runner.MigrateAsync();
            Assert.Equal(1, report.StateVersion);
            Assert.Equal(1, report.IndexVersion);
            Assert.Equal("applied", await MigrationTestSql.ScalarAsync(restarted.Index, "SELECT phase FROM migration_state WHERE version = 1;"));
            Assert.Equal("1", await MigrationTestSql.ScalarAsync(restarted.Index, "SELECT value FROM index_meta WHERE key = 'built_from_schema_version';"));
        }

        await using var secondRestart = new MigrationTestStores(root.Path);
        var secondReport = await secondRestart.Runner.MigrateAsync();
        Assert.Empty(secondReport.Recoveries);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(secondRestart.State, "SELECT COUNT(*) FROM migration_state;"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(secondRestart.Index, "SELECT COUNT(*) FROM migration_state;"));
    }

    [Theory]
    [InlineData(MigrationCrashPointNames.AfterStateApplying, "state")]
    [InlineData(MigrationCrashPointNames.AfterIndexApplying, "index")]
    public async Task ApplyingMarkerRecoversByIdempotentRerun(string crashPoint, string storeName)
    {
        using var root = new MigrationTestDataRoot();
        await using (var stores = new MigrationTestStores(root.Path, new MigrationTestCrashPoints(crashPoint)))
        {
            await Assert.ThrowsAsync<MigrationTestCrashException>(() => stores.Runner.MigrateAsync().AsTask());
            Assert.Equal("applying", await MigrationTestSql.ScalarAsync(storeName == "state" ? stores.State : stores.Index,
                "SELECT phase FROM migration_state WHERE version = 1;"));
        }

        await using (var restarted = new MigrationTestStores(root.Path))
        {
            var report = await restarted.Runner.MigrateAsync();
            var recovery = Assert.Single(report.Recoveries);
            Assert.Equal(storeName, recovery.Store);
            Assert.Equal(MigrationRecoveryMethod.ReranIdempotently, recovery.Method);
            Assert.Contains("idempotent re-run", recovery.Detail, StringComparison.Ordinal);
        }

        await using var secondRestart = new MigrationTestStores(root.Path);
        Assert.Empty((await secondRestart.Runner.MigrateAsync()).Recoveries);
    }

    [Fact]
    public async Task EveryM0MigrationIsIdempotentAndPreservesData()
    {
        using var root = new MigrationTestDataRoot();
        string stateSchema;
        string indexSchema;
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await stores.Runner.MigrateAsync();
            await MigrationTestSql.WriteAsync(stores.State,
                "INSERT INTO sources(id, kind, name, created_at) VALUES ('source-1', 'folder', 'keep me', '2026-10-08T00:00:00Z');");
            stateSchema = await MigrationTestSql.SchemaAsync(stores.State);
            indexSchema = await MigrationTestSql.SchemaAsync(stores.Index);
            await MigrationTestSql.WriteAsync(stores.State, "UPDATE migration_state SET phase = 'applying', finished_at = NULL;");
            await MigrationTestSql.WriteAsync(stores.Index, "UPDATE migration_state SET phase = 'applying', finished_at = NULL;");
        }

        await using var restarted = new MigrationTestStores(root.Path);
        var report = await restarted.Runner.MigrateAsync();
        Assert.Equal(2, report.Recoveries.Count);
        Assert.Equal(stateSchema, await MigrationTestSql.SchemaAsync(restarted.State));
        Assert.Equal(indexSchema, await MigrationTestSql.SchemaAsync(restarted.Index));
        Assert.Equal("keep me", await MigrationTestSql.ScalarAsync(restarted.State, "SELECT name FROM sources WHERE id = 'source-1';"));
        var completedAt = await MigrationTestSql.ScalarAsync(restarted.State, "SELECT finished_at FROM migration_state;");
        await restarted.Runner.MigrateAsync();
        Assert.Equal(completedAt, await MigrationTestSql.ScalarAsync(restarted.State, "SELECT finished_at FROM migration_state;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptedSnapshotIsDetectedBeforeRecovery(bool remainsValidSqlite)
    {
        using var root = new MigrationTestDataRoot();
        string snapshot;
        await using (var stores = new MigrationTestStores(root.Path, new MigrationTestCrashPoints(MigrationCrashPointNames.AfterStateApplying)))
        {
            await Assert.ThrowsAsync<MigrationTestCrashException>(() => stores.Runner.MigrateAsync().AsTask());
            snapshot = stores.Runner.GetStateSnapshotPath(1);
        }

        if (remainsValidSqlite)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE tampered (id INTEGER);";
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            await File.WriteAllBytesAsync(snapshot, "not a SQLite database"u8.ToArray());
        }

        await using var restarted = new MigrationTestStores(root.Path);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => restarted.Runner.MigrateAsync().AsTask());
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.Equal("applying", await MigrationTestSql.ScalarAsync(restarted.State, "SELECT phase FROM migration_state;"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(restarted.State, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'account';"));
    }

    [Fact]
    public async Task SnapshotIntegrityCheckRejectsCorruptionEvenWithMatchingManifest()
    {
        using var root = new MigrationTestDataRoot();
        var snapshot = System.IO.Path.Combine(root.Path, "broken.db");
        var bytes = "not a SQLite database"u8.ToArray();
        await File.WriteAllBytesAsync(snapshot, bytes);
        await File.WriteAllTextAsync(snapshot + ".sha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => MigrationRunner.VerifySnapshotAsync(snapshot).AsTask());
        Assert.Contains("valid SQLite", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompatibleInterruptedSchemaRestoresVerifiedSnapshotAndReportsIt()
    {
        using var root = new MigrationTestDataRoot();
        await using (var stores = new MigrationTestStores(root.Path, new MigrationTestCrashPoints(MigrationCrashPointNames.AfterStateApplying)))
        {
            await Assert.ThrowsAsync<MigrationTestCrashException>(() => stores.Runner.MigrateAsync().AsTask());
            await MigrationTestSql.WriteAsync(stores.State, "CREATE TABLE sources(id TEXT PRIMARY KEY);");
        }

        await using (var restarted = new MigrationTestStores(root.Path))
        {
            var report = await restarted.Runner.MigrateAsync();
            var recovery = Assert.Single(report.Recoveries);
            Assert.Equal(MigrationRecoveryMethod.RestoredSnapshot, recovery.Method);
            Assert.Contains("restored its verified", recovery.Detail, StringComparison.Ordinal);
            var columns = await MigrationTestSql.FirstColumnAsync(restarted.State, "SELECT name FROM pragma_table_info('sources');");
            Assert.Contains("created_at", columns);
            Assert.Equal("applied", await MigrationTestSql.ScalarAsync(restarted.State, "SELECT phase FROM migration_state;"));
        }

        await using var secondRestart = new MigrationTestStores(root.Path);
        Assert.Empty((await secondRestart.Runner.MigrateAsync()).Recoveries);
    }

    [Fact]
    public async Task SchemaContainsEveryM0TableAndCredentialLineageColumns()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        var expectedState = "meta sources documents occurrences revisions overrides suppressions absences mutations jobs credentials account passkeys login_attempts idempotency usage payloads payload_refs conversations turns tool_executions pending_operations batch_jobs embedding_spaces".Split(' ');
        var actualState = await MigrationTestSql.FirstColumnAsync(stores.State, "SELECT name FROM sqlite_schema WHERE type = 'table';");
        Assert.All(expectedState, table => Assert.Contains(table, actualState));
        var actualIndex = await MigrationTestSql.FirstColumnAsync(stores.Index, "SELECT name FROM sqlite_schema WHERE type = 'table';");
        Assert.Contains("index_meta", actualIndex);
        Assert.Contains("indexed_documents", actualIndex);
        foreach (var table in new[] { "jobs", "turns", "tool_executions" })
        {
            var columns = await MigrationTestSql.FirstColumnAsync(stores.State, $"SELECT name FROM pragma_table_info('{table}');");
            Assert.Contains("initiating_credential_id", columns);
            Assert.Contains("credential_generation", columns);
            Assert.Contains("account_epoch", columns);
        }

        Assert.Equal("TEXT", await MigrationTestSql.ScalarAsync(stores.State, "SELECT type FROM pragma_table_info('credentials') WHERE name = 'verifier';"));
        var indexes = await MigrationTestSql.FirstColumnAsync(stores.State, "SELECT name FROM sqlite_schema WHERE type = 'index';");
        Assert.All(new[] { "documents_type_occurred", "documents_updated", "documents_status", "documents_natural_key", "occurrences_document", "mutations_status", "jobs_status", "login_attempts_source" }, name => Assert.Contains(name, indexes));
        Assert.Contains("publication_fences", actualState);
        Assert.Contains("indexed_document_generations", actualIndex);
    }

    [Fact]
    public async Task DiskPreflightFailsBeforeMigrationMarkersAreWritten()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path, options: new MigrationOptions { MinimumFreeBytes = long.MaxValue / 2 });
        var error = await Assert.ThrowsAsync<IOException>(() => stores.Runner.MigrateAsync().AsTask());
        Assert.Contains("disk preflight", error.Message, StringComparison.Ordinal);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'migration_state';"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'migration_state';"));
    }

    [Fact]
    public async Task AlreadyAppliedStartupDoesNotRequireMigrationDiskReservation()
    {
        using var root = new MigrationTestDataRoot();
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await stores.Runner.MigrateAsync();
        }

        await using var restarted = new MigrationTestStores(root.Path, options: new MigrationOptions { MinimumFreeBytes = long.MaxValue / 2 });
        var report = await restarted.Runner.MigrateAsync();
        Assert.Equal(1, report.StateVersion);
        Assert.Equal(1, report.IndexVersion);
        Assert.Empty(report.Recoveries);
    }

    [Fact]
    public async Task FutureSchemaVersionIsRejected()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO migration_state(version, phase) VALUES (2, 'applied');");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => stores.Runner.MigrateAsync().AsTask());
        Assert.Contains("Unsupported migration version 2", error.Message, StringComparison.Ordinal);
    }
}

internal sealed class MigrationTestDataRoot : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "secondbrain-migrations-" + Guid.NewGuid().ToString("N"));

    internal MigrationTestDataRoot() => Directory.CreateDirectory(Path);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class MigrationTestStores : IAsyncDisposable
{
    internal SqliteStateStore State { get; }
    internal SqliteIndexStore Index { get; }
    internal MigrationRunner Runner { get; }

    internal MigrationTestStores(string dataRoot, ICrashPoints? crashPoints = null, MigrationOptions? options = null)
    {
        State = new SqliteStateStore(dataRoot);
        Index = new SqliteIndexStore(dataRoot);
        Runner = new MigrationRunner(State, Index, dataRoot, crashPoints, options);
    }

    public async ValueTask DisposeAsync()
    {
        Runner.Dispose();
        await Index.DisposeAsync();
        await State.DisposeAsync();
    }
}

internal sealed class MigrationTestCrashPoints(string expectedPoint) : ICrashPoints
{
    public ValueTask HitAsync(string point, CancellationToken cancellationToken = default)
        => point == expectedPoint ? throw new MigrationTestCrashException() : ValueTask.CompletedTask;
}

internal sealed class MigrationTestCrashException : Exception;

internal static class MigrationTestSql
{
    internal static async Task<object?> ScalarAsync(IStoreHandle store, string sql)
    {
        await using var lease = await store.OpenReadConnectionAsync();
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    internal static async Task WriteAsync(IStoreHandle store, string sql)
    {
        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return await command.ExecuteNonQueryAsync(token);
        });
    }

    internal static async Task<string[]> FirstColumnAsync(IStoreHandle store, string sql)
    {
        await using var lease = await store.OpenReadConnectionAsync();
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    internal static async Task<string> SchemaAsync(IStoreHandle store)
        => string.Join('\n', await FirstColumnAsync(store, "SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name;"));
}
