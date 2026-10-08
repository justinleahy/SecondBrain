using System.Data.Common;

namespace SecondBrain.Core.Storage;

/// <summary>
/// A store with a bounded read pool and a single queued writer; implementations
/// own connections, transactions, queue bounds and timeouts (spec §§5.1, 15.11, SEC-29).
/// </summary>
public interface IStoreHandle
{
    /// <summary>
    /// Leases an open read connection. The caller must promptly dispose the lease
    /// to return it to the pool and must not use the connection after disposal (§5.1).
    /// </summary>
    ValueTask<IReadConnectionLease> OpenReadConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues bounded database-only work on the store's sole writer and returns
    /// after commit. The store supplies and owns the connection and transaction;
    /// callbacks must not commit, roll back, dispose, or retain either. Callbacks
    /// must not perform external side effects, network calls or unrelated/unbounded
    /// waits. A callback failure rolls back and propagates to the caller (§15.11, SEC-29).
    /// </summary>
    ValueTask<TResult> QueueWriteAsync<TResult>(
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask<TResult>> write,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An open read connection lease; disposal returns its resources to the owning
/// store's bounded pool (spec §§5.1, 15.11, SEC-29).
/// </summary>
public interface IReadConnectionLease : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets the leased connection. Dispose the lease, rather than the underlying
    /// connection; query snapshots and transaction lifetimes remain bounded (§5.1, SEC-29).
    /// </summary>
    DbConnection Connection { get; }
}
