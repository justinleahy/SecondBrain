using System.Text.Json;
using System.Globalization;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Sources;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class CalendarSubscriptionTests
{
    private static readonly IngestionAuthority Admin = new("subscription-admin", 1, 1, "admin", "session");
    [Fact]
    public async Task RegistrationQueuesInitialRefreshAndListingsNeverContainProtectedSettings()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); var clock = new Clock(); await Seed(stores, clock);
        var repository = new SqliteCalendarSubscriptionRepository(stores.State, clock);
        var created = await repository.CreateAsync("Shared calendar", "https://feed.test", "protected-envelope", 3600, true, Admin);
        Assert.Equal("never_synced", created.Freshness); Assert.NotNull(created.NextAttemptAt);
        var work = Assert.Single(await repository.ClaimDueAsync());
        Assert.Equal(created.ContainerId, work.ContainerId); Assert.Equal("protected-envelope", work.ProtectedUrl);
        Assert.True(await repository.IsCurrentAsync(work.SourceId, work.Generation));
        Assert.Empty(await repository.ClaimDueAsync());
        Assert.DoesNotContain("protected-envelope", JsonSerializer.Serialize(await repository.ListAsync()));
        Assert.DoesNotContain("protected-envelope", JsonSerializer.Serialize(work));
        Assert.DoesNotContain("protected-envelope", work.ToString());
        Assert.DoesNotContain("protected-envelope", (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT config_json FROM sources WHERE kind='calendar_subscription'"))!);
    }

    [Fact]
    public async Task PauseInvalidatesInFlightWorkAndResumeKeepsContainerIdentity()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); var clock = new Clock(); await Seed(stores, clock);
        var repository = new SqliteCalendarSubscriptionRepository(stores.State, clock);
        var created = await repository.CreateAsync("Shared calendar", "https://feed.test", "protected-envelope", 3600, true, Admin);
        var first = Assert.Single(await repository.ClaimDueAsync());
        await repository.UpdateAsync(created.Id, null, null, false, Admin);
        Assert.False(await repository.IsCurrentAsync(first.SourceId, first.Generation));
        Assert.Equal("paused", (await repository.GetAsync(created.Id))!.Freshness);
        await Assert.ThrowsAsync<CalendarSubscriptionException>(async () => await repository.QueueAsync(created.Id, false, Admin));
        Assert.Empty(await repository.ClaimDueAsync());
        await repository.UpdateAsync(created.Id, "Renamed", 7200, true, Admin);
        var next = Assert.Single(await repository.ClaimDueAsync());
        Assert.Equal(first.ContainerId, next.ContainerId); Assert.True(next.Generation > first.Generation);
        await repository.FailAsync(first, "calendar_timeout");
        Assert.Null((await repository.GetAsync(created.Id))!.Error);
        Assert.Equal("Renamed", (await repository.GetAsync(created.Id))!.Name);
    }

    [Fact]
    public async Task RepeatedTriggersCoalesceAndFailurePreservesAcceptedValidatorsAndChangeTime()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); var clock = new Clock(); await Seed(stores, clock);
        var repository = new SqliteCalendarSubscriptionRepository(stores.State, clock);
        var created = await repository.CreateAsync("Shared calendar", "https://feed.test", "protected-envelope", 3600, true, Admin);
        await Accepted(stores, created.Id, clock.GetUtcNow());
        var first = Assert.Single(await repository.ClaimDueAsync());
        await repository.QueueAsync(created.Id, false, Admin); await repository.QueueAsync(created.Id, true, Admin);
        Assert.Empty(await repository.ClaimDueAsync());
        await repository.FailAsync(first, "calendar_parse_failed", rejectedHash: new string('b', 64), processingGeneration: "calendar:1");
        Assert.Equal("failed", (await repository.GetAsync(created.Id))!.Freshness);
        var second = Assert.Single(await repository.ClaimDueAsync());
        Assert.True(second.Deep); Assert.Null(second.ETag); Assert.Equal(new string('a', 64), second.SnapshotHash);
        Assert.Equal(new string('b', 64), second.RejectedHash); Assert.Equal("calendar:1", second.RejectedGeneration);
        await repository.CompleteUnchangedAsync(second);
        Assert.Equal("current", (await repository.GetAsync(created.Id))!.Freshness);
        Assert.Equal(clock.GetUtcNow(), (await repository.GetAsync(created.Id))!.LastChangeAt);
        Assert.Equal("\"accepted\"", (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT etag FROM calendar_subscriptions"))!);
        Assert.Empty(await repository.ClaimDueAsync());
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal("stale", (await repository.GetAsync(created.Id))!.Freshness);
    }

    [Fact]
    public async Task RestartReclaimsOnlyExpiredLeaseAndRetainsSourceAndContainer()
    {
        using var root = new MigrationTestDataRoot(); var clock = new Clock(); string id; CalendarRefreshWork first;
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await Seed(stores, clock); var repository = new SqliteCalendarSubscriptionRepository(stores.State, clock);
            id = (await repository.CreateAsync("Shared calendar", "https://feed.test", "protected-envelope", 3600, true, Admin)).Id;
            first = Assert.Single(await repository.ClaimDueAsync());
        }
        await using var restarted = new MigrationTestStores(root.Path); await restarted.Runner.MigrateAsync();
        var current = new SqliteCalendarSubscriptionRepository(restarted.State, clock);
        Assert.Empty(await current.ClaimDueAsync());
        clock.Advance(TimeSpan.FromMinutes(5));
        var recovered = Assert.Single(await current.ClaimDueAsync());
        Assert.Equal(id, recovered.SourceId); Assert.Equal(first.ContainerId, recovered.ContainerId);
        Assert.NotEqual(first.LeaseId, recovered.LeaseId); Assert.True(recovered.Generation > first.Generation);
        Assert.False(await current.IsCurrentAsync(first.SourceId, first.Generation));
    }

    [Fact]
    public async Task RevocationBackoffAndRetryAfterAreBoundedAndNonAdminsCannotRegister()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); var clock = new Clock(); await Seed(stores, clock);
        var repository = new SqliteCalendarSubscriptionRepository(stores.State, clock);
        await Assert.ThrowsAnyAsync<Exception>(async () => await repository.CreateAsync("Denied", "https://feed.test", "protected-envelope", 3600, true, Admin with { CredentialId = "reader" }));
        Assert.Empty(await repository.ListAsync());
        var created = await repository.CreateAsync("Shared calendar", "https://feed.test", "protected-envelope", 3600, true, Admin);
        var work = Assert.Single(await repository.ClaimDueAsync());
        await repository.FailAsync(work, "calendar_unavailable", retryAfterSeconds: 30);
        Assert.True((await repository.GetAsync(created.Id))!.NextAttemptAt >= clock.GetUtcNow().AddHours(1));
        clock.Advance(TimeSpan.FromDays(1)); work = Assert.Single(await repository.ClaimDueAsync());
        await repository.FailAsync(work, "calendar_rate_limited", retryAfterSeconds: int.MaxValue);
        Assert.Equal(clock.GetUtcNow().AddDays(1), (await repository.GetAsync(created.Id))!.NextAttemptAt);
    }

    private static async Task Seed(MigrationTestStores stores, Clock clock)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        var auth = new AuthRepository(stores.State, clock);
        foreach (var (id, scopes) in new[] { (Admin.CredentialId, "read,write,infer,admin"), ("reader", "read") })
            await auth.AddAsync(new CredentialRecord { Id = id, Name = id, Kind = "session", Generation = 1, AccountEpoch = 1, Scopes = scopes, Verifier = "fixture", Kid = "fixture", CreatedAt = AuthTime.Format(clock.GetUtcNow()) });
    }
    private static Task Accepted(MigrationTestStores stores, string id, DateTimeOffset now) => MigrationTestSql.WriteAsync(stores.State,
        $"UPDATE calendar_subscriptions SET snapshot_hash='{new string('a', 64)}',snapshot_path='/fixture/snapshot.ics',etag='\"accepted\"',last_success_at='{now:O}',last_change_at='{now:O}' WHERE source_id='{id}'");
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-24T12:00:00Z", CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan value) => now += value;
    }
}
