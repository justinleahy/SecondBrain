using System.Data.Common;
using SecondBrain.Core.Storage;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class SqliteStoresTests
{
    [Theory]
    [InlineData(true, 2L)]
    [InlineData(false, 1L)]
    public async Task PragmasAndExtensionPolicyAreEnforced(bool state, long synchronous)
    {
        using var root = new TempRoot();
        await using SqliteStore store = state ? new SqliteStateStore(root.Path) : new SqliteIndexStore(root.Path);
        await using var lease = await store.OpenReadConnectionAsync();
        Assert.Equal("wal", await ScalarAsync(lease.Connection, "PRAGMA journal_mode;"));
        Assert.Equal(synchronous, await ScalarAsync(lease.Connection, "PRAGMA synchronous;"));
        Assert.Equal(5000L, await ScalarAsync(lease.Connection, "PRAGMA busy_timeout;"));
        Assert.Equal(1L, await ScalarAsync(lease.Connection, "PRAGMA foreign_keys;"));
        Assert.Equal(0L, await ScalarAsync(lease.Connection, "PRAGMA trusted_schema;"));
        Assert.Equal(1L, await ScalarAsync(lease.Connection, "PRAGMA query_only;"));
        Assert.Equal(1000L, await ScalarAsync(lease.Connection, "PRAGMA wal_autocheckpoint;"));
        Assert.Equal(67108864L, await ScalarAsync(lease.Connection, "PRAGMA journal_size_limit;"));
        var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ScalarAsync(lease.Connection, "SELECT load_extension('nonexistent');"));
        Assert.Contains("not authorized", error.Message);
        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            Assert.Equal(synchronous, await ScalarAsync(connection, "PRAGMA synchronous;", transaction, token));
            Assert.Equal(0L, await ScalarAsync(connection, "PRAGMA trusted_schema;", transaction, token));
            return 0;
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriterSerializationAndRollback(bool state)
    {
        using var root = new TempRoot();
        await using SqliteStore store = state ? new SqliteStateStore(root.Path) : new SqliteIndexStore(root.Path);
        await ExecuteAsync(store, "CREATE TABLE counter(value INTEGER NOT NULL); INSERT INTO counter VALUES(0);");
        var concurrent = 0;
        var maximum = 0;
        var writes = Enumerable.Range(0, 40).Select(_ => store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var active = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, active);
            try
            {
                var prior = (long)(await ScalarAsync(connection, "SELECT value FROM counter;", transaction, token))!;
                await Task.Delay(2, token);
                await NonQueryAsync(connection, transaction, "UPDATE counter SET value=value+1;", token);
                return prior;
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }).AsTask()).ToArray();
        Assert.Equal(Enumerable.Range(0, 40).Select(value => (long)value), (await Task.WhenAll(writes)).Order());
        Assert.Equal(1, maximum);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueWriteAsync<int>(async (connection, transaction, token) =>
        {
            await NonQueryAsync(connection, transaction, "UPDATE counter SET value=999;", token);
            throw new InvalidOperationException("callback failure");
        }).AsTask());
        await using var lease = await store.OpenReadConnectionAsync();
        Assert.Equal(40L, await ScalarAsync(lease.Connection, "SELECT value FROM counter;"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => NonQueryAsync(lease.Connection, null, "UPDATE counter SET value=999;"));
    }

    [Fact]
    public async Task ReadPoolIsBoundedAndDisposedLeaseCannotBeReused()
    {
        using var root = new TempRoot();
        await using var store = new SqliteStateStore(root.Path, new SqliteStoreOptions { ReadPoolSize = 1 });
        var first = await store.OpenReadConnectionAsync();
        var connection = first.Connection;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenReadConnectionAsync(cancellation.Token).AsTask());
        await first.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ScalarAsync(connection, "SELECT 1;"));
        await using var second = await store.OpenReadConnectionAsync();
        Assert.Equal(1L, await ScalarAsync(second.Connection, "SELECT 1;"));
    }

    [Fact]
    public async Task ExpiredReadLeaseReleasesSnapshotAndPoolSlot()
    {
        using var root = new TempRoot();
        await using var store = new SqliteStateStore(root.Path, new SqliteStoreOptions { ReadPoolSize = 1, ReadLeaseTimeout = TimeSpan.FromMilliseconds(100) });
        await using var first = await store.OpenReadConnectionAsync();
        var connection = first.Connection;
        using var transaction = connection.BeginTransaction();
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT 1;", transaction));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var second = await store.OpenReadConnectionAsync(limit.Token);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ScalarAsync(connection, "SELECT 1;"));
        Assert.Equal(1L, await ScalarAsync(second.Connection, "SELECT 1;"));
    }

    [Fact]
    public async Task StatementBudgetInterruptsCpuBoundSql()
    {
        using var root = new TempRoot();
        await using var store = new SqliteStateStore(root.Path, new SqliteStoreOptions { StatementTimeout = TimeSpan.FromMilliseconds(50) });
        await using var lease = await store.OpenReadConnectionAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => ScalarAsync(lease.Connection, "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n;"));
        Assert.Equal(7L, await ScalarAsync(lease.Connection, "SELECT 7;"));
    }

    [Fact]
    public async Task TimedOutWriterRollsBackAndRevokesTheCallbackConnection()
    {
        using var root = new TempRoot();
        await using var store = new SqliteStateStore(root.Path, new SqliteStoreOptions { TransactionTimeout = TimeSpan.FromMilliseconds(150) });
        await ExecuteAsync(store, "CREATE TABLE counter(value INTEGER); INSERT INTO counter VALUES(0);");
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await NonQueryAsync(connection, transaction, "UPDATE counter SET value=99;", token);
            await resume.Task;
            try { await NonQueryAsync(connection, transaction, "UPDATE counter SET value=88;"); }
            catch (Exception exception) { abandoned.SetResult(exception); }
            return 0;
        }).AsTask());
        await ExecuteAsync(store, "UPDATE counter SET value=1;");
        resume.SetResult();
        Assert.IsType<ObjectDisposedException>(await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await using var lease = await store.OpenReadConnectionAsync();
        Assert.Equal(1L, await ScalarAsync(lease.Connection, "SELECT value FROM counter;"));
    }

    [Fact]
    public async Task CancellationRollsBackAcceptedWriteAndWriterOwnsCommit()
    {
        using var root = new TempRoot();
        await using var store = new SqliteStateStore(root.Path);
        await ExecuteAsync(store, "CREATE TABLE counter(value INTEGER); INSERT INTO counter VALUES(0);");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var write = store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await NonQueryAsync(connection, transaction, "UPDATE counter SET value=9;", token);
            Assert.Throws<NotSupportedException>(transaction.Commit);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }, cancellation.Token).AsTask();
        await entered.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        await using var lease = await store.OpenReadConnectionAsync();
        Assert.Equal(0L, await ScalarAsync(lease.Connection, "SELECT value FROM counter;"));
    }

    [Fact]
    public async Task LeaseReturnClosesUnfinishedReaderAndTransactionBeforeReuse()
    {
        using var root = new TempRoot();
        await using var store = new SqliteStateStore(root.Path, new SqliteStoreOptions { ReadPoolSize = 1 });
        var first = await store.OpenReadConnectionAsync();
        var firstTransaction = first.Connection.BeginTransaction();
        using var command = first.Connection.CreateCommand();
        command.Transaction = firstTransaction;
        command.CommandText = "SELECT 1 UNION ALL SELECT 2;";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        await first.DisposeAsync();
        await using var second = await store.OpenReadConnectionAsync();
        using var secondTransaction = second.Connection.BeginTransaction();
        Assert.Equal(3L, await ScalarAsync(second.Connection, "SELECT 3;", secondTransaction));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadAsync());
        Assert.Throws<ObjectDisposedException>(firstTransaction.Commit);
        firstTransaction.Dispose();
    }

    [Fact]
    public async Task InterleavedCommandCannotExtendReaderStatementDeadline()
    {
        using var root = new TempRoot();
        await using var store = new SqliteStateStore(root.Path, new SqliteStoreOptions { StatementTimeout = TimeSpan.FromMilliseconds(50) });
        await using var lease = await store.OpenReadConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT 1 UNION ALL SELECT 2;";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        await Task.Delay(80);
        Assert.Equal(3L, await ScalarAsync(lease.Connection, "SELECT 3;"));
        await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task FullWriterQueueBackpressuresAndCanceledWaiterDoesNotRun()
    {
        using var root = new TempRoot();
        await using var store = new SqliteStateStore(root.Path, new SqliteStoreOptions { WriterQueueCapacity = 1 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = store.QueueWriteAsync(async (_, _, token) => { entered.SetResult(); await resume.Task.WaitAsync(token); return 1; }).AsTask();
        await entered.Task;
        var queued = store.QueueWriteAsync((_, _, _) => ValueTask.FromResult(2)).AsTask();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.QueueWriteAsync((_, _, _) => { ran = true; return ValueTask.FromResult(3); }, cancellation.Token).AsTask());
        Assert.False(ran);
        resume.SetResult();
        Assert.Equal(1, await active);
        Assert.Equal(2, await queued);
        Assert.False(ran);
    }

    private static async Task ExecuteAsync(IStoreHandle store, string sql) => await store.QueueWriteAsync(async (connection, transaction, token) => await NonQueryAsync(connection, transaction, sql, token));
    private static async Task<int> NonQueryAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken token = default)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(token);
    }
    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, DbTransaction? transaction = null, CancellationToken token = default)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(token);
    }
    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "secondbrain-stores-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
