using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Sources;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class CalendarSubscriptionPublicationTests
{
    private static readonly IngestionAuthority Admin = new("calendar-publication", 1, 1, "admin", "session");
    private static readonly CalendarWindow Window = new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", "UTC");

    [Fact]
    public async Task AcceptedSnapshotAndValidatorsCommitTogetherAndUnchangedBytesDoNotReviseDocuments()
    {
        await using var fixture = await Fixture.Create();
        var first = await fixture.Next("one", Snapshot("Planning"), "\"first\"");
        var id = (string)(await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT document_id FROM calendar_members"))!;
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(fixture.Stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Equal(first.SnapshotPath, (string)(await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT snapshot_path FROM calendar_subscriptions"))!);
        Assert.Equal("current", (await fixture.Sources.GetAsync(fixture.Source.Id))!.Freshness);
        var changedAt = (await fixture.Sources.GetAsync(fixture.Source.Id))!.LastChangeAt;
        fixture.Clock.Now += TimeSpan.FromMinutes(10);
        var same = await fixture.Next("one", Snapshot("Planning"), "\"same-bytes\"");
        Assert.Equal(first.SnapshotPath, same.SnapshotPath);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT MAX(revision) FROM documents"));
        Assert.Equal(changedAt, (await fixture.Sources.GetAsync(fixture.Source.Id))!.LastChangeAt);
        Assert.Equal("indexed", (await fixture.Documents.GetAsync(fixture.Source.ContainerId!))!.Status);
        Assert.Equal("\"same-bytes\"", (string)(await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT etag FROM calendar_subscriptions"))!);
        await fixture.Next("two", Snapshot("Updated"), "\"changed\"");
        Assert.Equal(id, (string)(await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT document_id FROM calendar_members"))!);
        Assert.Equal("Updated", (await fixture.Documents.GetAsync(id))!.Title);
        Assert.Equal("one", await File.ReadAllTextAsync(first.SnapshotPath));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT COUNT(*) FROM calendar_subscription_publications"));
    }

    [Fact]
    public async Task PausedDownloadCannotPublishAndRemovalHidesEveryMember()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Next("accepted", Snapshot("Accepted"));
        var work = await fixture.Claim();
        var pending = await fixture.Prepare(work, "not accepted", Snapshot("Not accepted"));
        await fixture.Sources.UpdateAsync(fixture.Source.Id, null, null, false, Admin);
        Assert.Equal(PublicationOutcome.Superseded, await fixture.Publications.PublishCalendarSubscriptionAsync(pending));
        Assert.Equal("Accepted", (string)(await MigrationTestSql.ScalarAsync(fixture.Stores.Index, "SELECT title FROM indexed_documents WHERE type='calendar_event'"))!);
        Assert.Equal("paused", (await fixture.Sources.GetAsync(fixture.Source.Id))!.Freshness);
        await fixture.Sources.RemoveAsync(fixture.Source.Id, Admin);
        Assert.Null(await fixture.Sources.GetAsync(fixture.Source.Id));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(fixture.Stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Empty(await fixture.Sources.ClaimDueAsync());
    }

    [Theory]
    [InlineData(CrashPointNames.AfterStatePublishing, false)]
    [InlineData(CrashPointNames.AfterIndexCommit, false)]
    [InlineData(CrashPointNames.AfterStatePublishing, true)]
    [InlineData(CrashPointNames.AfterIndexCommit, true)]
    public async Task RecoveryFinishesAcceptedBatchBeforeAcknowledgingPause(string point, bool configured)
    {
        await using var fixture = await Fixture.Create();
        if (configured) await MigrationTestSql.WriteAsync(fixture.Stores.State, "UPDATE sources SET config_json=json_set(config_json,'$.configured',1)");
        await fixture.Next("original", Snapshot("Original"), "\"old\"");
        var work = await fixture.Claim(); var pending = await fixture.Prepare(work, "changed", Snapshot("Changed"), "\"new\"");
        var crashing = new PublicationCoordinator(fixture.Stores.State, fixture.Stores.Index, new Crash(point), clock: fixture.Clock);
        await Assert.ThrowsAsync<CrashFailure>(async () => await crashing.PublishCalendarSubscriptionAsync(pending));
        Assert.Equal("\"old\"", (string)(await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT etag FROM calendar_subscriptions"))!);
        await fixture.Sources.FailAsync(work, "calendar_publication_failed");
        Assert.True(await fixture.Sources.IsCurrentAsync(work.SourceId, work.Generation));
        Assert.Empty(await fixture.Sources.ClaimDueAsync());
        if (configured)
            await fixture.Sources.ReconcileConfiguredAsync([new(fixture.Source.Id, fixture.Source.Name, fixture.Source.Origin, 3600, false, "protected-envelope", "protected-envelope")]);
        else await fixture.Sources.UpdateAsync(fixture.Source.Id, null, null, false, Admin);
        Assert.Equal("paused", (await fixture.Sources.GetAsync(fixture.Source.Id))!.Freshness);
        Assert.Equal("\"new\"", (string)(await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT etag FROM calendar_subscriptions"))!);
        Assert.Equal("Changed", (string)(await MigrationTestSql.ScalarAsync(fixture.Stores.Index, "SELECT title FROM indexed_documents WHERE type='calendar_event'"))!);
        Assert.False(await fixture.Sources.IsCurrentAsync(work.SourceId, work.Generation));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT COUNT(*) FROM calendar_batches WHERE phase='publishing'"));
    }

    [Fact]
    public async Task PausedCalendarAdvancesItsCoverageWithoutClaimingAFreshRemoteCheck()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Next("accepted", Snapshot("Accepted"));
        var success = (await fixture.Sources.GetAsync(fixture.Source.Id))!.LastSuccessAt;
        await fixture.Sources.UpdateAsync(fixture.Source.Id, null, null, false, Admin);
        fixture.Clock.Now += TimeSpan.FromDays(1);
        var nextWindow = Window with { From = "2026-10-02T00:00:00Z", To = "2026-11-02T00:00:00Z" };
        var work = Assert.Single(await fixture.Sources.ClaimMaterializationAsync(nextWindow));
        Assert.True(work.MaterializationOnly);
        var pending = await fixture.Prepare(work, "accepted", Snapshot("Accepted") with { Window = nextWindow });
        Assert.Equal(PublicationOutcome.Published, await fixture.Publications.PublishCalendarSubscriptionAsync(pending));
        Assert.Equal(success, (await fixture.Sources.GetAsync(fixture.Source.Id))!.LastSuccessAt);
        Assert.Equal("paused", (await fixture.Sources.GetAsync(fixture.Source.Id))!.Freshness);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT MAX(revision) FROM documents"));
        Assert.Empty(await fixture.Sources.ClaimMaterializationAsync(nextWindow));
    }

    [Fact]
    public async Task ValidEmptySnapshotIsAuthoritativeAndUnjournaledBytesAreRejected()
    {
        await using var fixture = await Fixture.Create(); await fixture.Next("accepted", Snapshot("Accepted"));
        var work = await fixture.Claim();
        var unowned = new CalendarSubscriptionPublication(work, Snapshot("Bad"), "/tmp/not-owned.ics", new string('a', 64), 12, null, null);
        await Assert.ThrowsAsync<CalendarSubscriptionException>(async () => await fixture.Publications.PublishCalendarSubscriptionAsync(unowned));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(fixture.Stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        var empty = await fixture.Prepare(work, "empty", new(Window, [], []));
        await fixture.Publications.PublishCalendarSubscriptionAsync(empty);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(fixture.Stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT COUNT(*) FROM absences WHERE cleared_at IS NULL"));
    }

    private static CalendarSnapshot Snapshot(string title)
    {
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"uid":"series","recurrence_id":"","starts_at":"2026-10-04T10:00:00Z","ends_at":"2026-10-04T11:00:00Z","status":"confirmed","all_day":false}""")!;
        return new(Window, [new("series", "", title, title, properties)], [new("series", "", "2026-10-04T10:00:00Z", "2026-10-04T11:00:00Z", "confirmed")]);
    }
    private sealed class Clock : TimeProvider { internal DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T12:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class CrashFailure : Exception;
    private sealed class Crash(string point) : ICrashPoints { public ValueTask HitAsync(string value, CancellationToken token = default) => value == point ? throw new CrashFailure() : ValueTask.CompletedTask; }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root = new();
        internal MigrationTestStores Stores { get; }
        internal Clock Clock { get; } = new();
        internal PublicationCoordinator Publications { get; }
        internal SqliteCalendarSubscriptionRepository Sources { get; }
        internal SqliteDocumentRepository Documents { get; }
        internal CalendarSubscription Source { get; private set; } = null!;
        private readonly MutationJournal journal; private readonly ManagedDocumentFiles files;
        private Fixture()
        {
            Stores = new(root.Path); Publications = new(Stores.State, Stores.Index, clock: Clock);
            Sources = new(Stores.State, Clock, publications: Publications);
            journal = new(Stores.State, root.Path); files = new(root.Path, journal);
            Documents = new(Stores.State, Stores.Index, Publications, files);
        }
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture(); await fixture.Stores.Runner.MigrateAsync();
            await MigrationTestSql.WriteAsync(fixture.Stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
            await new AuthRepository(fixture.Stores.State, fixture.Clock).AddAsync(new CredentialRecord { Id = Admin.CredentialId, Name = "admin", Kind = "session", Generation = 1, AccountEpoch = 1, Scopes = "read,write,admin", Verifier = "fixture", Kid = "fixture", CreatedAt = AuthTime.Format(fixture.Clock.Now) });
            fixture.Source = await fixture.Sources.CreateAsync("Fixture", "https://feed.test", "protected-envelope", 3600, true, Admin);
            return fixture;
        }
        internal async Task<CalendarRefreshWork> Claim()
        { await Sources.QueueAsync(Source.Id, false, Admin); return Assert.Single(await Sources.ClaimDueAsync()); }
        internal async Task<CalendarSubscriptionPublication> Prepare(CalendarRefreshWork work, string text, CalendarSnapshot snapshot, string? etag = null)
        {
            var bytes = Encoding.UTF8.GetBytes(text); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var path = hash == work.SnapshotHash && work.SnapshotPath is not null ? work.SnapshotPath : await files.WriteCalendarSnapshotAsync(work.ContainerId, work.LeaseId, bytes);
            return new(work, snapshot, path, hash, bytes.Length, etag, null);
        }
        internal async Task<CalendarSubscriptionPublication> Next(string text, CalendarSnapshot snapshot, string? etag = null)
        {
            var pending = await Prepare(await Claim(), text, snapshot, etag);
            Assert.Equal(PublicationOutcome.Published, await Publications.PublishCalendarSubscriptionAsync(pending));
            return pending;
        }
        public async ValueTask DisposeAsync() { files.Dispose(); journal.Dispose(); await Stores.DisposeAsync(); root.Dispose(); }
    }
}
