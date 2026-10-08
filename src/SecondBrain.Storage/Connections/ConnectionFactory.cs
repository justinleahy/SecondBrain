using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace SecondBrain.Storage.Connections;

internal static class ConnectionFactory
{
    internal static SqliteConnection Open(string path, bool state, SqliteStoreOptions options, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
            ForeignKeys = true
        }.ToString());
        try
        {
            connection.Open();
            SetConfig(connection, raw.SQLITE_DBCONFIG_DEFENSIVE, 1);
            SetConfig(connection, raw.SQLITE_DBCONFIG_TRUSTED_SCHEMA, 0);
            SetConfig(connection, raw.SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION, 0);
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous={(state ? "FULL" : "NORMAL")};
                PRAGMA busy_timeout=5000;
                PRAGMA foreign_keys=ON;
                PRAGMA trusted_schema=OFF;
                PRAGMA wal_autocheckpoint={options.WalAutoCheckpointPages};
                PRAGMA journal_size_limit={options.JournalSizeLimitBytes};
                """;
            command.ExecuteNonQuery();
            if (readOnly)
            {
                command.CommandText = "PRAGMA query_only=ON;";
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void SetConfig(SqliteConnection connection, int setting, int value)
    {
        var result = raw.sqlite3_db_config(connection.Handle, setting, value, out var actual);
        if (result != raw.SQLITE_OK || actual != value)
            throw new InvalidOperationException($"SQLite security setting {setting} could not be enforced ({result}).");
    }

    internal static void MakePrivate(string path, bool directory = false)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, directory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
