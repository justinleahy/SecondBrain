using System.Text;
using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Sources;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class FileCompletionTests
{
    private static readonly IngestionAuthority Actor = new("file-session", 1, 1, "file fixture", "session");
    [Fact]
    public async Task FileIdempotencyBindsTheParserHintAsWellAsBytes()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var bytes = Encoding.UTF8.GetBytes("# identical bytes"); var metadata = new CaptureRequest("", Type: "file");
        var first = await repository.AdmitFileAsync(bytes, metadata, "entry.md", Actor, "one-request");
        var replay = await repository.AdmitFileAsync(bytes, metadata, "entry.md", Actor, "one-request");
        Assert.Equal(first.Document.Id, replay.Document.Id);
        var failure = await Assert.ThrowsAsync<IngestionConflictException>(() => repository.AdmitFileAsync(bytes, metadata, "entry.txt", Actor, "one-request").AsTask());
        Assert.Equal("idempotency-key-reuse", failure.Code);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM documents"));
    }

    [Fact]
    public async Task CapacityRejectionLeavesNoManagedOriginalOrJournalEntry()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var options = new SecondBrain.Core.Configuration.SecondBrainOptions(); options.Limits.Global.Files = 1;
        var coordinator = new PublicationCoordinator(stores.State, stores.Index, configuration: Microsoft.Extensions.Options.Options.Create(options));
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        await repository.CaptureAsync(new("admitted capture"), Actor);
        var capture = await Assert.ThrowsAsync<IngestionConflictException>(() => repository.CaptureAsync(new("rejected capture"), Actor).AsTask());
        Assert.Equal("capacity", capture.Code);
        var file = await Assert.ThrowsAsync<IngestionConflictException>(() => repository.AdmitFileAsync(Encoding.UTF8.GetBytes("rejected file"), new(""), "rejected.txt", Actor).AsTask());
        Assert.Equal("capacity", file.Code);
        Assert.Single(Directory.GetFiles(Path.Combine(root.Path, "vault", "originals")));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM mutations"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM documents"));
    }

    [Fact]
    public async Task UnpublishedImportIsDiscardedButPublishedOriginalIsKept()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var published = await repository.CaptureAsync(new("published capture"), Actor);
        var orphan = await files.WriteAsync("01ORPHANDOCUMENT", Encoding.UTF8.GetBytes("never published"), ".md");
        Assert.True(File.Exists(orphan));
        await files.DiscardUnpublishedAsync(orphan);
        Assert.False(File.Exists(orphan));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM mutations"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM payload_refs WHERE referrer_kind='mutation' AND referrer_id NOT IN (SELECT id FROM mutations)"));
        await files.DiscardUnpublishedAsync(published.Document.Path!);
        Assert.True(File.Exists(published.Document.Path!));
    }

    [Fact]
    public async Task InterruptedCompletionRecoversWithoutDuplicateAdmissionOrProjectionPayload()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var admitted = await repository.AdmitFileAsync(Encoding.UTF8.GetBytes("original fixture"), new(""), "fixture.txt", Actor);
        var work = Assert.Single(await repository.PendingFilesAsync());
        var extraction = new ExtractionResult("extracted", "text", "text/plain", "durable extracted nebula");
        var crashing = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index, new Crash()), files);
        await Assert.ThrowsAsync<InjectedFailure>(() => crashing.CompleteFileAsync(work.DocumentId, work.Revision, extraction).AsTask());
        var count = await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM payloads");
        for (var restart = 0; restart < 2; restart++)
        {
            await new PublicationCoordinator(stores.State, stores.Index).RecoverAsync();
            await repository.CompleteFileAsync(work.DocumentId, work.Revision, extraction);
            Assert.Equal(count, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM payloads"));
            Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM jobs WHERE kind='embed'"));
            Assert.Equal("durable extracted nebula", (await repository.GetAsync(admitted.Document.Id, true))!.Text);
        }
    }
    [Theory]
    [InlineData(null, "article", "article", "frontmatter")]
    [InlineData("file", "article", "file", "door")]
    [InlineData(null, null, "note", "format-default")]
    public async Task CompletionPreservesOriginalAndTypePrecedenceAndRunsExactlyOnce(string? door, string? declared, string expected, string origin)
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var original = Encoding.UTF8.GetBytes("---\ntitle: Original\n---\noriginal bytes");
        var admitted = await repository.AdmitFileAsync(original, new("", Type: door), "original.md", Actor);
        Assert.Equal("queued", admitted.Document.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(admitted.Document.Path!));
        var work = Assert.Single(await repository.PendingFilesAsync());
        var extraction = new ExtractionResult("extracted", "markdown", "text/markdown", "extracted astronomy", DeclaredType: declared, Links:["Notebook", "https://example.org/article"]);
        var completed = await repository.CompleteFileAsync(work.DocumentId, work.Revision, extraction);
        Assert.Equal(expected, completed.Document.Type);
        Assert.Equal(origin, await MigrationTestSql.ScalarAsync(stores.State, "SELECT type_origin FROM documents"));
        var payloadCount = await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM payloads");
        await repository.CompleteFileAsync(work.DocumentId, work.Revision, extraction);
        Assert.Equal(payloadCount, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM payloads"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM jobs WHERE kind='embed'"));
        Assert.Equal(Actor.CredentialId, await MigrationTestSql.ScalarAsync(stores.State, "SELECT initiating_credential_id FROM jobs WHERE kind='embed'"));
        Assert.Equal(original, await File.ReadAllBytesAsync(admitted.Document.Path!));
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM links"));
        var identical = await repository.AdmitFileAsync(original, new("", Type: door, Title:"Second title"), "second.md", Actor);
        var cached = await repository.CachedExtractionAsync(identical.Document.Id);
        Assert.NotNull(cached); Assert.Equal(extraction.LosslessText, cached.LosslessText);
        var plain = await repository.AdmitFileAsync(original, new("", Type:door), "same-bytes.txt", Actor);
        Assert.Null(await repository.CachedExtractionAsync(plain.Document.Id));
    }

    [Fact]
    public async Task FolderAbsenceReattachesIdentityWhileSuppressionPreventsImport()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var folders = new SqliteFolderDocumentRepository(stores.State, new PublicationCoordinator(stores.State, stores.Index), repository, files);
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "watch", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("fixture-folder", "folder", "Fixture", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var bytes = Encoding.UTF8.GetBytes("stable astronomy"); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var entry = new SourceFileEntry("entry.txt", bytes.Length, DateTimeOffset.UtcNow, hash);
        var candidate = Assert.Single(await folders.PlanScanAsync(source, [entry], true));
        await folders.PersistPlanAsync(source, [new(candidate, 1)]);
        Assert.Equal(candidate, Assert.Single(await folders.PendingPlanAsync(source)).Candidate);
        var capture = (await folders.AdmitSourceAsync(source, candidate, bytes, hash))!;
        // A crash after admission but before checkpoint acknowledgement replays the old null-id candidate.
        var replayed = (await folders.AdmitSourceAsync(source, Assert.Single(await folders.PendingPlanAsync(source)).Candidate, bytes, hash))!;
        Assert.Equal(capture.Document.Id, replayed.Document.Id);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM documents"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM jobs"));
        await folders.CompleteCandidateAsync(source.Id, entry.RelativePath);
        Assert.Empty(await folders.PendingPlanAsync(source));
        await repository.CompleteFileAsync(capture.Document.Id, 1, new("extracted", "text", "text/plain", "stable astronomy"));
        await folders.PlanScanAsync(source, [], true);
        Assert.Equal(DocumentStatusNames.Missing, (await repository.GetAsync(capture.Document.Id))!.Status);
        var reappearance = Assert.Single(await folders.PlanScanAsync(source, [entry], true));
        Assert.Equal(capture.Document.Id, reappearance.DocumentId);
        var restored = (await folders.AdmitSourceAsync(source, reappearance, bytes, hash))!;
        Assert.Equal(capture.Document.Id, restored.Document.Id);
        var movedEntry = entry with { RelativePath = "renamed.txt" };
        var move = Assert.Single(await folders.PlanScanAsync(source, [movedEntry], true));
        var moved = (await folders.AdmitSourceAsync(source, move, bytes, hash, 1))!;
        Assert.Equal(capture.Document.Id, moved.Document.Id);
        Assert.Equal("embed", (await repository.JobAsync(moved.Job!.Id))!.Stage);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM jobs WHERE kind='extract'"));
        await repository.MutateAsync(capture.Document.Id, moved.Document.Revision, "delete", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Empty(await folders.PlanScanAsync(source, [movedEntry], true));
    }
    [Fact]
    public async Task UnreadableEntriesStayIndexedAndAreNeverMarkedAbsent()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var folders = new SqliteFolderDocumentRepository(stores.State, new PublicationCoordinator(stores.State, stores.Index), repository, files);
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "watch", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("unreadable-folder", "folder", "Unreadable", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var ids = new List<string>();
        foreach (var path in new[] { "top.txt", "nested/inner.txt" })
        {
            var bytes = Encoding.UTF8.GetBytes("readable " + path); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            var admitted = (await folders.AdmitSourceAsync(source, new(new SourceFileEntry(path, bytes.Length, DateTimeOffset.UtcNow, hash), null, 0), bytes, hash))!;
            await repository.CompleteFileAsync(admitted.Document.Id, 1, new("extracted", "text", "text/plain", "readable " + path));
            ids.Add(admitted.Document.Id);
        }
        var before = new List<string>(); foreach (var id in ids) before.Add((await repository.GetAsync(id))!.Status);
        Assert.DoesNotContain("missing", before);
        var candidates = await folders.PlanScanAsync(source, [SourceFileEntry.UnreadableEntry("top.txt"), SourceFileEntry.UnreadableEntry("nested")], true);
        Assert.Empty(candidates);
        for (var i = 0; i < ids.Count; i++) Assert.Equal(before[i], (await repository.GetAsync(ids[i]))!.Status);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM absences"));
        await folders.PlanScanAsync(source, [], true);
        foreach (var id in ids) Assert.Equal("missing", (await repository.GetAsync(id))!.Status);
    }

    [Fact]
    public async Task SourceReextractionPreservesJournaledUserOverridesAndRawProperties()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var folders = new SqliteFolderDocumentRepository(stores.State, new PublicationCoordinator(stores.State, stores.Index), repository, files);
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "watch", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("override-folder", "folder", "Overrides", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var bytes = Encoding.UTF8.GetBytes("original source");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var entry = new SourceFileEntry("entry.txt", bytes.Length, DateTimeOffset.UtcNow, hash);
        var admitted = (await folders.AdmitSourceAsync(source, new(entry, null, 0), bytes, hash))!;
        await repository.CompleteFileAsync(admitted.Document.Id, 1, new("extracted", "text", "text/plain", "original source"));
        var edited = await repository.MutateAsync(admitted.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { type="article", title="Authored title", props=new { author="Authored author" }, tags=new[]{"authored"} }), Actor);
        bytes = Encoding.UTF8.GetBytes("changed source"); hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        entry = entry with { Length=bytes.Length, Sha256=hash };
        var updated = (await folders.AdmitSourceAsync(source, new(entry, admitted.Document.Id, edited.Document.Revision), bytes, hash))!;
        var result = await repository.CompleteFileAsync(updated.Document.Id, updated.Document.Revision,
            new("extracted", "text", "text/plain", "changed source", Title:"Machine title", DeclaredType:"note",
                Properties:new Dictionary<string,JsonElement>{{"author",JsonSerializer.SerializeToElement("Machine author")}}, Tags:["machine"]));
        Assert.Equal("article", result.Document.Type); Assert.Equal("Authored title", result.Document.Title);
        Assert.Equal(new[] { "authored" }, result.Document.Tags!);
        Assert.Equal("Authored author", JsonDocument.Parse(result.Document.PropsJson).RootElement.GetProperty("author").GetString());
        Assert.Contains("Machine author", (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT props_original_json FROM documents"))!);
    }
    [Fact]
    public async Task MoveAdmittedLongAfterTheAbsenceKeepsIdentityWhileTheAbsenceIsOpen()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var folders = new SqliteFolderDocumentRepository(stores.State, new PublicationCoordinator(stores.State, stores.Index), repository, files);
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "watch", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("slow-move-folder", "folder", "Slow move", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var bytes = Encoding.UTF8.GetBytes("moved astronomy"); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var entry = new SourceFileEntry("old.txt", bytes.Length, DateTimeOffset.UtcNow, hash);
        var admitted = (await folders.AdmitSourceAsync(source, new(entry, null, 0), bytes, hash))!;
        await repository.CompleteFileAsync(admitted.Document.Id, 1, new("extracted", "text", "text/plain", "moved astronomy"));
        await folders.PlanScanAsync(source, [], true);
        // The absence was recorded well before the move is admitted (a long checkpointed plan drain).
        await MigrationTestSql.WriteAsync(stores.State, "UPDATE absences SET since='2000-01-01T00:00:00.0000000+00:00'");
        var moved = (await folders.AdmitSourceAsync(source, new(entry with { RelativePath = "new.txt" }, null, 0), bytes, hash, 1))!;
        Assert.Equal(admitted.Document.Id, moved.Document.Id);
        Assert.Equal("embed", (await repository.JobAsync(moved.Job!.Id))!.Stage);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM documents"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM jobs WHERE kind='extract'"));
    }

    [Fact]
    public async Task AmbiguousSameHashAbsencesNeverGuessMoveIdentity()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var folders = new SqliteFolderDocumentRepository(stores.State, new PublicationCoordinator(stores.State, stores.Index), repository, files);
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "watch", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("ambiguous-folder", "folder", "Ambiguous", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var bytes = Encoding.UTF8.GetBytes("duplicate bytes"); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var entry = new SourceFileEntry("a.txt", bytes.Length, DateTimeOffset.UtcNow, hash);
        var a = (await folders.AdmitSourceAsync(source, new(entry, null, 0), bytes, hash, 2))!;
        var b = (await folders.AdmitSourceAsync(source, new(entry with { RelativePath="b.txt" }, null, 0), bytes, hash, 2))!;
        await folders.PlanScanAsync(source, [], true);
        var moved = (await folders.AdmitSourceAsync(source, new(entry with { RelativePath="unknown.txt" }, null, 0), bytes, hash, 1))!;
        Assert.NotEqual(a.Document.Id, moved.Document.Id); Assert.NotEqual(b.Document.Id, moved.Document.Id);
        Assert.Equal(3L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM documents"));
        Assert.Equal(DocumentStatusNames.Missing, (await repository.GetAsync(a.Document.Id))!.Status);
    }
    [Fact]
    public async Task ArticleCaptureAndPropertyEditsDeriveSourceOccurrenceAndTitleEditsPreserveIt()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var result = await repository.CaptureAsync(new("authored text", Type:"article", Props:JsonSerializer.SerializeToElement(new { published_at="2026-07-02T10:30:00+02:00" })), Actor);
        Assert.StartsWith("2026-07-02T08:30:00", (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT occurred_at FROM documents"))!);
        Assert.Equal("+02:00", await MigrationTestSql.ScalarAsync(stores.State, "SELECT occurred_offset FROM documents"));
        await repository.MutateAsync(result.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { title="Only title changes" }), Actor);
        Assert.StartsWith("2026-07-02T08:30:00", (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT occurred_at FROM documents"))!);
        await repository.MutateAsync(result.Document.Id, 2, "update", JsonSerializer.SerializeToElement(new { props=new { published_at="2026-07-03T00:00:00Z" } }), Actor);
        Assert.StartsWith("2026-07-03T00:00:00", (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT occurred_at FROM documents"))!);
        Assert.Equal("published_at", await MigrationTestSql.ScalarAsync(stores.State, "SELECT occurred_basis FROM documents"));
    }
    [Fact]
    public async Task TagAdditionsExecuteDirectlyButTagRemovalFromAKeyIsAProposal()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var key = new IngestionAuthority("tag-key", 1, 1, "tag key", "api_key");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id=key.CredentialId,Name=key.CredentialName,Kind=key.Kind,Generation=1,AccountEpoch=1,Scopes="read,write",Verifier="fixture",Kid="fixture",CreatedAt=AuthTime.Format(DateTimeOffset.UtcNow) });
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var captured = await repository.CaptureAsync(new("tagged text", Tags:["alpha", "beta"]), Actor);
        var added = await repository.MutateAsync(captured.Document.Id, 1, "patch", JsonSerializer.SerializeToElement(new { tags = new[] { "alpha", "beta", "gamma" } }), key);
        Assert.Equal(2, added.Document.Revision);
        Assert.Equal(["alpha", "beta", "gamma"], (await repository.GetAsync(captured.Document.Id, true))!.Tags!);
        var removal = await repository.MutateAsync(captured.Document.Id, 2, "patch", JsonSerializer.SerializeToElement(new { tags = new[] { "alpha", "gamma" } }), key);
        Assert.Equal("proposed", removal.Document.Status); Assert.Equal("proposal", removal.Job!.Stage);
        Assert.Equal(["alpha", "beta", "gamma"], (await repository.GetAsync(captured.Document.Id, true))!.Tags!);
        var direct = await repository.MutateAsync(captured.Document.Id, 2, "patch", JsonSerializer.SerializeToElement(new { tags = new[] { "alpha" } }), Actor);
        Assert.Equal(3, direct.Document.Revision);
        Assert.Equal(["alpha"], (await repository.GetAsync(captured.Document.Id, true))!.Tags!);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(20, true)]
    public async Task FolderStatusUsesConfiguredStepUpWindow(int minutes, bool allowed)
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores);
        var stepped = AuthTime.Format(DateTimeOffset.UtcNow.AddMinutes(-5));
        await MigrationTestSql.WriteAsync(stores.State, $"UPDATE credentials SET stepped_up_at='{stepped}'");
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "watch", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("stepup-folder", "folder", "Stepup", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var monitor = new MutableFolderOptions();
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var folders = new SqliteFolderDocumentRepository(stores.State, coordinator, new SqliteDocumentRepository(stores.State, stores.Index, coordinator), optionsMonitor: monitor);
        monitor.CurrentValue = new SecondBrain.Core.Configuration.SecondBrainOptions();
        monitor.CurrentValue.Auth.StepUpMinutes = minutes;
        if (allowed) await folders.SetStatusAsync(source.Id, "disabled", Actor);
        else await Assert.ThrowsAsync<AuthorityChangedException>(() => folders.SetStatusAsync(source.Id, "disabled", Actor).AsTask());
        Assert.Equal(allowed ? "disabled" : "active", await MigrationTestSql.ScalarAsync(stores.State, "SELECT status FROM sources WHERE id='stepup-folder'"));
    }

    [Fact]
    public async Task SecondAbsentScanExpiresMoveWindowButSamePathReattachesRetainedIdentity()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var folders = new SqliteFolderDocumentRepository(stores.State, coordinator, repository, files);
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "watch", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("expired-folder", "folder", "Expired", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var bytes = Encoding.UTF8.GetBytes("retained source"); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var entry = new SourceFileEntry("entry.txt", bytes.Length, DateTimeOffset.UtcNow, hash);
        var admitted = (await folders.AdmitSourceAsync(source, new(entry, null, 0), bytes, hash))!;
        await repository.CompleteFileAsync(admitted.Document.Id, 1, new("extracted", "text", "text/plain", "retained source"));
        await folders.PlanScanAsync(source, [], true);
        var since = await MigrationTestSql.ScalarAsync(stores.State, "SELECT since FROM absences");
        var unrelatedBytes = Encoding.UTF8.GetBytes("unrelated arrival");
        var unrelatedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(unrelatedBytes));
        var unrelatedEntry = new SourceFileEntry("arrival.txt", unrelatedBytes.Length, DateTimeOffset.UtcNow, null);
        var unrelated = Assert.Single(await folders.PlanScanAsync(source, [unrelatedEntry], false));
        await folders.PersistPlanAsync(source, [new(unrelated, 1)]);
        await folders.AdmitSourceAsync(source, unrelated, unrelatedBytes, unrelatedHash);
        await folders.CompleteScanAsync(source.Id); // Still checkpointed: completion cannot expire anything yet.
        Assert.Equal("missing", (await repository.GetAsync(admitted.Document.Id))!.Status);
        await folders.CompleteCandidateAsync(source.Id, unrelatedEntry.RelativePath);
        await folders.CompleteScanAsync(source.Id);
        Assert.Equal("removed_from_source", (await repository.GetAsync(admitted.Document.Id))!.Status);
        Assert.Equal(since, await MigrationTestSql.ScalarAsync(stores.State, "SELECT since FROM absences"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM indexed_documents WHERE document_id='" + admitted.Document.Id + "'"));
        var renamed = (await folders.AdmitSourceAsync(source, new(entry with { RelativePath="renamed.txt" }, null, 0), bytes, hash))!;
        Assert.NotEqual(admitted.Document.Id, renamed.Document.Id);
        var reappearance = Assert.Single(await folders.PlanScanAsync(source, [entry, entry with { RelativePath="renamed.txt" }, unrelatedEntry with { Sha256=unrelatedHash }], true));
        Assert.Equal(admitted.Document.Id, reappearance.DocumentId);
        var restored = (await folders.AdmitSourceAsync(source, reappearance, bytes, hash))!;
        Assert.Equal(admitted.Document.Id, restored.Document.Id);
        Assert.Equal("indexed_partial", restored.Document.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameByteMovePreservesExtractionMetadataAndPageLocators(bool hashedScan)
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var folders = new SqliteFolderDocumentRepository(stores.State, coordinator, repository, files);
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "index", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("metadata-folder", "folder", "Metadata", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var bytes = Encoding.UTF8.GetBytes("source bytes"); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var entry = new SourceFileEntry("old.pdf", bytes.Length, DateTimeOffset.UtcNow, hash);
        var admitted = (await folders.AdmitSourceAsync(source, new(entry, null, 0), bytes, hash))!;
        await repository.CompleteFileAsync(admitted.Document.Id, 1, new("extracted", "pdf", "application/pdf", "page text", DeclaredType:"note", Links:["https://example.org"], Aliases:["alias"], DeclaredCreatedAt:"2025-01-02T03:04:05Z", Pages:[new ExtractionPage(7, 0, 9)]));
        var retrieval = new SecondBrain.Storage.Retrieval.SqliteRetrievalRepository(stores.Index, new NoCursorProtection());
        var originalResult = await retrieval.SearchAsync(new("page", "keyword", new(PathPrefix:"/fixture/old.pdf")));
        Assert.Equal(admitted.Document.Id, Assert.Single(originalResult.Results).DocumentId);
        Assert.Equal("/fixture/old.pdf", Assert.Single(originalResult.Results).Path);
        var metadataBefore = await MigrationTestSql.ScalarAsync(stores.State, "SELECT json_array(type_origin,props_original_json,mime,occurred_at,occurred_offset,occurred_basis,occurred_precision) FROM documents");
        var projectionBefore = (byte[])(await MigrationTestSql.ScalarAsync(stores.State, "SELECT bytes FROM payloads WHERE id=(SELECT payload_id FROM document_projections)"))!;
        await folders.PlanScanAsync(source, [], true);
        var moveCandidate = Assert.Single(await folders.PlanScanAsync(source, [entry with { RelativePath="new.pdf", Sha256=hashedScan ? hash : null }], hashedScan));
        Assert.Null(moveCandidate.DocumentId);
        // Shallow orchestration performs a deep uniqueness scan and persists its hash.
        moveCandidate = moveCandidate with { Entry = moveCandidate.Entry with { Sha256 = hash } };
        await folders.PersistPlanAsync(source, [new(moveCandidate, 1)]);
        var moved = (await folders.AdmitSourceAsync(source, moveCandidate, bytes, hash))!;
        await folders.CompleteCandidateAsync(source.Id, moveCandidate.Entry.RelativePath);
        await folders.CompleteScanAsync(source.Id);
        Assert.Equal(admitted.Document.Id, moved.Document.Id);
        Assert.Equal(metadataBefore, await MigrationTestSql.ScalarAsync(stores.State, "SELECT json_array(type_origin,props_original_json,mime,occurred_at,occurred_offset,occurred_basis,occurred_precision) FROM documents"));
        Assert.Equal(projectionBefore, (byte[])(await MigrationTestSql.ScalarAsync(stores.State, "SELECT bytes FROM payloads WHERE id=(SELECT payload_id FROM document_projections)"))!);
        Assert.Equal(7, Assert.Single(moved.Document.Chunks!).Page);
        Assert.Empty((await retrieval.SearchAsync(new("page", "keyword", new(PathPrefix:"/fixture/old.pdf")))).Results);
        Assert.Equal("/fixture/new.pdf", Assert.Single((await retrieval.SearchAsync(new("page", "keyword", new(PathPrefix:"/fixture/new.pdf")))).Results).Path);
        Assert.Equal(admitted.Document.Path, moved.Document.Path);
        await repository.RebuildAsync();
        Assert.Equal("/fixture/new.pdf", Assert.Single((await retrieval.SearchAsync(new("page", "keyword", new(PathPrefix:"/fixture/new.pdf")))).Results).Path);
        Assert.Equal("application/pdf", await MigrationTestSql.ScalarAsync(stores.Index, "SELECT mime FROM indexed_document_sources"));
    }

    [Fact]
    public async Task ChangedStagedMoveBytesDoNotOverwriteTheEnumeratedHashIdentity()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var folders = new SqliteFolderDocumentRepository(stores.State, coordinator, repository, files);
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "watch", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("changed-move-folder", "folder", "Changed", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var bytes = Encoding.UTF8.GetBytes("original bytes"); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var entry = new SourceFileEntry("old.txt", bytes.Length, DateTimeOffset.UtcNow, hash);
        var admitted = (await folders.AdmitSourceAsync(source, new(entry, null, 0), bytes, hash))!;
        await repository.CompleteFileAsync(admitted.Document.Id, 1, new("extracted", "text", "text/plain", "original bytes"));
        await folders.PlanScanAsync(source, [], true);
        var candidate = Assert.Single(await folders.PlanScanAsync(source, [entry with { RelativePath="new.txt" }], true));
        Assert.Null(candidate.DocumentId);
        await folders.PersistPlanAsync(source, [new(candidate, 1)]);
        var changed = Encoding.UTF8.GetBytes("different staged bytes");
        var changedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(changed));
        var replacement = (await folders.AdmitSourceAsync(source, candidate, changed, changedHash))!;
        Assert.NotEqual(admitted.Document.Id, replacement.Document.Id);
        await folders.CompleteCandidateAsync(source.Id, candidate.Entry.RelativePath);
        await folders.CompleteScanAsync(source.Id);
        var retained = (await repository.GetAsync(admitted.Document.Id))!;
        Assert.Equal(hash, retained.ContentHash); Assert.Equal(1, retained.Revision);
        Assert.Equal("removed_from_source", retained.Status);
    }

    [Fact]
    public async Task ChangedStagedHashCannotBorrowAnotherHashesUniqueMoveCount()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        var folders = new SqliteFolderDocumentRepository(stores.State, coordinator, repository, files);
        var config = new FolderSourceConfiguration("/fixture", ["*"], [], true, "watch", false, null, new Dictionary<string,string>(), "poll", 600);
        var source = new SourceRecord("changed-move-folder", "folder", "Changed", config, "active", null, null, DateTimeOffset.UtcNow);
        await new SqliteSourceRepository(stores.State).AddAsync(source);
        var bytes = Encoding.UTF8.GetBytes("original bytes"); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var entry = new SourceFileEntry("old.txt", bytes.Length, DateTimeOffset.UtcNow, hash);
        var admitted = (await folders.AdmitSourceAsync(source, new(entry, null, 0), bytes, hash))!;
        await repository.CompleteFileAsync(admitted.Document.Id, 1, new("extracted", "text", "text/plain", "original bytes"));
        var changed = Encoding.UTF8.GetBytes("different staged bytes");
        var changedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(changed));
        var otherEntry = entry with { RelativePath="other-old.txt", Sha256=changedHash };
        var other = (await folders.AdmitSourceAsync(source, new(otherEntry, null, 0), changed, changedHash))!;
        await repository.CompleteFileAsync(other.Document.Id, 1, new("extracted", "text", "text/plain", "different staged bytes"));
        await folders.PlanScanAsync(source, [], true);
        var candidates = await folders.PlanScanAsync(source,
            [entry with { RelativePath="new.txt" }, otherEntry with { RelativePath="other-new.txt" }], true);
        var candidate = Assert.Single(candidates, item => item.Entry.RelativePath=="new.txt");
        Assert.Null(candidate.DocumentId);
        await folders.PersistPlanAsync(source, [new(candidate, 1)]);
        var replacement = (await folders.AdmitSourceAsync(source, candidate, changed, changedHash))!;
        Assert.NotEqual(admitted.Document.Id, replacement.Document.Id);
        Assert.NotEqual(other.Document.Id, replacement.Document.Id);
        await folders.CompleteCandidateAsync(source.Id, candidate.Entry.RelativePath);
        await folders.CompleteScanAsync(source.Id);
        var retained = (await repository.GetAsync(admitted.Document.Id))!;
        Assert.Equal(hash, retained.ContentHash); Assert.Equal(1, retained.Revision);
        Assert.Equal("removed_from_source", retained.Status);
    }

    [Fact]
    public async Task ContentReplacementReportsPartialUntilNewVectorsComplete()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var capture = await repository.CaptureAsync(new("old text"), Actor);
        await MigrationTestSql.WriteAsync(stores.State, "UPDATE documents SET status='indexed'");
        var updated = await repository.MutateAsync(capture.Document.Id, capture.Document.Revision, "update", JsonSerializer.SerializeToElement(new { content="new text" }), Actor);
        Assert.Equal("indexed_partial", updated.Document.Status);
        Assert.Equal("new text", updated.Document.Text);
        Assert.Equal("indexed_partial", await MigrationTestSql.ScalarAsync(stores.Index, "SELECT status FROM indexed_documents"));
        Assert.Contains(await new SqliteEmbeddingWorkRepository(stores.State, stores.Index).PendingAsync("fixture-space"), work => work.DocumentId == capture.Document.Id && work.Revision == updated.Document.Revision);
    }

    private sealed class NoCursorProtection : SecondBrain.Core.Retrieval.IPayloadProtection
    {
        public string Protect(string purpose, string plaintext) => throw new NotSupportedException();
        public string Unprotect(string purpose, string protectedText) => throw new NotSupportedException();
    }

    private sealed class MutableFolderOptions : Microsoft.Extensions.Options.IOptionsMonitor<SecondBrain.Core.Configuration.SecondBrainOptions>
    {
        public SecondBrain.Core.Configuration.SecondBrainOptions CurrentValue { get; set; } = new();
        public SecondBrain.Core.Configuration.SecondBrainOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SecondBrain.Core.Configuration.SecondBrainOptions, string?> listener) => null;
    }

    private static async Task Seed(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync(); await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id=Actor.CredentialId,Name=Actor.CredentialName,Kind=Actor.Kind,Generation=1,AccountEpoch=1,Scopes="read,write,admin",Verifier="fixture",Kid="fixture",SteppedUpAt=AuthTime.Format(DateTimeOffset.UtcNow),CreatedAt=AuthTime.Format(DateTimeOffset.UtcNow) });
    }
    private sealed class InjectedFailure : Exception;
    private sealed class Crash : SecondBrain.Core.Durability.ICrashPoints
    {
        public ValueTask HitAsync(string name, CancellationToken cancellationToken = default) => name == SecondBrain.Core.Durability.CrashPointNames.AfterIndexCommit ? throw new InjectedFailure() : ValueTask.CompletedTask;
    }
}
