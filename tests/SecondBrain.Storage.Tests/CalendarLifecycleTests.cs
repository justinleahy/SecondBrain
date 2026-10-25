using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
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

public sealed class CalendarLifecycleTests
{
    private static readonly IngestionAuthority Admin = new("calendar-lifecycle", 1, 1, "admin", "session");
    [Fact]
    public async Task DeleteAndRestoreMoveSnapshotPauseFetchingAndPreserveIndependentMemberDeletion()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Refresh("private-calendar-bytes-one");
        var children = await fixture.Members();
        await fixture.Mutate(children[0], "delete");
        var work = await fixture.Claim();
        await fixture.Mutate(fixture.RootId, "delete");
        Assert.False(await fixture.Sources.IsCurrentAsync(work.SourceId, work.Generation));
        Assert.Equal("paused", (await fixture.Sources.GetAsync(fixture.Source.Id))!.Freshness);
        await Assert.ThrowsAsync<CalendarSubscriptionException>(async () => await fixture.Sources.UpdateAsync(fixture.Source.Id, null, null, true, Admin));
        Assert.Empty(await fixture.Sources.ClaimDueAsync());
        Assert.Empty(await fixture.Sources.ClaimMaterializationAsync(Window));
        Assert.Equal(0L, await fixture.Scalar(true, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Equal(0L, await fixture.Scalar(true, "SELECT COUNT(*) FROM indexed_calendar_coverage"));
        var trashed = (await fixture.Documents.GetAsync(fixture.RootId, true))!;
        Assert.Contains(Path.DirectorySeparatorChar + "trash" + Path.DirectorySeparatorChar, trashed.Path!);
        Assert.True(File.Exists(trashed.Path));
        await fixture.Mutate(fixture.RootId, "restore");
        Assert.Equal("deleted", (await fixture.Documents.GetAsync(children[0]))!.Status);
        Assert.Equal(1L, await fixture.Scalar(true, "SELECT COUNT(*) FROM event_occurrences"));
        var restored = (await fixture.Documents.GetAsync(fixture.RootId, true))!;
        Assert.Equal("private-calendar-bytes-one", await File.ReadAllTextAsync(restored.Path!));
        Assert.Equal(restored.Path, await fixture.Scalar(false, "SELECT snapshot_path FROM calendar_subscriptions"));
        Assert.Equal("paused", (await fixture.Sources.GetAsync(fixture.Source.Id))!.Freshness);
        await fixture.Sources.UpdateAsync(fixture.Source.Id, null, null, true, Admin);
        Assert.Single(await fixture.Sources.ClaimDueAsync());
    }

    [Fact]
    public async Task ConfiguredRestartKeepsLifecyclePauseUntilExplicitResumeAfterRestore()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Refresh("private-calendar-bytes-one");
        await MigrationTestSql.WriteAsync(fixture.Stores.State, "UPDATE sources SET config_json=json_set(config_json,'$.configured',1)");
        var registration = new ConfiguredCalendarRegistration(fixture.Source.Id, "Configured", "https://feed.test", 3600, true, "protected-envelope", "protected-envelope");
        await fixture.Mutate(fixture.RootId, "delete");
        var restarted = new SqliteCalendarSubscriptionRepository(fixture.Stores.State, TimeProvider.System, publications: fixture.Publications);
        await restarted.ReconcileConfiguredAsync([registration]);
        Assert.Equal("paused", (await restarted.GetAsync(fixture.Source.Id))!.Freshness);
        Assert.Empty(await restarted.ClaimDueAsync());
        await fixture.Mutate(fixture.RootId, "restore");
        await restarted.UpdateAsync(fixture.Source.Id, "Edited", null, null, Admin);
        await restarted.ReconcileConfiguredAsync([registration]);
        Assert.Equal("Configured", (await restarted.GetAsync(fixture.Source.Id))!.Name);
        Assert.Equal("paused", (await restarted.GetAsync(fixture.Source.Id))!.Freshness);
        Assert.Empty(await restarted.ClaimDueAsync());
        await restarted.UpdateAsync(fixture.Source.Id, null, null, true, Admin);
        await restarted.ReconcileConfiguredAsync([registration]);
        Assert.Equal("active", (await restarted.GetAsync(fixture.Source.Id))!.Status);
        Assert.Single(await restarted.ClaimDueAsync());
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM calendar_lifecycle_pauses"));
    }

    [Fact]
    public async Task SourceRemovalPublishesNewRevisionsAsOneGroupAndIsIdempotent()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Refresh("private-calendar-bytes-one");
        var before = Convert.ToInt64(await fixture.Scalar(false, "SELECT SUM(revision) FROM documents"));
        await fixture.Sources.RemoveAsync(fixture.Source.Id, Admin);
        Assert.Equal(before + 3, await fixture.Scalar(false, "SELECT SUM(revision) FROM documents"));
        Assert.Equal(3L, await fixture.Scalar(false, "SELECT COUNT(*) FROM documents WHERE status='removed_from_source' AND indexed_revision=revision AND publish_state='published'"));
        Assert.Equal(0L, await fixture.Scalar(true, "SELECT COUNT(*) FROM indexed_documents"));
        Assert.Equal(0L, await fixture.Scalar(true, "SELECT COUNT(*) FROM indexed_calendar_coverage"));
        await fixture.Sources.RemoveAsync(fixture.Source.Id, Admin);
        Assert.Equal(before + 3, await fixture.Scalar(false, "SELECT SUM(revision) FROM documents"));
    }

    [Fact]
    public async Task SourcePurgeErasesMoreThanOneThousandSnapshotsAndPreservesOtherFiles()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Refresh("private-calendar-bytes-one");
        var folder = Path.Combine(fixture.RootPath, "vault", "originals");
        for (var i = 0; i < 1100; i++) await File.WriteAllTextAsync(Path.Combine(folder, fixture.RootId + ".history" + i + ".ics"), "retained-private-calendar");
        var unrelated = Path.Combine(folder, "unrelated.ics"); await File.WriteAllTextAsync(unrelated, "keep");
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "vault", ".brainignore"), "*");
        var hidden = Path.Combine(fixture.RootPath, "trash", ".claimed"); Directory.CreateDirectory(hidden);
        var hiddenOriginal = Path.Combine(hidden, fixture.RootId + ".old.ics"); await File.WriteAllTextAsync(hiddenOriginal, "private-hidden-original");
        await fixture.Sources.PurgeAsync(fixture.Source.Id, Admin);
        Assert.Empty(Directory.EnumerateFiles(folder, fixture.RootId + ".*"));
        Assert.False(File.Exists(hiddenOriginal));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(3L, await fixture.Scalar(false, "SELECT COUNT(*) FROM documents WHERE status='purged'"));
        Assert.Equal(0L, await fixture.Scalar(true, "SELECT COUNT(*) FROM indexed_documents"));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'private-')>0"));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM calendar_subscriptions WHERE protected_url<>'' OR snapshot_path IS NOT NULL"));
        var revisions = await fixture.Scalar(false, "SELECT SUM(revision) FROM documents");
        await fixture.Sources.PurgeAsync(fixture.Source.Id, Admin);
        Assert.Equal(revisions, await fixture.Scalar(false, "SELECT SUM(revision) FROM documents"));
        Assert.Equal(0, await fixture.Sources.RecoverPurgesAsync());
    }

    [Fact]
    public async Task SourceWithoutAcceptedContainerPurgesItsCandidateAndProtectedLink()
    {
        await using var fixture = await Fixture.Create();
        var work = await fixture.Claim();
        var candidate = await fixture.WriteCandidate(work, "private-unaccepted-bytes");
        await fixture.Sources.PurgeAsync(fixture.Source.Id, Admin);
        Assert.False(File.Exists(candidate));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM documents"));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM payloads WHERE length(bytes)>0"));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM calendar_subscriptions WHERE protected_url<>''"));
        Assert.False(await fixture.Sources.IsCurrentAsync(work.SourceId, work.Generation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteCandidate(work, "late-private-snapshot"));
    }

    [Theory]
    [InlineData(MutationCrashPointNames.AfterSourceQuarantine, 1)]
    [InlineData(CrashPointNames.BeforeFinalize, 1)]
    [InlineData(CrashPointNames.BeforeFinalize, 2)]
    public async Task SourcePurgeRecoversInterruptedFileBatchesAndFinalization(string point, int occurrence)
    {
        await using var fixture = await Fixture.Create();
        var original = await fixture.Refresh("private-calendar-bytes-one");
        using var crashingJournal = new MutationJournal(fixture.Stores.State, fixture.RootPath, new NthCrash(point, occurrence));
        using var crashingFiles = new ManagedDocumentFiles(fixture.RootPath, crashingJournal);
        var interrupted = new SqliteCalendarSubscriptionRepository(fixture.Stores.State, TimeProvider.System, publications: fixture.Publications, files: crashingFiles);
        await Assert.ThrowsAsync<CrashFailure>(async () => await interrupted.PurgeAsync(fixture.Source.Id, Admin));
        Assert.Null(await fixture.Sources.GetAsync(fixture.Source.Id));
        Assert.Equal(1L, await fixture.Scalar(false, "SELECT COUNT(*) FROM calendar_source_purges WHERE completed_at IS NULL"));
        Assert.Equal(1, await fixture.Sources.RecoverPurgesAsync());
        Assert.False(File.Exists(original));
        Assert.Equal(3L, await fixture.Scalar(false, "SELECT COUNT(*) FROM documents WHERE status='purged'"));
        Assert.Equal(0L, await fixture.Scalar(true, "SELECT COUNT(*) FROM indexed_documents"));
        Assert.Equal(0, await fixture.Sources.RecoverPurgesAsync());
    }

    [Fact]
    public async Task UnreadableOwnedFileKeepsDurablePurgePendingUntilItIsRepaired()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Refresh("private-calendar-bytes-one");
        var external = Path.Combine(fixture.RootPath, "unrelated-private.txt");
        await File.WriteAllTextAsync(external, "keep external content");
        var link = Path.Combine(fixture.RootPath, "vault", "originals", fixture.RootId + ".blocked.ics");
        File.CreateSymbolicLink(link, external);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Sources.PurgeAsync(fixture.Source.Id, Admin));
        Assert.Null(await fixture.Sources.GetAsync(fixture.Source.Id));
        Assert.Equal("calendar_purge_pending", await fixture.Scalar(false, "SELECT error_code FROM calendar_source_purges"));
        Assert.Equal("keep external content", await File.ReadAllTextAsync(external));
        File.Delete(link);
        Assert.Equal(1, await fixture.Sources.RecoverPurgesAsync());
        Assert.Equal("keep external content", await File.ReadAllTextAsync(external));
        Assert.Equal(1L, await fixture.Scalar(false, "SELECT COUNT(*) FROM calendar_source_purges WHERE completed_at IS NOT NULL AND error_code IS NULL"));
    }

    [Fact]
    public async Task PurgeIntentSurvivesRemovalPublicationCrashAndCredentialRevocation()
    {
        await using var fixture = await Fixture.Create();
        var original = await fixture.Refresh("private-calendar-bytes-one");
        var publications = new PublicationCoordinator(fixture.Stores.State, fixture.Stores.Index, new Crash(CrashPointNames.AfterIndexCommit));
        using var files = new ManagedDocumentFiles(fixture.RootPath, fixture.Journal);
        var interrupted = new SqliteCalendarSubscriptionRepository(fixture.Stores.State, TimeProvider.System, publications: publications, files: files);
        await Assert.ThrowsAsync<CrashFailure>(async () => await interrupted.PurgeAsync(fixture.Source.Id, Admin));
        await MigrationTestSql.WriteAsync(fixture.Stores.State, "UPDATE credentials SET generation=generation+1");
        Assert.Equal(1, await fixture.Sources.RecoverPurgesAsync());
        Assert.False(File.Exists(original));
        Assert.Equal(3L, await fixture.Scalar(false, "SELECT COUNT(*) FROM documents WHERE status='purged'"));
    }

    [Fact]
    public async Task InterruptedNeverAcceptedSnapshotCannotReappearAfterSourcePurge()
    {
        await using var fixture = await Fixture.Create();
        var work = await fixture.Claim();
        using var crashingJournal = new MutationJournal(fixture.Stores.State, fixture.RootPath, new Crash(CrashPointNames.BeforeFinalize));
        using var crashingFiles = new ManagedDocumentFiles(fixture.RootPath, crashingJournal);
        await Assert.ThrowsAsync<CrashFailure>(async () => await crashingFiles.WriteCalendarSnapshotAsync(fixture.RootId, work.LeaseId, Encoding.UTF8.GetBytes("private-interrupted-snapshot")));
        await fixture.Sources.PurgeAsync(fixture.Source.Id, Admin);
        await fixture.Journal.RecoverAsync();
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.RootPath, "vault", "originals"), fixture.RootId + ".*"));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'private-interrupted-snapshot')>0"));
    }

    [Fact]
    public async Task MemberPurgeRetainsOwnedSnapshotsButContainerPurgeErasesTheirLineage()
    {
        await using var fixture = await Fixture.Create();
        var first = await fixture.Refresh("private-calendar-bytes-one");
        var second = await fixture.Refresh("private-calendar-bytes-two");
        var children = await fixture.Members();
        await fixture.Mutate(children[0], "purge");
        Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
        Assert.Contains("private-calendar-bytes", await File.ReadAllTextAsync(second));
        await fixture.Mutate(fixture.RootId, "purge");
        Assert.False(File.Exists(first)); Assert.False(File.Exists(second));
        Assert.Null(await fixture.Sources.GetAsync(fixture.Source.Id));
        Assert.Empty(await fixture.Sources.ClaimDueAsync());
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM documents WHERE status<>'purged'"));
        Assert.Equal(0L, await fixture.Scalar(true, "SELECT COUNT(*) FROM indexed_documents"));
        Assert.Equal(0L, await fixture.Scalar(true, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'private-calendar-bytes')>0 OR instr(CAST(bytes AS TEXT),'private-event-content')>0"));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM meta WHERE key LIKE 'purge_cleanup:%'"));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM calendar_subscriptions WHERE snapshot_path IS NOT NULL"));
    }

    [Theory]
    [InlineData(CrashPointNames.BeforeFinalize)]
    [InlineData(CrashPointNames.AfterIndexCommit)]
    public async Task InterruptedContainerPurgeRecoversAllMembersExactlyOnce(string point)
    {
        await using var fixture = await Fixture.Create();
        var original = await fixture.Refresh("private-calendar-bytes-one");
        using var crashJournal = new MutationJournal(fixture.Stores.State, fixture.RootPath, new Crash(point));
        using var crashFiles = new ManagedDocumentFiles(fixture.RootPath, crashJournal);
        var crashCoordinator = new PublicationCoordinator(fixture.Stores.State, fixture.Stores.Index, new Crash(point));
        var documents = new SqliteDocumentRepository(fixture.Stores.State, fixture.Stores.Index, crashCoordinator, crashFiles);
        var root = (await documents.GetAsync(fixture.RootId))!;
        var generation = (string)(await fixture.Scalar(false, "SELECT generation_json FROM publication_fences WHERE document_id='" + fixture.RootId + "'"))!;
        await Assert.ThrowsAsync<CrashFailure>(async () => await documents.MutateAsync(root.Id, root.Revision, "purge", JsonSerializer.SerializeToElement(new { }), Admin));
        if (point == CrashPointNames.AfterIndexCommit)
            Assert.Equal(PublicationOutcome.Superseded, await fixture.Publications.PublishCurrentAsync(new(root.Id, root.Revision, generation)));
        await fixture.Journal.RecoverAsync();
        await fixture.Publications.RecoverAsync();
        await PurgeCleanup.RecoverAsync(fixture.Stores.State, fixture.Stores.Index);
        Assert.False(File.Exists(original));
        Assert.Equal(0L, await fixture.Scalar(false, "SELECT COUNT(*) FROM documents WHERE status<>'purged'"));
        Assert.Equal(0L, await fixture.Scalar(true, "SELECT COUNT(*) FROM indexed_documents"));
        var revisions = await fixture.Scalar(false, "SELECT SUM(revision) FROM documents");
        await fixture.Journal.RecoverAsync(); await fixture.Publications.RecoverAsync();
        Assert.Equal(revisions, await fixture.Scalar(false, "SELECT SUM(revision) FROM documents"));
    }

    [Fact]
    public async Task SnapshotCleanupKeepsAcceptedHistoryAndLiveWorkButRemovesRejectedBytes()
    {
        await using var fixture = await Fixture.Create();
        var accepted = await fixture.Refresh("private-calendar-bytes-one");
        var work = await fixture.Claim(); var rejected = await fixture.WriteCandidate(work, "rejected-calendar-bytes");
        Assert.Equal(0, await fixture.Journal.CleanupUnpublishedCalendarSnapshotsAsync());
        Assert.True(File.Exists(rejected));
        await fixture.Sources.FailAsync(work, "calendar_publication_failed");
        Assert.Equal(1, await fixture.Journal.CleanupUnpublishedCalendarSnapshotsAsync());
        Assert.False(File.Exists(rejected)); Assert.True(File.Exists(accepted));
        Assert.Equal(0, await fixture.Journal.CleanupUnpublishedCalendarSnapshotsAsync());
    }

    [Fact]
    public async Task SnapshotCleanupPreservesExternallyChangedBytes()
    {
        await using var fixture = await Fixture.Create();
        var work = await fixture.Claim(); var path = await fixture.WriteCandidate(work, "rejected-calendar-bytes");
        await fixture.Sources.FailAsync(work, "calendar_publication_failed");
        await File.WriteAllTextAsync(path, "external-replacement");
        Assert.Equal(0, await fixture.Journal.CleanupUnpublishedCalendarSnapshotsAsync());
        Assert.Equal("external-replacement", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InterruptedUnpublishedSnapshotCleanupIsRecoverable()
    {
        await using var fixture = await Fixture.Create();
        var work = await fixture.Claim(); var path = await fixture.WriteCandidate(work, "rejected-calendar-bytes");
        await fixture.Sources.FailAsync(work, "calendar_publication_failed");
        using var crashing = new MutationJournal(fixture.Stores.State, fixture.RootPath, new Crash(MutationCrashPointNames.AfterSourceQuarantine));
        await Assert.ThrowsAsync<CrashFailure>(async () => await crashing.CleanupUnpublishedCalendarSnapshotsAsync());
        Assert.Equal(1, await fixture.Journal.CleanupUnpublishedCalendarSnapshotsAsync());
        Assert.False(File.Exists(path));
        Assert.Equal(0, await fixture.Journal.CleanupUnpublishedCalendarSnapshotsAsync());
    }

    [Fact]
    public async Task DeletedContainerCannotReceiveNewSnapshotFiles()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Refresh("private-calendar-bytes-one");
        var work = await fixture.Claim();
        await fixture.Mutate(fixture.RootId, "delete");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteCandidate(work, "late-snapshot"));
    }

    private static readonly CalendarWindow Window = new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", "UTC");
    private sealed class CrashFailure : Exception;
    private sealed class Crash(string point) : ICrashPoints
    { public ValueTask HitAsync(string value, CancellationToken cancellationToken = default) => value == point ? throw new CrashFailure() : ValueTask.CompletedTask; }
    private sealed class NthCrash(string point, int occurrence) : ICrashPoints
    {
        private int count;
        public ValueTask HitAsync(string value, CancellationToken cancellationToken = default) =>
            value == point && Interlocked.Increment(ref count) == occurrence ? throw new CrashFailure() : ValueTask.CompletedTask;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root = new();
        internal string RootPath => root.Path;
        internal MigrationTestStores Stores { get; }
        internal MutationJournal Journal { get; }
        private readonly ManagedDocumentFiles files;
        internal PublicationCoordinator Publications { get; }
        internal SqliteDocumentRepository Documents { get; }
        internal SqliteCalendarSubscriptionRepository Sources { get; }
        internal CalendarSubscription Source { get; private set; } = null!;
        internal string RootId => Source.ContainerId!;
        private Fixture()
        {
            Stores = new(root.Path); Journal = new(Stores.State, root.Path); files = new(root.Path, Journal);
            Publications = new(Stores.State, Stores.Index); Documents = new(Stores.State, Stores.Index, Publications, files);
            Sources = new(Stores.State, TimeProvider.System, publications: Publications, files: files);
        }
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture(); await fixture.Stores.Runner.MigrateAsync();
            await MigrationTestSql.WriteAsync(fixture.Stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
            await new AuthRepository(fixture.Stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id = Admin.CredentialId, Name = "admin", Kind = "session", Generation = 1, AccountEpoch = 1, Scopes = "read,write,admin", Verifier = "fixture", Kid = "fixture", CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow), SteppedUpAt = AuthTime.Format(DateTimeOffset.UtcNow) });
            fixture.Source = await fixture.Sources.CreateAsync("Calendar", "https://feed.test", "protected-envelope", 3600, true, Admin);
            return fixture;
        }
        internal async Task<CalendarRefreshWork> Claim()
        { await Sources.QueueAsync(Source.Id, false, Admin); return Assert.Single(await Sources.ClaimDueAsync()); }
        internal async Task<string> Refresh(string text)
        {
            var work = await Claim(); var bytes = Encoding.UTF8.GetBytes(text);
            var path = await files.WriteCalendarSnapshotAsync(RootId, work.LeaseId, bytes);
            var members = new[] { "one", "two" }.Select(uid => new CalendarMember(uid, "", uid, "private-event-content " + uid,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { uid, recurrence_id = "", starts_at = "2026-10-04T10:00:00Z", ends_at = "2026-10-04T11:00:00Z", all_day = false, status = "confirmed" }))!)).ToArray();
            var calendar = new CalendarSnapshot(Window, members, members.Select(m => new CalendarInstance(m.Uid, "", "2026-10-04T10:00:00Z", "2026-10-04T11:00:00Z", "confirmed")).ToArray());
            Assert.Equal(PublicationOutcome.Published, await Publications.PublishCalendarSubscriptionAsync(new(work, calendar, path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, null, null)));
            return path;
        }
        internal async Task<string> WriteCandidate(CalendarRefreshWork work, string text) =>
            await files.WriteCalendarSnapshotAsync(RootId, work.LeaseId, Encoding.UTF8.GetBytes(text));
        internal async Task<string[]> Members()
        {
            await using var lease = await Stores.State.OpenReadConnectionAsync();
            return (await lease.Connection.QueryAsync<string>("SELECT document_id FROM calendar_members ORDER BY member_key")).ToArray();
        }
        internal async Task Mutate(string id, string operation)
        { var current = (await Documents.GetAsync(id))!; await Documents.MutateAsync(id, current.Revision, operation, JsonSerializer.SerializeToElement(new { }), Admin); }
        internal Task<object?> Scalar(bool index, string sql) => MigrationTestSql.ScalarAsync(index ? Stores.Index : Stores.State, sql);
        public async ValueTask DisposeAsync() { files.Dispose(); Journal.Dispose(); await Stores.DisposeAsync(); root.Dispose(); }
    }
}
