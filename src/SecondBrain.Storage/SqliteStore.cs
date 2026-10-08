using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Connections;

namespace SecondBrain.Storage;

/// <summary>Bounded connection ownership shared by the authoritative and disposable stores.</summary>
public abstract class SqliteStore : IStoreHandle, IDisposable, IAsyncDisposable
{
    private readonly bool _state;
    private readonly SqliteStoreOptions _options;
    private readonly Channel<IWriterWork> _queue;
    private readonly Task _writer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _readCapacity;
    private readonly ConcurrentBag<SqliteConnection> _readPool = [];
    private readonly ConcurrentDictionary<ReadLease, byte> _leases = new();
    private readonly object _readGate = new();
    private int _disposed;
    public string DatabasePath { get; }

    protected SqliteStore(string dataRoot, bool state, SqliteStoreOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException("The data root must be absolute.", nameof(dataRoot));
        _options = options ?? new SqliteStoreOptions();
        _options.Validate();
        _state = state;
        var directory = Path.Combine(dataRoot, state ? "state" : "index");
        Directory.CreateDirectory(directory);
        ConnectionFactory.MakePrivate(directory, directory: true);
        DatabasePath = Path.Combine(directory, state ? "state.db" : "index.db");
        var writerConnection = ConnectionFactory.Open(DatabasePath, state, _options);
        ConnectionFactory.MakePrivate(DatabasePath);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(DatabasePath + suffix)) ConnectionFactory.MakePrivate(DatabasePath + suffix);
        _readCapacity = new SemaphoreSlim(_options.ReadPoolSize, _options.ReadPoolSize);
        _queue = Channel.CreateBounded<IWriterWork>(new BoundedChannelOptions(_options.WriterQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _writer = Task.Run(() => WriterLoopAsync(writerConnection));
    }

    public async ValueTask<IReadConnectionLease> OpenReadConnectionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _readCapacity.WaitAsync(wait.Token).ConfigureAwait(false);
        SqliteConnection? native = null;
        try
        {
            lock (_readGate)
            {
                ThrowIfDisposed();
                if (!_readPool.TryTake(out native)) native = ConnectionFactory.Open(DatabasePath, _state, _options, readOnly: true);
                var lease = new ReadLease(this, native, _options);
                _leases.TryAdd(lease, 0);
                lease.StartExpiry();
                return lease;
            }
        }
        catch
        {
            native?.Dispose();
            _readCapacity.Release();
            throw;
        }
    }

    public async ValueTask<TResult> QueueWriteAsync<TResult>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<TResult>> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ThrowIfDisposed();
        var work = new WriteWork<TResult>(write, cancellationToken);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _queue.Writer.WriteAsync(work, wait.Token).ConfigureAwait(false);
        // Once accepted, await a definitive commit/rollback result, even on cancellation.
        return await work.Completion.Task.ConfigureAwait(false);
    }

    private async Task WriterLoopAsync(SqliteConnection connection)
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                await work.ExecuteAsync(connection, _options, _shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            while (_queue.Reader.TryRead(out var pending)) pending.Fail(new ObjectDisposedException(GetType().Name));
            try
            {
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            finally { await connection.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private void Return(ReadLease lease, SqliteConnection native, BoundedConnection bounded, bool expired)
    {
        try
        {
            bounded.Detach();
            lock (_readGate)
            {
                if (expired || Volatile.Read(ref _disposed) != 0) native.Dispose();
                else _readPool.Add(native);
            }
        }
        catch
        {
            native.Dispose();
            throw;
        }
        finally
        {
            _leases.TryRemove(lease, out _);
            _readCapacity.Release();
        }
    }

    protected async ValueTask RunMaintenanceAsync(Action<SqliteConnection> maintenance, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var work = new MaintenanceWork(maintenance, cancellationToken);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _queue.Writer.WriteAsync(work, wait.Token).ConfigureAwait(false);
        await work.Completion.Task.ConfigureAwait(false);
    }

    protected void RequireNoReadLeases()
    {
        lock (_readGate)
        {
            if (!_leases.IsEmpty) throw new InvalidOperationException("Restore requires all read leases to be released.");
            while (_readPool.TryTake(out var read)) read.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _writer.ConfigureAwait(false); return; }
        _queue.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var lease in _leases.Keys) lease.Expire();
        await _writer.ConfigureAwait(false);
        lock (_readGate) while (_readPool.TryTake(out var read)) read.Dispose();
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class ReadLease : IReadConnectionLease
    {
        private readonly SqliteStore _owner;
        private readonly SqliteConnection _native;
        private readonly BoundedConnection _bounded;
        private readonly Timer _expiry;
        private int _returned;
        internal ReadLease(SqliteStore owner, SqliteConnection native, SqliteStoreOptions options)
        {
            _owner = owner;
            _native = native;
            _bounded = new BoundedConnection(native, options, options.ReadLeaseTimeout);
            _expiry = new Timer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        internal void StartExpiry() => _expiry.Change(_owner._options.ReadLeaseTimeout, Timeout.InfiniteTimeSpan);
        public DbConnection Connection { get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _returned) != 0, this); return _bounded; } }
        internal void Expire() => Return(expired: true);
        public void Dispose() => Return(expired: false);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        private void Return(bool expired)
        {
            if (Interlocked.Exchange(ref _returned, 1) != 0) return;
            _expiry.Dispose();
            _owner.Return(this, _native, _bounded, expired);
        }
    }

    private interface IWriterWork
    {
        Task ExecuteAsync(SqliteConnection connection, SqliteStoreOptions options, CancellationToken shutdown);
        void Fail(Exception exception);
    }

    private sealed class WriteWork<TResult>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<TResult>> callback, CancellationToken caller) : IWriterWork
    {
        internal TaskCompletionSource<TResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(SqliteConnection connection, SqliteStoreOptions options, CancellationToken shutdown)
        {
            Task<TResult>? invocation = null;
            BoundedConnection? bounded = null;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown);
                budget.CancelAfter(options.TransactionTimeout);
                budget.Token.ThrowIfCancellationRequested();
                using var transaction = connection.BeginTransaction();
                bounded = new BoundedConnection(connection, options, options.TransactionTimeout, transaction);
                var view = bounded;
                // Microsoft.Data.Sqlite executes SQL synchronously, so the queue's budget must run independently.
                invocation = Task.Run(async () => await callback(view, view.WriterTransaction!, budget.Token).ConfigureAwait(false), CancellationToken.None);
                try
                {
                    var result = await invocation.WaitAsync(budget.Token).ConfigureAwait(false);
                    budget.Token.ThrowIfCancellationRequested();
                    bounded.Detach();
                    transaction.Commit();
                    Completion.TrySetResult(result);
                }
                catch
                {
                    bounded.Detach();
                    transaction.Rollback();
                    throw;
                }
            }
            catch (OperationCanceledException exception)
            {
                if (caller.IsCancellationRequested) Completion.TrySetCanceled(caller);
                else if (shutdown.IsCancellationRequested) Completion.TrySetException(new ObjectDisposedException(nameof(SqliteStore)));
                else Completion.TrySetException(new TimeoutException("The SQLite writer transaction lifetime expired.", exception));
            }
            catch (Exception exception) { Completion.TrySetException(exception); }
            finally
            {
                bounded?.Detach();
                if (invocation is not null)
                    _ = invocation.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed class MaintenanceWork(Action<SqliteConnection> action, CancellationToken caller) : IWriterWork
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteAsync(SqliteConnection connection, SqliteStoreOptions options, CancellationToken shutdown)
        {
            try { caller.ThrowIfCancellationRequested(); shutdown.ThrowIfCancellationRequested(); action(connection); Completion.TrySetResult(); }
            catch (Exception exception) { Completion.TrySetException(exception); }
            return Task.CompletedTask;
        }
        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }
}
