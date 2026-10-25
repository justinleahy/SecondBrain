using System.Text;
using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Retrieval;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class CalendarImportTests
{
    private static readonly IngestionAuthority Actor = new("calendar-session", 1, 1, "calendar fixture", "session");

    [Fact]
    public async Task CompleteContainerPublishesOccurrencesAndReconcilesAbsencesWithoutChangingIdentity()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var container = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("synthetic calendar bytes"), new("", "Calendar"), "calendar.ics", Actor);
        var first = Snapshot(Member("one", "Planning"), Member("two", "Review"));
        await Complete(documents, container.Document.Id, 1, first);
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        var embedding = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embedding.EnsureSpaceAsync("calendar-fixture", "fixture", "model", 2);
        foreach (var work in await embedding.PendingAsync(space.Id))
            Assert.True(await embedding.SaveAsync(space, work, work.Chunks.ToDictionary(chunk => chunk.Id, _ => new[] { 1f, 0f })));
        Assert.True(await embedding.ActivateAsync(space.Id));
        var firstId = (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT document_id FROM calendar_members ORDER BY member_key LIMIT 1"))!;
        var retrieval = new SqliteRetrievalRepository(stores.Index, new Protection(), state: stores.State);
        var query = await retrieval.QueryAsync(new(Type: "calendar_event", Occurred: new("2026-10-01T00:00:00Z", "2026-10-31T00:00:00Z"), Direction: "asc", Limit: 1), Actor);
        Assert.Single(query.Records); Assert.NotNull(query.NextCursor); Assert.NotNull(query.Records[0].InstanceId);
        Assert.Equal("complete", Assert.IsType<CalendarCoverage>(query.Coverage).Status);
        var next = await retrieval.QueryAsync(new(Type: "calendar_event", Occurred: new("2026-10-01T00:00:00Z", "2026-10-31T00:00:00Z"), Direction: "asc", Limit: 1, Cursor: query.NextCursor), Actor);
        Assert.Single(next.Records); Assert.NotEqual(query.Records[0].Id, next.Records[0].Id);
        var partial = await retrieval.QueryAsync(new(Type: "calendar_event", Occurred: new("2025-01-01T00:00:00Z", "2026-10-31T00:00:00Z")), Actor);
        Assert.Equal("partial", Assert.IsType<CalendarCoverage>(partial.Coverage).Status);
        await Complete(documents, container.Document.Id, 1, first);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT MAX(revision) FROM documents WHERE container_document_id IS NOT NULL"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT materialization_generation FROM calendar_coverage"));
        await Next(coordinator, documents, container.Document.Id);
        await Complete(documents, container.Document.Id, 2, Snapshot(Member("two", "Review changed")));
        Assert.Equal("removed_from_source", (await documents.GetAsync(firstId))!.Status);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM absences WHERE cleared_at IS NULL"));
        await Next(coordinator, documents, container.Document.Id);
        await Complete(documents, container.Document.Id, 3, first);
        Assert.Equal(firstId, (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT document_id FROM calendar_members ORDER BY member_key LIMIT 1"))!);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM absences WHERE cleared_at IS NULL"));
        await documents.RebuildAsync();
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM indexed_calendar_coverage"));
        Assert.Equal(1, await documents.ScheduleCalendarMaterializationAsync(first.Window));
        await Complete(documents, container.Document.Id, 3, first);
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM indexed_calendar_coverage"));
    }

    [Fact]
    public async Task SameUidInDifferentContainersIsDistinctAndDeletingMemberNeverMovesTheContainer()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var first = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("original one"), new(""), "one.ics", Actor);
        var second = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("original two"), new(""), "two.ics", Actor);
        await Complete(documents, first.Document.Id, 1, Snapshot(Member("shared", "First")));
        await Complete(documents, second.Document.Id, 1, Snapshot(Member("shared", "Second")));
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM documents WHERE type='calendar_event'"));
        var id = (string)(await MigrationTestSql.ScalarAsync(stores.State, $"SELECT document_id FROM calendar_members WHERE container_id='{first.Document.Id}'"))!;
        await documents.MutateAsync(id, 1, "delete", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Equal("original one", await File.ReadAllTextAsync(first.Document.Path!));
        await Next(coordinator, documents, first.Document.Id);
        await Complete(documents, first.Document.Id, 2, Snapshot(Member("shared", "Changed")));
        Assert.Equal("deleted", (await documents.GetAsync(id))!.Status);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
    }

    [Fact]
    public async Task UserMetadataAndPeopleSurviveRefreshAndPurgeRetainsOnlyContainerOriginal()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index); var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var parent = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("calendar original"), new(""), "calendar.ics", Actor);
        var member = Member("one", "Imported title") with { People = [new("attendee", "ada@example.com", "Ada Lovelace")] };
        await Complete(documents, parent.Document.Id, 1, Snapshot(member));
        var id = (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT document_id FROM calendar_members"))!;
        var updated = await documents.MutateAsync(id, 1, "patch", JsonSerializer.SerializeToElement(new { title = "My title", tags = new[] { "important" }, aliases = new[] { "planning" } }), Actor);
        Assert.Equal(2, updated.Document.Revision);
        var retrieval = new SqliteRetrievalRepository(stores.Index, new Protection(), state: stores.State);
        var people = await retrieval.QueryAsync(new(Type: "calendar_event", People: [new("ADA LOVELACE", "attendee")]), Actor);
        Assert.Equal(id, Assert.Single(people.Records).Id);
        await Next(coordinator, documents, parent.Document.Id);
        await Complete(documents, parent.Document.Id, 2, Snapshot(member with { Title = "Changed source title" }));
        var kept = (await documents.GetAsync(id, true))!;
        Assert.Equal("My title", kept.Title); Assert.Equal(new[] { "important" }, kept.Tags);
        Assert.Equal(3, kept.Revision);
        var metadata = await new SqliteDocumentCatalog(stores.State, new Protection(), stores.Index).DetailsAsync(id, Actor);
        Assert.Equal(new[] { "planning" }, metadata.Aliases);
        await documents.MutateAsync(id, kept.Revision, "purge", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Equal("calendar original", await File.ReadAllTextAsync(parent.Document.Path!));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, $"SELECT COUNT(*) FROM payloads WHERE redacted_at IS NULL AND EXISTS(SELECT 1 FROM json_each(document_refs_json) WHERE value='{id}')"));
        await Next(coordinator, documents, parent.Document.Id);
        await Complete(documents, parent.Document.Id, 3, Snapshot(member));
        Assert.Equal("purged", (await documents.GetAsync(id))!.Status);
        Assert.Empty((await retrieval.QueryAsync(new(Type: "calendar_event"), Actor)).Records);
    }

    [Fact]
    public async Task MixedTimelineReturnsEachOccurrenceAndHidesChildrenWhenTheirContainerIsDeleted()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index); var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var parent = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("calendar original"), new(""), "calendar.ics", Actor);
        var snapshot = Snapshot(Member("series", "Recurring"));
        snapshot = snapshot with { Instances = [snapshot.Instances[0], snapshot.Instances[0] with { StartsAt = "2026-10-05T10:00:00Z", EndsAt = "2026-10-05T11:00:00Z" }] };
        await Complete(documents, parent.Document.Id, 1, snapshot);
        var article = await documents.CaptureAsync(new("Article body", "An article", "article", JsonSerializer.SerializeToElement(new { published_at = "2026-10-04T12:00:00Z" })), Actor);
        var retrieval = new SqliteRetrievalRepository(stores.Index, new Protection(), state: stores.State);
        var page = await retrieval.QueryAsync(new(Occurred: new("2026-10-01T00:00:00Z", "2026-10-31T00:00:00Z"), Sort: "occurred_at", Direction: "asc"), Actor);
        Assert.Equal(3, page.Records.Count);
        Assert.Equal(article.Document.Id, page.Records[1].Id);
        Assert.Equal(page.Records[0].Id, page.Records[2].Id);
        Assert.NotEqual(page.Records[0].InstanceId, page.Records[2].InstanceId);
        var eventId = page.Records[0].Id;
        await documents.MutateAsync(parent.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Empty((await retrieval.QueryAsync(new(Type: "calendar_event"), Actor)).Records);
        Assert.Null(await documents.GetAsync(eventId, true));
        await Assert.ThrowsAsync<IngestionConflictException>(async () => await new SqliteDocumentCatalog(stores.State, new Protection(), stores.Index).DetailsAsync(eventId, Actor));
        await documents.MutateAsync(parent.Document.Id, 2, "restore", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Equal(2, (await retrieval.QueryAsync(new(Type: "calendar_event"), Actor)).Records.Count);
    }

    [Fact]
    public async Task EmptySnapshotRemovesMembersAndFailedValidationPreservesPreviousCalendar()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index); var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var parent = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("calendar"), new(""), "calendar.ics", Actor);
        await Complete(documents, parent.Document.Id, 1, Snapshot(Member("one", "Keep until valid empty")));
        await Next(coordinator, documents, parent.Document.Id);
        var invalid = new CalendarMember("invalid", "", "Invalid", "Invalid", new Dictionary<string, JsonElement>());
        await Assert.ThrowsAnyAsync<Exception>(() => Complete(documents, parent.Document.Id, 2, Snapshot(Member("new", "must roll back"), invalid)));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM calendar_members"));
        await Complete(documents, parent.Document.Id, 2, Snapshot());
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM absences WHERE cleared_at IS NULL"));
    }

    [Fact]
    public async Task DailyMaterializationQueuesOnceAndAdvancesCoverageWithoutDocumentRevisionChanges()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index); var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var parent = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("calendar"), new(""), "calendar.ics", Actor);
        var initial = Snapshot(Member("one", "Weekly"));
        await Complete(documents, parent.Document.Id, 1, initial);
        var nextWindow = new CalendarWindow("2026-10-02T00:00:00Z", "2026-11-02T00:00:00Z", "UTC");
        Assert.Equal(1, await documents.ScheduleCalendarMaterializationAsync(nextWindow));
        Assert.Equal(0, await documents.ScheduleCalendarMaterializationAsync(nextWindow));
        await Complete(documents, parent.Document.Id, 1, initial with { Window = nextWindow });
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT MAX(revision) FROM documents"));
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT materialization_generation FROM calendar_coverage"));
        Assert.Equal(0, await documents.ScheduleCalendarMaterializationAsync(nextWindow));
    }

    [Theory]
    [InlineData(CrashPointNames.AfterStatePublishing)]
    [InlineData(CrashPointNames.AfterIndexCommit)]
    public async Task InterruptedBatchRecoversWithoutDuplicateMembersOrPartialCalendar(string crashPoint)
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var stable = new PublicationCoordinator(stores.State, stores.Index);
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, stable, files);
        var parent = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("calendar"), new(""), "calendar.ics", Actor);
        await Complete(documents, parent.Document.Id, 1, Snapshot(Member("one", "Original")));
        await Next(stable, documents, parent.Document.Id);
        var crashing = new PublicationCoordinator(stores.State, stores.Index, new Crash(crashPoint));
        var fenced = new SqliteDocumentRepository(stores.State, stores.Index, crashing, files);
        await Assert.ThrowsAsync<InjectedFailure>(() => Complete(fenced, parent.Document.Id, 2, Snapshot(Member("one", "Changed"), Member("two", "New"))));
        Assert.Equal(crashPoint == CrashPointNames.AfterStatePublishing ? 1L : 2L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        await stable.RecoverAsync();
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM event_occurrences"));
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM calendar_members"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM calendar_batches WHERE phase='publishing'"));
    }

    private static async Task Next(PublicationCoordinator coordinator, SqliteDocumentRepository documents, string id)
    {
        var document = (await documents.GetAsync(id, true))!;
        var generation = new ChunkingProfile().GenerationJson;
        await coordinator.PublishAsync(new(id, "api-captures", document.Revision, generation, "source-edit", document.Title, Guid.NewGuid().ToString("N"), document.Provenance)
        { Status = "queued", Projection = TextProcessor.Process(id, document.Title, "", path: document.Path) });
    }
    private static async Task Complete(SqliteDocumentRepository documents, string id, long revision, CalendarSnapshot snapshot) =>
        await documents.CompleteFileAsync(id, revision, new("extracted", "calendar", "text/calendar", "", Calendar: snapshot));
    private static CalendarMember Member(string uid, string title) => new(uid, "", title, title,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { uid, recurrence_id = "", starts_at = "2026-10-04T10:00:00Z", ends_at = "2026-10-04T11:00:00Z", all_day = false, status = "confirmed", attendees = new[] { "ada@example.com" } }))!);
    private static CalendarSnapshot Snapshot(params CalendarMember[] members) => new(new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", "UTC"), members,
        members.Select(member => new CalendarInstance(member.Uid, member.RecurrenceId, "2026-10-04T10:00:00Z", "2026-10-04T11:00:00Z", "confirmed")).ToArray());
    private static async Task Seed(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id = Actor.CredentialId, Name = Actor.CredentialName, Kind = Actor.Kind, Generation = 1, AccountEpoch = 1, Scopes = "read,write,infer,admin", Verifier = "fixture", Kid = "fixture", SteppedUpAt = AuthTime.Format(DateTimeOffset.UtcNow), CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow) });
    }
    private sealed class Protection : IPayloadProtection { public string Protect(string purpose, string plaintext) => plaintext; public string Unprotect(string purpose, string protectedText) => protectedText; }
    private sealed class InjectedFailure : Exception;
    private sealed class Crash(string point) : ICrashPoints { public ValueTask HitAsync(string name, CancellationToken cancellationToken = default) => name == point ? throw new InjectedFailure() : ValueTask.CompletedTask; }
}
