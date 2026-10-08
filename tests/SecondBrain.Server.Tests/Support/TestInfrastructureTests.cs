using System.Data.Common;
using System.Security.Cryptography;
using Xunit;

namespace SecondBrain.Server.Tests.Support;

public sealed class TestInfrastructureTests
{
    [Fact]
    public async Task StateStoreHasOnlyLaneDTablesAndStartsAtEpochOne()
    {
        await using var store = new TestStateStore();
        await using var lease = await store.OpenReadConnectionAsync();
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["account", "credentials", "login_attempts", "meta", "passkeys", "sources", "usage"], tables);
        command.CommandText = "SELECT value FROM meta WHERE key='account_epoch'";
        Assert.Equal("1", await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA table_info(credentials)";
        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        Assert.Equal(
            ["id", "name", "kind", "verifier", "scopes", "generation", "kid", "account_epoch", "device", "idle_expires_at", "absolute_expires_at", "stepped_up_at", "created_at", "last_used_at", "expires_at", "revoked_at"],
            columns);
    }

    [Fact]
    public async Task StateStoreRollsBackFailedWorkAndSerializesConcurrentWrites()
    {
        await using var store = new TestStateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueWriteAsync<int>(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE meta SET value='999' WHERE key='account_epoch'";
            await command.ExecuteNonQueryAsync(token);
            throw new InvalidOperationException("Injected transaction failure.");
        }).AsTask());

        var writes = Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
            await store.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='account_epoch' RETURNING value";
                return (string)(await command.ExecuteScalarAsync(token))!;
            })));
        var values = await Task.WhenAll(writes);
        Assert.Equal(Enumerable.Range(2, 24), values.Select(int.Parse).Order());
        await using var lease = await store.OpenReadConnectionAsync();
        await using var read = lease.Connection.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE key='account_epoch'";
        Assert.Equal("25", await read.ExecuteScalarAsync());
    }

    [Fact]
    public void KeyRingRetainsOldKidsAndRejectsAlteredVerifiers()
    {
        var ring = new TestKeyRing();
        var secret = RandomNumberGenerator.GetBytes(32);
        var oldKid = ring.ActiveKid;
        var verifier = ring.Sign(oldKid, secret);
        Assert.NotEqual(oldKid, ring.Rotate());
        Assert.True(ring.Verify(secret, verifier));
        verifier[0] ^= 1;
        Assert.False(ring.Verify(secret, verifier));
        Assert.Equal(1, ring.SignCalls);
        Assert.Equal(2, ring.VerifyCalls);
        Assert.Equal(5, ring.HmacCalls);
        ring.ResetCounts();
        Assert.Equal(0, ring.HmacCalls);
    }

    [Fact]
    public void OptionsMonitorPublishesChangesAndRemovesSubscriptions()
    {
        var monitor = new MutableOptionsMonitor<TestOptions>(new TestOptions(1));
        var observed = new List<int>();
        using (monitor.OnChange((options, _) => observed.Add(options.Value)))
        {
            monitor.Update(new TestOptions(2));
            monitor.NotifyChanged();
        }

        monitor.Update(new TestOptions(3));
        Assert.Equal([2, 2], observed);
        Assert.Equal(3, monitor.CurrentValue.Value);
    }

    private sealed record TestOptions(int Value);
}
