using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Sources;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class DocumentRetentionTests
{
    private static readonly IngestionAuthority Admin = new("retention-admin", 1, 1, "admin", "session");

    [Fact]
    public async Task ExpiredTrashIsPurgedThroughTheJournalAndRecentTrashIsPreserved()
    {
        await using var f = await Fixture.Create();
        var old = await f.Note("old-private-text"); await f.Mutate(old.Id, "delete");
        var trashedPath = (await f.Documents.GetAsync(old.Id, true))!.Path;
        Assert.NotNull(await f.Scalar("SELECT deleted_at FROM documents WHERE id=@id", new { id = old.Id }));
        f.Clock.Advance(29);
        Assert.Equal(0, (await f.Retention.RunAsync()).Purged);
        var recent = await f.Note("recent-private-text"); await f.Mutate(recent.Id, "delete");
        // Authored mutations use the wall clock. Give the recent fixture its intended deletion time.
        await f.Write("UPDATE documents SET deleted_at=@now WHERE id=@id", new { id = recent.Id, now = f.Clock.GetUtcNow().ToString("O") });
        f.Clock.Advance(2);
        var result = await f.Retention.RunAsync();
        Assert.Equal(1, result.Purged);
        Assert.Equal("purged", await f.Scalar("SELECT status FROM documents WHERE id=@id", new { id = old.Id }));
        Assert.Equal("deleted", await f.Scalar("SELECT status FROM documents WHERE id=@id", new { id = recent.Id }));
        Assert.False(File.Exists(trashedPath));
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'old-private-text')>0"));
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM retention_purges WHERE completed_at IS NULL"));
        Assert.Equal(0, (await f.Retention.RunAsync()).Purged);
    }

    [Fact]
    public async Task AlreadyMissingTrashFileDoesNotStrandItsRetainedPayloads()
    {
        await using var f = await Fixture.Create(); var note = await f.Note("missing-file-private-text"); await f.Mutate(note.Id, "delete");
        File.Delete((await f.Documents.GetAsync(note.Id, true))!.Path!); f.Clock.Advance(31);
        Assert.Equal(1, (await f.Retention.RunAsync()).Purged);
        Assert.Equal("purged", (await f.Documents.GetAsync(note.Id))!.Status);
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'missing-file-private-text')>0"));
    }

    [Fact]
    public async Task ChangedTrashFileIsPreservedAndDoesNotStarveAnotherExpiry()
    {
        await using var f = await Fixture.Create(); var changed = await f.Note("original text"); await f.Mutate(changed.Id, "delete");
        var path = (await f.Documents.GetAsync(changed.Id, true))!.Path!;
        await File.WriteAllTextAsync(path, "external replacement");
        var normal = await f.Note("normal text"); await f.Mutate(normal.Id, "delete"); f.Clock.Advance(31);
        var first = await f.Retention.RunAsync(limit: 1);
        Assert.Equal(1, first.Deferred); Assert.Equal("external replacement", await File.ReadAllTextAsync(path));
        var second = await f.Retention.RunAsync(limit: 1);
        Assert.Equal(1, second.Purged);
        Assert.Equal("purged", (await f.Documents.GetAsync(normal.Id))!.Status);
        Assert.Equal("deleted", (await f.Documents.GetAsync(changed.Id))!.Status);
    }

    [Fact]
    public async Task RestoredDocumentRejectsAStaleExpiryEvenWithItsCurrentRevision()
    {
        await using var f = await Fixture.Create(); var note = await f.Note("restore-private-text");
        await f.Mutate(note.Id, "delete"); f.Clock.Advance(31); await f.Mutate(note.Id, "restore");
        var restored = (await f.Documents.GetAsync(note.Id, true))!;
        var mutation = "retention-purge:" + Ulid.NewUlid();
        Directory.CreateDirectory(Path.GetDirectoryName(f.Files.GetLifecyclePath(note.Id, ".md", "purge"))!);
        await Assert.ThrowsAsync<MutationRevisionException>(async () => await f.Journal.PrepareAsync(new MutationWriteRequest
        {
            MutationId = mutation, OperationId = mutation, Kind = "purge", Destination = f.Files.GetLifecyclePath(note.Id, ".md", "purge"), Content = [],
            DocumentReferences = [note.Id], Finalization = new() { DocumentId = note.Id, RevisionBefore = restored.Revision, RevisionAfter = restored.Revision + 1,
                RevisionCause = "purge", PurgeDocumentLineage = true, Expiry = new("deleted", f.Clock.GetUtcNow().AddDays(-30), Ulid.NewUlid().ToString()) }
        }));
        Assert.Equal(0, (await f.Retention.RunAsync()).Purged); Assert.True(File.Exists(restored.Path));
        Assert.Equal("restore-private-text", restored.Text);
    }

    [Fact]
    public async Task RemovedExternalDocumentExpiresItsIdentityWithoutTouchingTheOriginal()
    {
        await using var f = await Fixture.Create();
        var path = Path.Combine(f.External, "source.txt"); await File.WriteAllTextAsync(path, "external-private-text");
        var id = await f.ExternalDocument(path);
        await f.RemoveExternal(id, path);
        f.Clock.Advance(89); Assert.Equal(0, (await f.Retention.RunAsync()).Purged); f.Clock.Advance(2);
        var result = await f.Retention.RunAsync();
        Assert.Equal(1, result.Purged); Assert.Equal(1, result.ExpiredIdentities);
        Assert.Equal("external-private-text", await File.ReadAllTextAsync(path));
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM occurrences WHERE document_id=@id", new { id }));
        Assert.Equal(1L, await f.Scalar("SELECT COUNT(*) FROM documents WHERE id=@id AND natural_key IS NULL AND status='purged'", new { id }));
        var next = await f.ExternalDocument(path);
        Assert.NotEqual(id, next);
    }

    [Fact]
    public async Task ReappearanceBeforeExpiryKeepsTheOriginalIdentity()
    {
        await using var f = await Fixture.Create(); var path = Path.Combine(f.External, "source.txt");
        await File.WriteAllTextAsync(path, "reappeared"); var id = await f.ExternalDocument(path);
        await f.RemoveExternal(id, path); f.Clock.Advance(91);
        Assert.Equal(PublicationOutcome.Published, await f.Publications.PublishAsync(new(id, "external", 2, "{}", "import", "External", "hash", "external")
            { Status = "indexed", NaturalKey = "source.txt", Projection = TextProcessor.Process(id, "External", "reappeared", path: path) }));
        Assert.Equal(0, (await f.Retention.RunAsync()).Purged);
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM retention_removed_at WHERE document_id=@id", new { id }));
        Assert.Equal("indexed", (await f.Documents.GetAsync(id))!.Status);
    }

    [Theory]
    [InlineData(CrashPointNames.BeforeFinalize)]
    [InlineData(CrashPointNames.AfterIndexCommit)]
    public async Task InterruptedRetentionResumesWithoutRepeatingTheDocumentRevision(string point)
    {
        await using var f = await Fixture.Create(); var note = await f.Note("crash-private-text"); await f.Mutate(note.Id, "delete");
        f.Clock.Advance(31); f.Crash.Point = point;
        await Assert.ThrowsAsync<CrashFailure>(async () => await f.Retention.RunAsync());
        f.Crash.Point = null;
        await f.Retention.RunAsync();
        Assert.Equal("purged", (await f.Documents.GetAsync(note.Id))!.Status);
        var revision = (await f.Documents.GetAsync(note.Id))!.Revision;
        await f.Retention.RunAsync();
        Assert.Equal(revision, (await f.Documents.GetAsync(note.Id))!.Revision);
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM retention_purges WHERE completed_at IS NULL"));
    }

    [Fact]
    public async Task TrashedSubscriptionExpiresItsContainerAndProtectedLink()
    {
        await using var f = await Fixture.Create(); var source = await f.Calendars.CreateAsync("Calendar", "https://feed.test", "protected-test", 3600, true, Admin);
        var work = Assert.Single(await f.Calendars.ClaimDueAsync()); var bytes = Encoding.UTF8.GetBytes("private-calendar-original");
        var path = await f.Files.WriteCalendarSnapshotAsync(work.ContainerId, work.LeaseId, bytes);
        Assert.Equal(PublicationOutcome.Published, await f.Publications.PublishCalendarSubscriptionAsync(new(work, Calendar("one", "two"), path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, null, null)));
        await f.Mutate(work.ContainerId, "delete"); f.Clock.Advance(31);
        Assert.Equal(1, (await f.Retention.RunAsync()).Purged);
        Assert.Null(await f.Calendars.GetAsync(source.Id)); Assert.False(File.Exists(path));
        Assert.Equal(3L, await f.Scalar("SELECT COUNT(*) FROM documents WHERE status='purged'"));
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM calendar_subscriptions WHERE protected_url<>''"));
    }

    [Fact]
    public async Task CalendarRefreshWaitsForExpiredMemberIdentityDetachment()
    {
        await using var f = await Fixture.Create();
        await f.Calendars.CreateAsync("Calendar", "https://feed.test", "protected-test", 3600, true, Admin);
        var work = Assert.Single(await f.Calendars.ClaimDueAsync()); var bytes = Encoding.UTF8.GetBytes("retained-calendar-original");
        var path = await f.Files.WriteCalendarSnapshotAsync(work.ContainerId, work.LeaseId, bytes);
        await f.Publications.PublishCalendarSubscriptionAsync(new(work, Calendar("one", "two"), path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, null, null));
        var original = (string)(await f.Scalar("SELECT document_id FROM calendar_members WHERE member_key LIKE '%one%'"))!;
        await f.Publications.PublishCalendarAsync(await f.RootFence(work.ContainerId), Calendar("two"));
        f.Clock.Advance(91); f.Crash.BlockPoint = CrashPointNames.AfterStatePublishing;
        var retention = f.Retention.RunAsync().AsTask();
        Task<PublicationOutcome> refresh;
        try
        {
            await f.Crash.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            refresh = f.Publications.PublishCalendarAsync(await f.RootFence(work.ContainerId), Calendar("one", "two")).AsTask();
            Assert.False(refresh.IsCompleted);
        }
        finally { f.Crash.Continue.TrySetResult(); }
        await retention.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PublicationOutcome.Published, await refresh.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(original, await f.Scalar("SELECT document_id FROM calendar_members WHERE member_key LIKE '%one%'"));
        Assert.Equal("purged", await f.Scalar("SELECT status FROM documents WHERE id=@id", new { id = original }));
    }

    [Fact]
    public async Task ExpiredAbsentCalendarMemberGetsANewIdentityOnReappearance()
    {
        await using var f = await Fixture.Create();
        var source = await f.Calendars.CreateAsync("Calendar", "https://feed.test", "protected-test", 3600, true, Admin);
        var work = Assert.Single(await f.Calendars.ClaimDueAsync()); var bytes = Encoding.UTF8.GetBytes("retained-calendar-original");
        var path = await f.Files.WriteCalendarSnapshotAsync(work.ContainerId, work.LeaseId, bytes);
        await f.Publications.PublishCalendarSubscriptionAsync(new(work, Calendar("one", "two"), path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, null, null));
        var original = (string)(await f.Scalar("SELECT document_id FROM calendar_members WHERE member_key LIKE '%one%'"))!;
        await f.Publications.PublishCalendarAsync(await f.RootFence(work.ContainerId), Calendar("two"));
        f.Clock.Advance(91);
        var result = await f.Retention.RunAsync(); Assert.Equal(1, result.ExpiredIdentities);
        Assert.True(File.Exists(path)); Assert.Equal("retained-calendar-original", await File.ReadAllTextAsync(path));
        await f.Publications.PublishCalendarAsync(await f.RootFence(work.ContainerId), Calendar("one", "two"));
        Assert.NotEqual(original, await f.Scalar("SELECT document_id FROM calendar_members WHERE member_key LIKE '%one%'"));
        Assert.Equal("purged", await f.Scalar("SELECT status FROM documents WHERE id=@id", new { id = original }));
    }

    private static CalendarSnapshot Calendar(params string[] ids)
    {
        var members = ids.Select(id => new CalendarMember(id, "", id, "calendar content " + id,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"uid\":\"" + id + "\",\"recurrence_id\":\"\",\"starts_at\":\"2026-10-25T09:00:00Z\",\"ends_at\":\"2026-10-25T10:00:00Z\",\"all_day\":false,\"status\":\"confirmed\"}")!)).ToArray();
        return new(new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", "UTC"), members, members.Select(m => new CalendarInstance(m.Uid, "", "2026-10-25T09:00:00Z", "2026-10-25T10:00:00Z", "confirmed")).ToArray());
    }
    private sealed class CrashFailure : Exception;
    private sealed class Crash : ICrashPoints
    {
        internal string? Point, BlockPoint;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask HitAsync(string point, CancellationToken cancellationToken = default)
        {
            if (point == Point) throw new CrashFailure();
            if (point != BlockPoint) return;
            BlockPoint = null; Entered.TrySetResult(); await Continue.Task.WaitAsync(cancellationToken);
        }
    }
    private sealed class Clock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; internal void Advance(int days) => now = now.AddDays(days); }
    private sealed class Monitor(SecondBrainOptions options) : IOptionsMonitor<SecondBrainOptions> { public SecondBrainOptions CurrentValue => options; public SecondBrainOptions Get(string? name) => options; public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null; }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root = new();
        internal string External { get; } = Path.Combine(Path.GetTempPath(), "sb-retention-external-" + Guid.NewGuid().ToString("N"));
        internal Clock Clock { get; } = new(); internal Crash Crash { get; } = new();
        internal MigrationTestStores Stores { get; }
        internal MutationJournal Journal { get; } internal ManagedDocumentFiles Files { get; }
        internal PublicationCoordinator Publications { get; } internal SqliteDocumentRepository Documents { get; }
        internal SqliteCalendarSubscriptionRepository Calendars { get; } internal SqliteDocumentRetention Retention { get; }
        private Fixture()
        {
            Stores = new(root.Path); var options = new Monitor(new SecondBrainOptions { DataRoot = root.Path });
            Journal = new(Stores.State, root.Path, Crash); Files = new(root.Path, Journal); Publications = new(Stores.State, Stores.Index, Crash);
            Documents = new(Stores.State, Stores.Index, Publications, Files, optionsMonitor: options);
            Calendars = new(Stores.State, Clock, options, Publications, Files);
            Retention = new(Stores.State, Stores.Index, Publications, Journal, Files, Calendars, options, Clock);
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.External); await f.Stores.Runner.MigrateAsync();
            await f.Write("INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'; INSERT INTO sources(id,kind,name,created_at) VALUES('external','folder','External',@now)");
            await new AuthRepository(f.Stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id = Admin.CredentialId, Name = "admin", Kind = "session", Generation = 1, AccountEpoch = 1,
                Scopes = "read,write,admin", Verifier = "fixture", Kid = "fixture", CreatedAt = DateTimeOffset.UtcNow.ToString("O"), SteppedUpAt = DateTimeOffset.UtcNow.ToString("O") });
            return f;
        }
        internal async Task<DocumentRecord> Note(string text) => (await Documents.CaptureAsync(new(text, text), Admin)).Document;
        internal async Task Mutate(string id, string operation) => await Documents.MutateAsync(id, (await Documents.GetAsync(id))!.Revision, operation, JsonSerializer.SerializeToElement(new { }), Admin);
        internal async Task<string> ExternalDocument(string path)
        {
            var id = Ulid.NewUlid().ToString();
            await Publications.PublishAsync(new(id, "external", 0, "{}", "import", "External", "hash", "external") { NaturalKey = "source.txt", Projection = TextProcessor.Process(id, "External", "external-private-text", path: path) });
            await Write("INSERT INTO occurrences(document_id,source_id,path,last_seen_at) VALUES(@id,'external','source.txt',@now)", new { id, now = Clock.GetUtcNow().ToString("O") });
            return id;
        }
        internal async Task RemoveExternal(string id, string path) => await Publications.PublishAsync(new(id, "external", 1, "{}", "source-absence", "External", "hash", "external")
            { Status = "removed_from_source", NaturalKey = "source.txt", Projection = TextProcessor.Process(id, "External", "external-private-text", path: path) });
        internal Task Write(string sql, object? args = null) => Stores.State.QueueWriteAsync(async (c, t, ct) => { await c.ExecuteAsync(new CommandDefinition(sql, args ?? new { now = Clock.GetUtcNow().ToString("O") }, t, cancellationToken: ct)); return 0; }).AsTask();
        internal Task<object?> Scalar(string sql, object? args = null) => Read(sql, args);
        private async Task<object?> Read(string sql, object? args) { await using var read = await Stores.State.OpenReadConnectionAsync(); return await read.Connection.ExecuteScalarAsync(sql, args); }
        internal async Task<PublicationFence> RootFence(string id) => new(id, Convert.ToInt64(await Scalar("SELECT revision FROM documents WHERE id=@id", new { id })), (string)(await Scalar("SELECT generation_json FROM publication_fences WHERE document_id=@id", new { id }))!);
        public async ValueTask DisposeAsync() { Files.Dispose(); Journal.Dispose(); await Stores.DisposeAsync(); root.Dispose(); Directory.Delete(External, true); }
    }
}
