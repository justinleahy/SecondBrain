using System.Data.Common;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Storage;
using SecondBrain.Storage;

namespace SecondBrain.Server.Tests.Support;

/// <summary>Small real-SQLite store containing only the Appendix A tables lane D uses.</summary>
public sealed class TestStateStore : IStateStore, IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _readers = new(8, 8);
    private readonly string _connectionString;
    private int _disposed;

    public TestStateStore()
    {
        DirectoryPath = Directory.CreateTempSubdirectory("secondbrain-lane-d-").FullName;
        DatabasePath = System.IO.Path.Combine(DirectoryPath, "state.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
    }

    public string DirectoryPath { get; }
    public string DatabasePath { get; }
    public string Path => DatabasePath;

    public async ValueTask<IReadConnectionLease> OpenReadConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _readers.WaitAsync(cancellationToken);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await OpenAsync(connection, cancellationToken);
            return new ReadLease(connection, _readers);
        }
        catch
        {
            await connection.DisposeAsync();
            _readers.Release();
            throw;
        }
    }

    public async ValueTask<TResult> QueueWriteAsync<TResult>(
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask<TResult>> write,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writer.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await OpenAsync(connection, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var result = await write(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            _writer.Release();
        }
    }

    private static async Task OpenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA synchronous=FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _writer.Dispose();
        _readers.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class ReadLease(SqliteConnection connection, SemaphoreSlim readers) : IReadConnectionLease
    {
        private int _disposed;

        public DbConnection Connection { get; } = connection;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                connection.Dispose();
                readers.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await connection.DisposeAsync();
                readers.Release();
            }
        }
    }

    private const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=FULL;
        PRAGMA busy_timeout=5000;
        PRAGMA foreign_keys=ON;
        PRAGMA trusted_schema=OFF;

        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO meta(key, value) VALUES ('account_epoch', '1');

        CREATE TABLE sources (
          id               TEXT PRIMARY KEY,
          kind             TEXT NOT NULL,
          name             TEXT NOT NULL,
          config_json      TEXT NOT NULL DEFAULT '{}',
          status           TEXT NOT NULL DEFAULT 'active',
          last_scan_at     TEXT,
          last_full_scan_at TEXT,
          created_at       TEXT NOT NULL
        );

        CREATE TABLE credentials (
          id               TEXT PRIMARY KEY,
          name             TEXT NOT NULL,
          kind             TEXT NOT NULL,
          verifier         TEXT NOT NULL,
          scopes           TEXT NOT NULL,
          generation       INTEGER NOT NULL DEFAULT 1,
          kid              TEXT NOT NULL,
          account_epoch    INTEGER NOT NULL,
          device           TEXT,
          idle_expires_at  TEXT, absolute_expires_at TEXT,
          stepped_up_at    TEXT,
          created_at       TEXT NOT NULL, last_used_at TEXT, expires_at TEXT, revoked_at TEXT
        );

        CREATE TABLE account (
          id               INTEGER PRIMARY KEY CHECK (id = 1),
          password_hash    TEXT NOT NULL,
          password_version INTEGER NOT NULL,
          updated_at       TEXT NOT NULL
        );

        CREATE TABLE passkeys (id TEXT PRIMARY KEY, public_key BLOB NOT NULL, sign_count INTEGER NOT NULL, name TEXT, created_at TEXT NOT NULL);
        CREATE TABLE login_attempts (source TEXT NOT NULL, at TEXT NOT NULL, success INTEGER NOT NULL);
        CREATE INDEX login_attempts_source ON login_attempts(source, at);

        CREATE TABLE usage (
          id INTEGER PRIMARY KEY, at TEXT NOT NULL, provider TEXT, model TEXT, role TEXT, turn_id TEXT, credential_id TEXT, job_id TEXT,
          reserved_tokens INTEGER, input_tokens INTEGER, output_tokens INTEGER, cached_input_tokens INTEGER,
          cost_usd REAL, settled INTEGER NOT NULL DEFAULT 0
        );
        """;
}
