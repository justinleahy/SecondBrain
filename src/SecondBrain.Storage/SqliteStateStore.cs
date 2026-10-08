using Microsoft.Data.Sqlite;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Connections;

namespace SecondBrain.Storage;

/// <summary>The state/state.db store, with FULL synchronous WAL commits.</summary>
public sealed class SqliteStateStore(string dataRoot, SqliteStoreOptions? options = null)
    : SqliteStore(dataRoot, state: true, options), IStateStore, IStoreSnapshot
{
    public ValueTask CreateSnapshotAsync(string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Snapshot paths must be absolute.", nameof(destination));
        if (Path.GetFullPath(destination) == DatabasePath) throw new ArgumentException("A snapshot must be a separate database.", nameof(destination));
        return RunMaintenanceAsync(source =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ConnectionFactory.MakePrivate(Path.GetDirectoryName(destination)!, directory: true);
            using var reserve = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            reserve.Dispose();
            ConnectionFactory.MakePrivate(destination);
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
            snapshot.Open();
            source.BackupDatabase(snapshot);
            VerifyIntegrity(snapshot);
            snapshot.Dispose();
            using var durable = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            durable.Flush(flushToDisk: true);
            Migrations.MigrationFiles.SyncDirectory(Path.GetDirectoryName(destination)!);
        }, cancellationToken);
    }

    public ValueTask RestoreSnapshotAsync(string snapshotPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        return RunMaintenanceAsync(destination =>
        {
            RequireNoReadLeases();
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            snapshot.Open();
            VerifyIntegrity(snapshot);
            snapshot.BackupDatabase(destination);
            VerifyIntegrity(destination);
        }, cancellationToken);
    }

    private static void VerifyIntegrity(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("The SQLite snapshot failed integrity verification.");
    }
}
