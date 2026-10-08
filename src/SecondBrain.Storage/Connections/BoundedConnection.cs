using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace SecondBrain.Storage.Connections;

// Consumers only see this lease facade, so a timed-out callback cannot retain a usable writer.
internal sealed class BoundedConnection : DbConnection
{
    private readonly SqliteConnection _inner;
    private readonly SqliteStoreOptions _options;
    private readonly long _deadline;
    private readonly object _gate = new();
    private long _statementDeadline = long.MaxValue;
    private CancellationToken _operationToken;
    private int _revoked;
    private int _detached;
    private readonly List<IDisposable> _resources = [];
    internal BoundedTransaction? WriterTransaction { get; }

    internal BoundedConnection(SqliteConnection inner, SqliteStoreOptions options, TimeSpan lifetime, SqliteTransaction? transaction = null)
    {
        _inner = inner;
        _options = options;
        _deadline = After(lifetime);
        if (transaction is not null) WriterTransaction = new BoundedTransaction(this, transaction, ownedByStore: true);
        raw.sqlite3_progress_handler(inner.Handle, 1000, static state => ((BoundedConnection)state).Expired ? 1 : 0, this);
    }

    private static long After(TimeSpan duration) => Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
    private bool Expired => Volatile.Read(ref _revoked) != 0 || _operationToken.IsCancellationRequested
        || Stopwatch.GetTimestamp() >= Math.Min(_deadline, Volatile.Read(ref _statementDeadline));

    internal long CurrentStatementDeadline => Volatile.Read(ref _statementDeadline);

    internal T Run<T>(Func<T> action, CancellationToken cancellationToken = default, bool startStatement = false, long? statementDeadline = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _revoked) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= _deadline) throw new TimeoutException("The SQLite lease or transaction lifetime expired.");
            _operationToken = cancellationToken;
            if (startStatement) Volatile.Write(ref _statementDeadline, After(_options.StatementTimeout));
            else if (statementDeadline is { } readerDeadline) Volatile.Write(ref _statementDeadline, readerDeadline);
            else Volatile.Write(ref _statementDeadline, long.MaxValue);
            if (Stopwatch.GetTimestamp() >= Volatile.Read(ref _statementDeadline))
                throw new TimeoutException("The SQLite statement budget expired.");
            using var registration = cancellationToken.Register(() => raw.sqlite3_interrupt(_inner.Handle));
            try
            {
                return action();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == raw.SQLITE_INTERRUPT)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Expired) throw new TimeoutException("The SQLite statement or transaction budget expired.", exception);
                throw;
            }
        }
    }

    internal void Revoke()
    {
        if (Interlocked.Exchange(ref _revoked, 1) == 0) raw.sqlite3_interrupt(_inner.Handle);
    }

    internal void Detach()
    {
        if (Interlocked.Exchange(ref _detached, 1) != 0) return;
        Revoke();
        lock (_gate)
        {
            for (var i = _resources.Count - 1; i >= 0; i--) _resources[i].Dispose();
            _resources.Clear();
            raw.sqlite3_progress_handler(_inner.Handle, 0, null!, null!);
        }
    }

    internal T Own<T>(T resource) where T : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }

    internal void Cleanup(Action cleanup)
    {
        lock (_gate)
            if (Volatile.Read(ref _revoked) == 0) cleanup();
    }

    internal void DisposeNative()
    {
        Revoke();
        lock (_gate) _inner.Dispose();
    }

    [AllowNull]
    public override string ConnectionString { get => _inner.ConnectionString; set => throw new NotSupportedException("Store connections cannot be replaced."); }
    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override ConnectionState State => Volatile.Read(ref _revoked) == 0 ? _inner.State : ConnectionState.Closed;
    public override void Open() => throw new NotSupportedException("Dispose the lease instead of opening a connection.");
    public override void Close() => throw new NotSupportedException("Dispose the lease instead of closing a connection.");
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (WriterTransaction is not null) throw new NotSupportedException("The store owns the write transaction.");
        return Run<DbTransaction>(() => new BoundedTransaction(this, Own(_inner.BeginTransaction(isolationLevel, deferred: true)), false), startStatement: true);
    }
    protected override DbCommand CreateDbCommand() => Run<DbCommand>(() => new BoundedCommand(this, _inner.CreateCommand(), WriterTransaction));
    protected override void Dispose(bool disposing) { if (disposing) Revoke(); }
}

internal sealed class BoundedTransaction(BoundedConnection connection, SqliteTransaction inner, bool ownedByStore) : DbTransaction
{
    internal SqliteTransaction Inner { get; } = inner;
    protected override DbConnection DbConnection => connection;
    public override IsolationLevel IsolationLevel => Inner.IsolationLevel;
    public override void Commit()
    {
        if (ownedByStore) throw new NotSupportedException("The store commits write transactions.");
        connection.Run(() => { Inner.Commit(); return 0; }, startStatement: true);
    }
    public override void Rollback()
    {
        if (ownedByStore) throw new NotSupportedException("The store rolls back write transactions.");
        connection.Run(() => { Inner.Rollback(); return 0; }, startStatement: true);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !ownedByStore) connection.Cleanup(Inner.Dispose);
        base.Dispose(disposing);
    }
}

internal sealed class BoundedCommand : DbCommand
{
    private readonly BoundedConnection _connection;
    private readonly SqliteCommand _inner;
    private BoundedTransaction? _transaction;
    internal BoundedCommand(BoundedConnection connection, SqliteCommand inner, BoundedTransaction? transaction)
    {
        _connection = connection;
        _inner = connection.Own(inner);
        _transaction = transaction;
        _inner.Transaction = transaction?.Inner;
    }
    [AllowNull]
    public override string CommandText { get => _inner.CommandText; set => _inner.CommandText = value ?? string.Empty; }
    public override int CommandTimeout { get => _inner.CommandTimeout; set { if (value < 1 || value > 5) throw new ArgumentOutOfRangeException(nameof(value)); _inner.CommandTimeout = value; } }
    public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }
    public override bool DesignTimeVisible { get => _inner.DesignTimeVisible; set => _inner.DesignTimeVisible = value; }
    public override UpdateRowSource UpdatedRowSource { get => _inner.UpdatedRowSource; set => _inner.UpdatedRowSource = value; }
    protected override DbConnection? DbConnection { get => _connection; set { if (!ReferenceEquals(value, _connection)) throw new NotSupportedException(); } }
    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            if (value is not null && (value is not BoundedTransaction bounded || !ReferenceEquals(bounded.Connection, _connection)))
                throw new ArgumentException("Use the transaction supplied by this store.", nameof(value));
            if (_connection.WriterTransaction is not null && !ReferenceEquals(value, _connection.WriterTransaction))
                throw new NotSupportedException("Write commands must use the store transaction.");
            _transaction = (BoundedTransaction?)value;
            _inner.Transaction = _transaction?.Inner;
        }
    }
    public override void Cancel() => _inner.Cancel();
    public override void Prepare() => _connection.Run(() => { _inner.Prepare(); return 0; }, startStatement: true);
    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();
    public override int ExecuteNonQuery() => _connection.Run(_inner.ExecuteNonQuery, startStatement: true);
    public override object? ExecuteScalar() => _connection.Run(_inner.ExecuteScalar, startStatement: true);
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _connection.Run<DbDataReader>(() => new BoundedReader(_connection, _connection.Own(_inner.ExecuteReader(behavior)), _connection.CurrentStatementDeadline), startStatement: true);
    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(_connection.Run(_inner.ExecuteNonQuery, cancellationToken, true));
    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => Task.FromResult(_connection.Run(_inner.ExecuteScalar, cancellationToken, true));
    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) => Task.FromResult(_connection.Run<DbDataReader>(() => new BoundedReader(_connection, _connection.Own(_inner.ExecuteReader(behavior)), _connection.CurrentStatementDeadline), cancellationToken, true));
    protected override void Dispose(bool disposing) { if (disposing) _connection.Cleanup(_inner.Dispose); base.Dispose(disposing); }
}
