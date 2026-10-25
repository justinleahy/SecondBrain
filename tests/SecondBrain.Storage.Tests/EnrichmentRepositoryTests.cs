using System.Text.Json;
using System.Text;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Sources;
using SecondBrain.Extraction.Contracts;
using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Core.Providers;
using Microsoft.Extensions.AI;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class EnrichmentRepositoryTests
{
    [Fact]
    public async Task EnrichmentPublishesAllSuggestionsWithoutChangingOriginalBytesOrRevision()
    {
        await using var f = await Fixture.Create();
        var note = await f.Capture(); var original = await File.ReadAllBytesAsync(note.Path!);
        var job = await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint);
        var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        Assert.Equal(job.Id, work.JobId);
        Assert.True(await f.Enrichment.PublishAsync(work, Result(f.Fingerprint)));
        var enriched = (await f.Documents.GetAsync(note.Id, true))!;
        Assert.Equal(note.Revision, enriched.Revision); Assert.Equal(original, await File.ReadAllBytesAsync(note.Path!));
        Assert.Contains("generated", enriched.Tags!); Assert.Equal("A useful summary.", enriched.Enrichment!.Summary);
        await using var read = await f.Stores.Index.OpenReadConnectionAsync();
        Assert.Equal("A useful summary.", await read.Connection.ExecuteScalarAsync<string>("SELECT summary FROM document_text WHERE document_id=@id", new { id = note.Id }));
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mentions WHERE document_id=@id", new { id = note.Id }));
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM enrichment_links WHERE document_id=@id", new { id = note.Id }));
        Assert.Equal(0, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM links WHERE from_document_id=@id", new { id = note.Id }));
        Assert.Null(await f.Enrichment.ClaimAsync(f.Fingerprint));
        Assert.Equal("completed", (await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserTagOverrideIsPreservedWhileModelTagOriginRemainsRecorded(bool writeback)
    {
        await using var f = await Fixture.Create(); f.Options.CurrentValue.Enrichment.WriteBackFrontmatter = writeback; var note = await f.Capture();
        note = (await f.Documents.MutateAsync(note.Id, note.Revision, "patch", JsonSerializer.SerializeToElement(new { tags = new[] { "kept" } }), f.Actor)).Document;
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint); var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await f.Enrichment.PublishAsync(work, Result(f.Fingerprint));
        Assert.Equal(new[] { "kept" }, (await f.Documents.GetAsync(note.Id, true))!.Tags);
        await using var read = await f.Stores.Index.OpenReadConnectionAsync();
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tag_assertions WHERE document_id=@id AND tag='generated' AND origin='llm'", new { id = note.Id }));
        Assert.Equal(0, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM effective_tags WHERE document_id=@id AND tag='generated'", new { id = note.Id }));
    }

    [Fact]
    public async Task ChangedDocumentOrRevokedActorPreventsLatePublication()
    {
        await using var f = await Fixture.Create(); var note = await f.Capture();
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint); var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await f.Documents.MutateAsync(note.Id, note.Revision, "patch", JsonSerializer.SerializeToElement(new { title = "Changed" }), f.Actor);
        Assert.False(await f.Enrichment.PublishAsync(work, Result(f.Fingerprint)));
        Assert.Null((await f.Documents.GetAsync(note.Id, true))!.Enrichment);
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint); work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await MigrationTestSql.WriteAsync(f.Stores.State, "UPDATE credentials SET revoked_at='2020-01-01T00:00:00Z' WHERE id='actor'");
        Assert.False(await f.Enrichment.CheckAsync(work));
        Assert.False(await f.Enrichment.PublishAsync(work, Result(f.Fingerprint)));
    }

    [Fact]
    public async Task InterruptedIndexPublicationRecoversTheSavedResultWithoutRegeneration()
    {
        await using var f = await Fixture.Create(); var note = await f.Capture();
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint); var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        f.Crashes.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => f.Enrichment.PublishAsync(work, Result(f.Fingerprint)));
        await MigrationTestSql.WriteAsync(f.Stores.State, "UPDATE jobs SET status='queued',lease_until=NULL WHERE kind='enrich' AND status='running'");
        var restarted = new SqliteEnrichmentRepository(f.Stores.State, f.Stores.Index, f.Publication, f.Options, f.Clock);
        var recovered = (await restarted.ClaimAsync(f.Fingerprint))!;
        Assert.NotNull(recovered.CachedResult);
        Assert.True(await restarted.PublishAsync(recovered, recovered.CachedResult));
        Assert.Equal("A useful summary.", (await f.Documents.GetAsync(note.Id, true))!.Enrichment!.Summary);
    }

    [Fact]
    public async Task PurgeRemovesEnrichmentFromIndexAndLivePayloads()
    {
        await using var f = await Fixture.Create(); var note = await f.Capture();
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint); var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await f.Enrichment.PublishAsync(work, Result(f.Fingerprint) with { Summary = "unique-enrichment-secret" });
        await f.Documents.MutateAsync(note.Id, note.Revision, "purge", JsonSerializer.SerializeToElement(new { }), f.Actor);
        await using var read = await f.Stores.Index.OpenReadConnectionAsync();
        foreach (var table in new[] { "document_text", "tag_assertions", "mentions", "enrichment_links" })
            Assert.Equal(0, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM " + table + " WHERE document_id=@id", new { id = note.Id }));
        await using var state = await f.Stores.State.OpenReadConnectionAsync();
        Assert.Equal(0, await state.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payloads WHERE redacted_at IS NULL AND CAST(bytes AS TEXT) LIKE '%unique-enrichment-secret%'"));
    }

    [Fact]
    public async Task AutomaticWorkRequiresInferenceAuthorityAndBudgetFailuresBackOff()
    {
        await using var f = await Fixture.Create(); await f.Capture();
        await MigrationTestSql.WriteAsync(f.Stores.State, "UPDATE credentials SET scopes='read,write' WHERE id='actor'");
        Assert.Null(await f.Enrichment.ClaimAsync(f.Fingerprint));
        await MigrationTestSql.WriteAsync(f.Stores.State, "UPDATE credentials SET scopes='read,write,infer,admin' WHERE id='actor'");
        var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await f.Enrichment.FailAsync(work, "budget", true);
        Assert.Null(await f.Enrichment.ClaimAsync(f.Fingerprint));
        f.Clock.Now = f.Clock.Now.AddMinutes(16);
        Assert.NotNull(await f.Enrichment.ClaimAsync(f.Fingerprint));
    }

    [Fact]
    public async Task ExpiredLeaseCannotCancelNewWorkAndAutomaticWorkResumesAfterReenable()
    {
        await using var f = await Fixture.Create(); await f.Capture();
        var original = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        f.Clock.Now = f.Clock.Now.AddMinutes(4);
        Assert.False(await f.Enrichment.CheckAsync(original));
        await f.Enrichment.FailAsync(original, "timeout", true);
        var recovered = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        Assert.NotEqual(original.Lease, recovered.Lease);
        await f.Enrichment.FailAsync(original, "superseded", false);
        Assert.True(await f.Enrichment.CheckAsync(recovered));
        f.Options.CurrentValue.Enrichment.Enabled = false;
        Assert.False(await f.Enrichment.CheckAsync(recovered));
        await f.Enrichment.FailAsync(recovered, "configuration_changed", false);
        f.Options.CurrentValue.Enrichment.Enabled = true;
        Assert.NotNull(await f.Enrichment.ClaimAsync(f.Fingerprint));
    }

    [Fact]
    public async Task EnabledWritebackCreatesOneJournaledRevisionAndKeepsTagsAndAttribution()
    {
        await using var f = await Fixture.Create(); f.Options.CurrentValue.Enrichment.WriteBackFrontmatter = true;
        var note = await f.Capture();
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint);
        var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        Assert.True(await f.Enrichment.PublishAsync(work, Result(f.Fingerprint)));
        var result = (await f.Documents.GetAsync(note.Id, true))!;
        Assert.Equal(2, result.Revision); Assert.True(result.Enrichment!.WrittenBack);
        Assert.EndsWith("Original document body.", await File.ReadAllTextAsync(result.Path!));
        Assert.Contains("secondbrain_enrichment:", result.Text);
        Assert.Contains("base", result.Tags!); Assert.Contains("generated", result.Tags!);
        Assert.Null(await f.Enrichment.ClaimAsync(f.Fingerprint));
        Assert.Equal("completed", (await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint)).Status);
        await using var read = await f.Stores.State.OpenReadConnectionAsync();
        Assert.Equal("enrichment", await read.Connection.ExecuteScalarAsync<string>("SELECT cause FROM revisions WHERE document_id=@id AND revision=2", new { id = note.Id }));
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mutations WHERE kind='write_back' AND status='finalized'"));
        Assert.Equal("actor", await read.Connection.ExecuteScalarAsync<string>("SELECT initiating_credential_id FROM jobs WHERE document_id=@id AND revision=2 AND kind='write_back'", new { id = note.Id }));
    }

    [Fact]
    public async Task EnablingWritebackUsesAnExistingResultWithoutCallingTheModel()
    {
        await using var f = await Fixture.Create(); var note = await f.Capture();
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint);
        var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await f.Enrichment.PublishAsync(work, Result(f.Fingerprint));
        await MigrationTestSql.WriteAsync(f.Stores.State, "UPDATE credentials SET revoked_at='2020-01-01T00:00:00Z' WHERE id='actor'");
        f.Options.CurrentValue.Enrichment.WriteBackFrontmatter = true;
        var next = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        Assert.NotNull(next.CachedResult); Assert.Null(next.Actor);
        await f.Enrichment.PublishAsync(next, next.CachedResult);
        Assert.Equal(2, (await f.Documents.GetAsync(note.Id, true))!.Revision);
        Assert.Null(await f.Enrichment.ClaimAsync(f.Fingerprint));
    }

    [Fact]
    public async Task WritebackRecoversAfterRenameWithoutDuplicatingTheRevision()
    {
        await using var f = await Fixture.Create(); f.Options.CurrentValue.Enrichment.WriteBackFrontmatter = true;
        var note = await f.Capture(); await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint);
        var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        f.Crashes.Point = MutationCrashPointNames.AfterRename; f.Crashes.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => f.Enrichment.PublishAsync(work, Result(f.Fingerprint)));
        await f.Journal.RecoverAsync(); await f.Publication.RecoverAsync();
        f.Clock.Now = f.Clock.Now.AddMinutes(4);
        Assert.Null(await f.Enrichment.ClaimAsync(f.Fingerprint));
        Assert.Equal(2, (await f.Documents.GetAsync(note.Id, true))!.Revision);
        await using var read = await f.Stores.State.OpenReadConnectionAsync();
        Assert.Equal("completed", await read.Connection.ExecuteScalarAsync<string>("SELECT status FROM jobs WHERE id=@id", new { id = work.JobId }));
        Assert.Equal(2, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM revisions WHERE document_id=@id", new { id = note.Id }));
    }

    [Fact]
    public async Task JournalChecksWritebackPermissionAtPreparation()
    {
        await using var f = await Fixture.Create(); var note = await f.Capture();
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint); var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Journal.PrepareAsync(new MutationWriteRequest
        {
            MutationId = "writeback-test", OperationId = "writeback-test", Kind = "write_back", Destination = note.Path!, Content = Encoding.UTF8.GetBytes("changed"),
            ExpectedHash = note.ContentHash, DocumentReferences = [note.Id],
            Finalization = new() { DocumentId = note.Id, RevisionBefore = 1, RevisionAfter = 2, RevisionCause = "enrichment", GenerationJson = work.GenerationJson,
                EnrichmentWriteback = new(work.JobId, work.Lease, work.Fingerprint, work.SourceStamp) }
        }).AsTask());
        Assert.Equal("Original document body.", await File.ReadAllTextAsync(note.Path!));
    }

    [Fact]
    public async Task EnrichmentNeverWritesToAnExternalSourcePath()
    {
        await using var f = await Fixture.Create(); f.Options.CurrentValue.Enrichment.WriteBackFrontmatter = true;
        var note = await f.Capture(); var external = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".md");
        try
        {
            await File.WriteAllTextAsync(external, "External file stays unchanged.");
            await f.Stores.State.QueueWriteAsync(async (c, t, ct) => await c.ExecuteAsync(new CommandDefinition(
                "UPDATE payloads SET bytes=CAST(json_set(CAST(bytes AS TEXT),'$.Path',@external) AS BLOB) WHERE id=(SELECT payload_id FROM document_projections WHERE document_id=@id)",
                new { id = note.Id, external }, t, cancellationToken: ct)));
            await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint); var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
            Assert.True(await f.Enrichment.PublishAsync(work, Result(f.Fingerprint)));
            Assert.Equal("External file stays unchanged.", await File.ReadAllTextAsync(external));
            Assert.Equal(1, (await f.Documents.GetAsync(note.Id, true))!.Revision);
            Assert.Null(await f.Enrichment.ClaimAsync(f.Fingerprint));
        }
        finally { File.Delete(external); }
    }

    [Fact]
    public async Task ConcurrentManagedFileReplacementIsPreservedAsAConflict()
    {
        await using var f = await Fixture.Create(); f.Options.CurrentValue.Enrichment.WriteBackFrontmatter = true;
        var note = await f.Capture(); await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint);
        var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        f.Extractor.OnInspect = () =>
        {
            var replacement = note.Path! + ".external";
            File.WriteAllText(replacement, "A newer human edit."); File.Move(replacement, note.Path!, true);
        };
        await Assert.ThrowsAsync<ManagedFileConflictException>(() => f.Enrichment.PublishAsync(work, Result(f.Fingerprint)));
        Assert.Equal("A newer human edit.", await File.ReadAllTextAsync(note.Path!));
    }

    [Fact]
    public async Task WritebackBackfillHonorsTheQueueLimitWhenReusingCompletedJobs()
    {
        await using var f = await Fixture.Create(); var note = await f.Capture();
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint); var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await f.Enrichment.PublishAsync(work, Result(f.Fingerprint));
        f.Options.CurrentValue.Enrichment.WriteBackFrontmatter = true;
        f.Options.CurrentValue.Limits.Global.QueuedJobs = 0;
        Assert.Equal("capacity", (await Assert.ThrowsAsync<IngestionConflictException>(() => f.Enrichment.ClaimAsync(f.Fingerprint))).Code);
        Assert.Equal("capacity", (await Assert.ThrowsAsync<IngestionConflictException>(() => f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint))).Code);
        f.Options.CurrentValue.Limits.Global.QueuedJobs = 100;
        Assert.NotNull(await f.Enrichment.ClaimAsync(f.Fingerprint));
    }

    [Theory]
    [InlineData("import")]
    [InlineData("index")]
    public async Task FolderScansCompareOriginalSourceHashAfterManagedWriteback(string mode)
    {
        await using var f = await Fixture.Create();
        var folder = await f.Folder(mode); var note = folder.Document;
        f.Options.CurrentValue.Enrichment.WriteBackFrontmatter = true;
        await f.Enrichment.RequestAsync(note.Id, f.Actor, f.Fingerprint); var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await f.Enrichment.PublishAsync(work, Result(f.Fingerprint));
        var written = (await f.Documents.GetAsync(note.Id, true))!;
        Assert.NotEqual(folder.Entry.Sha256, written.ContentHash);
        Assert.Empty(await folder.Repository.PlanScanAsync(folder.Source, [folder.Entry], true));
        Assert.Equal(written.Revision, (await folder.Repository.AdmitSourceAsync(folder.Source, new(folder.Entry, note.Id, written.Revision), folder.Bytes, folder.Entry.Sha256!))!.Document.Revision);
        var changed = Encoding.UTF8.GetBytes("A changed source body."); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(changed));
        var entry = folder.Entry with { Sha256 = hash, Length = changed.Length };
        var candidate = Assert.Single(await folder.Repository.PlanScanAsync(folder.Source, [entry], true));
        var imported = await folder.Repository.AdmitSourceAsync(folder.Source, candidate, changed, hash);
        Assert.Equal(hash, imported!.Document.ContentHash);
        Assert.Equal(written.Revision + 1, imported.Document.Revision);
        Assert.DoesNotContain("secondbrain_enrichment:", await File.ReadAllTextAsync(imported.Document.Path!));
    }

    [Fact]
    public async Task MovingAnUnchangedSourceKeepsItsWrittenBackCopyAndIdentity()
    {
        await using var f = await Fixture.Create(); var folder = await f.Folder("index");
        f.Options.CurrentValue.Enrichment.WriteBackFrontmatter = true;
        await f.Enrichment.RequestAsync(folder.Document.Id, f.Actor, f.Fingerprint); var work = (await f.Enrichment.ClaimAsync(f.Fingerprint))!;
        await f.Enrichment.PublishAsync(work, Result(f.Fingerprint));
        var before = (await f.Documents.GetAsync(folder.Document.Id, true))!;
        var moved = folder.Entry with { RelativePath = "moved.md" };
        var candidate = Assert.Single(await folder.Repository.PlanScanAsync(folder.Source, [moved], true));
        var after = (await folder.Repository.AdmitSourceAsync(folder.Source, candidate, folder.Bytes, moved.Sha256!))!.Document;
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.ContentHash, after.ContentHash);
        Assert.True(after.Enrichment!.WrittenBack);
        Assert.Empty(await folder.Repository.PlanScanAsync(folder.Source, [moved], true));
    }

    [Fact]
    public async Task NativeBackfillReservesBeforeUploadAndPublishesIndividualResults()
    {
        await using var f=await Fixture.Create(); var native=new NativeBatch(f);var first=await f.Capture();var second=await f.Capture();
        var worker=native.Worker(); Assert.True(await worker.ProcessAsync());
        await using(var read=await f.Stores.State.OpenReadConnectionAsync()) {
            Assert.Equal(2,await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM usage WHERE role='enrich' AND settled=0"));
            Assert.Equal(2,await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM jobs WHERE json_extract(payload_json,'$.batchId') IS NOT NULL AND status='running'"));
        }
        Assert.Equal(0,native.Stages);await Drive(worker,f);
        Assert.Equal(1,native.Stages);Assert.Equal(1,native.Submits);Assert.Equal(1,native.Cleanups);
        Assert.Equal("Batch summary",(await f.Documents.GetAsync(first.Id,true))!.Enrichment!.Summary);
        Assert.Equal("Batch summary",(await f.Documents.GetAsync(second.Id,true))!.Enrichment!.Summary);
        await using var state=await f.Stores.State.OpenReadConnectionAsync();
        Assert.Equal(2,await state.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM usage WHERE settled=1 AND input_tokens=11 AND output_tokens=3"));
        Assert.Equal("done",await state.Connection.ExecuteScalarAsync<string>("SELECT status FROM batch_jobs"));
        Assert.Equal(0,await state.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payload_refs WHERE referrer_kind='batch_job'"));
        Assert.Equal(0,await state.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM batch_jobs WHERE request_payload_id IS NOT NULL OR result_payload_id IS NOT NULL"));
    }

    [Fact]
    public async Task LostSubmissionResponseIsReconciledAfterRestartWithoutResubmission()
    {
        await using var f=await Fixture.Create();var native=new NativeBatch(f) {LoseSubmitResponse=true};var document=await f.Capture();
        var worker=native.Worker();for(var i=0;i<3;i++) await worker.ProcessAsync();
        Assert.Equal(1,native.Submits);Assert.Equal("submitting",(await f.Enrichment.ReadPendingAsync())!.State.Phase);
        f.Clock.Now=f.Clock.Now.AddMinutes(2);
        var restarted=new SqliteEnrichmentRepository(f.Stores.State,f.Stores.Index,f.Publication,f.Options,f.Clock);
        await Drive(new(restarted,restarted,native,f.Options,f.Clock),f);
        Assert.Equal(1,native.Submits);Assert.Equal(1,native.Finds);
        Assert.NotNull((await f.Documents.GetAsync(document.Id,true))!.Enrichment);
    }

    [Fact]
    public async Task LostUploadResponseReusesTheStagedFile()
    {
        await using var f=await Fixture.Create();var native=new NativeBatch(f) {LoseStageResponse=true};await f.Capture();
        var worker=native.Worker();await worker.ProcessAsync();await worker.ProcessAsync();
        Assert.Equal("staging",(await f.Enrichment.ReadPendingAsync())!.State.Phase);
        await Drive(worker,f);Assert.Equal(1,native.Stages);Assert.Equal(1,native.Submits);Assert.Equal(1,native.StageFinds);
    }

    [Fact]
    public async Task InlineStagingCanRecoverBeforeAnyRemoteSubmission()
    {
        await using var f=await Fixture.Create();var native=new NativeBatch(f,inline:true);var document=await f.Capture();var worker=native.Worker();
        await worker.ProcessAsync();var pending=(await f.Enrichment.ReadPendingAsync())!;
        await f.Enrichment.SaveAsync(pending with {State=pending.State with {Phase="staging"}});
        await Drive(worker,f);
        Assert.Equal(0,native.Stages);Assert.Equal(1,native.StageFinds);Assert.Equal(1,native.Submits);
        Assert.NotNull((await f.Documents.GetAsync(document.Id,true))!.Enrichment);
    }

    [Fact]
    public async Task UnrecoverableSubmissionFailsWithoutAutomaticResubmissionOrRefund()
    {
        await using var f=await Fixture.Create();var native=new NativeBatch(f,inline:true) {LoseSubmitResponse=true,Unrecoverable=true};var document=await f.Capture();var worker=native.Worker();
        for(var i=0;i<3;i++) await worker.ProcessAsync();
        f.Clock.Now=f.Clock.Now.AddHours(49);await worker.ProcessAsync();
        Assert.Null(await f.Enrichment.ReadPendingAsync());Assert.False(await worker.ProcessAsync());Assert.Equal(1,native.Submits);
        Assert.Null((await f.Documents.GetAsync(document.Id,true))!.Enrichment);
        await using var read=await f.Stores.State.OpenReadConnectionAsync();
        Assert.Equal("failed",await read.Connection.ExecuteScalarAsync<string>("SELECT status FROM batch_jobs"));
        Assert.Equal(1,await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM usage u JOIN usage_calls c ON c.usage_id=u.id WHERE settled=1 AND charged_tokens=reserved_tokens"));
    }

    [Fact]
    public async Task BudgetFailureRollsBackEveryReservationBeforeAnyProviderTraffic()
    {
        await using var f=await Fixture.Create();var native=new NativeBatch(f);await f.Capture();await f.Capture();f.Options.CurrentValue.Budgets.DailyTokens=1;
        Assert.True(await native.Worker().ProcessAsync());Assert.Equal(0,native.Stages);Assert.Null(await f.Enrichment.ReadPendingAsync());
        await using var read=await f.Stores.State.OpenReadConnectionAsync();Assert.Equal(0,await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM usage"));
    }

    [Fact]
    public async Task ExplicitEnrichmentStaysInTheInteractiveQueue()
    {
        await using var f=await Fixture.Create();var native=new NativeBatch(f);var document=await f.Capture();
        await f.Enrichment.RequestAsync(document.Id,f.Actor,f.Fingerprint);
        Assert.False(await native.Worker().ProcessAsync());Assert.Equal(0,native.Stages);
        Assert.NotNull(await f.Enrichment.ClaimInteractiveAsync(f.Fingerprint));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("credential")]
    [InlineData("disabled")]
    [InlineData("purge")]
    public async Task StaleBatchCancelsWithoutPublishingAndRetainsItsCharge(string change)
    {
        await using var f=await Fixture.Create();var native=new NativeBatch(f);var document=await f.Capture();var worker=native.Worker();
        for(var i=0;i<3;i++) await worker.ProcessAsync();
        if(change=="revision") await f.Documents.MutateAsync(document.Id,document.Revision,"patch",JsonSerializer.SerializeToElement(new {title="Changed"}),f.Actor);
        if(change=="credential") await MigrationTestSql.WriteAsync(f.Stores.State,"UPDATE credentials SET revoked_at='2020-01-01T00:00:00Z' WHERE id='actor'");
        if(change=="disabled") f.Options.CurrentValue.Enrichment.Enabled=false;
        if(change=="purge") await f.Documents.MutateAsync(document.Id,document.Revision,"purge",JsonSerializer.SerializeToElement(new {}),f.Actor);
        await Drive(worker,f);Assert.Equal(1,native.Cancels);Assert.Equal(0,native.Collections);
        await using var read=await f.Stores.State.OpenReadConnectionAsync();
        Assert.Equal(1,await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM usage u JOIN usage_calls c ON c.usage_id=u.id WHERE settled=1 AND charged_tokens=reserved_tokens"));
        Assert.Equal("cancelled",await read.Connection.ExecuteScalarAsync<string>("SELECT status FROM batch_jobs"));
    }

    [Fact]
    public async Task PublicationCrashReusesCollectedResultsWithoutAnotherRemoteCall()
    {
        await using var f=await Fixture.Create();var native=new NativeBatch(f);var document=await f.Capture();var worker=native.Worker();
        for(var i=0;i<5;i++) await worker.ProcessAsync();
        Assert.Equal("publishing",(await f.Enrichment.ReadPendingAsync())!.State.Phase);
        f.Crashes.Armed=true;
        await Assert.ThrowsAsync<IOException>(()=>worker.ProcessAsync());
        await Drive(worker,f);
        Assert.NotNull((await f.Documents.GetAsync(document.Id,true))!.Enrichment);
        Assert.Equal(1,native.Collections);Assert.Equal(1,native.Submits);
    }

    private static async Task Drive(EnrichmentBatchProcessor worker,Fixture f)
    {
        for(var i=0;i<16 && await f.Enrichment.ReadPendingAsync() is not null;i++) {f.Clock.Now=f.Clock.Now.AddMinutes(2);await worker.ProcessAsync();}
        Assert.Null(await f.Enrichment.ReadPendingAsync());
    }
    private sealed class NativeBatch : IProviderRegistry,IProviderBinding,IBatchProvider
    {
        private readonly Fixture f;public int Stages,Submits,Finds,StageFinds,Cancels,Collections,Cleanups; public bool LoseSubmitResponse,LoseStageResponse,Unrecoverable;private readonly bool inline;
        private bool staged,submitted; private BatchChatRequest[] requests=[];
        public NativeBatch(Fixture fixture,bool inline=false) {
            f=fixture;this.inline=inline;f.Options.CurrentValue.Providers["batch"]=new() {Kind=inline?"anthropic":"openai",Endpoint="https://batch.example.invalid/v1"};
            f.Options.CurrentValue.Models.Enrich=new() {Provider="batch",Model="batch-model",Capabilities=new() {Batch=true,StructuredOutput=true},Limits=new() {ContextTokens=32768,MaxOutputTokens=2048}};
        }
        public EnrichmentBatchProcessor Worker()=>new(f.Enrichment,f.Enrichment,this,f.Options,f.Clock);
        public async Task<string?> StageAsync(string key,IReadOnlyList<BatchChatRequest> input,CancellationToken token=default) {
            Stages++;requests=input.ToArray();staged=true;
            await using var read=await f.Stores.State.OpenReadConnectionAsync(token);
            Assert.Equal(input.Count,await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM usage WHERE settled=0"));
            if(LoseStageResponse) throw new ProviderRequestException("provider-unavailable","lost response",true);
            return inline?"inline-"+key:"file-input";
        }
        public Task<string?> FindStagedAsync(string key,CancellationToken token=default) {StageFinds++;return Task.FromResult(inline?"inline-"+key:staged?(string?)"file-input":null);}
        public Task<BatchReceipt> SubmitAsync(string key,string? file,IReadOnlyList<BatchChatRequest> input,CancellationToken token=default) {
            Submits++;submitted=true;requests=input.ToArray();if(LoseSubmitResponse)throw new ProviderRequestException("provider-unavailable","lost response",true);
            return Task.FromResult(new BatchReceipt("batch-remote",file));
        }
        public Task<BatchReceipt?> FindAsync(string key,CancellationToken token=default) {Finds++;return Task.FromResult(submitted&&!Unrecoverable?new BatchReceipt("batch-remote",inline?"inline-"+key:"file-input"):null);}
        public Task<BatchSnapshot> PollAsync(BatchReceipt receipt,CancellationToken token=default)=>Task.FromResult(new BatchSnapshot(receipt,Cancels>0?"cancelled":"completed",true,"file-output"));
        public Task CancelAsync(BatchReceipt receipt,CancellationToken token=default) {Cancels++;return Task.CompletedTask;}
        public Task<IReadOnlyList<BatchChatResult>> CollectAsync(BatchSnapshot snapshot,IReadOnlyList<string> ids,CancellationToken token=default) {
            Collections++;return Task.FromResult<IReadOnlyList<BatchChatResult>>(requests.Reverse().Select(x=>new BatchChatResult(x.Id,"{\"summary\":\"Batch summary\",\"tags\":[],\"mentions\":[],\"suggested_links\":[]}",new(11,3,0,14),null)).ToArray());
        }
        public Task DeleteFilesAsync(BatchSnapshot snapshot,CancellationToken token=default) {Cleanups++;return Task.CompletedTask;}
        public bool IsCurrentConfiguration(SecondBrainOptions options)=>true;
        public ProviderRoleBinding GetRole(ModelRole role)=>new(role,this,new("batch","batch-model",new() {Batch=true,StructuredOutput=true},new() {ContextTokens=32768,MaxOutputTokens=2048}));
        public IProviderBinding GetProvider(string name)=>this;public IReadOnlyList<string> ProviderNames=>["batch"];
        ValueTask<IReadOnlyDictionary<string,IReadOnlyList<string>>> IProviderRegistry.ListModelsAsync(CancellationToken token)=>throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<ModelRole,ProviderHealthResult>> TestAsync(CancellationToken token=default)=>throw new NotSupportedException();
        public string ProviderName=>"batch";public Uri Endpoint=>new("https://batch.example.invalid/v1");public bool IsLocal=>false;
        public ValueTask<ResolvedModel> ResolveAsync(string model,ModelLimits? limits=null,CancellationToken token=default)=>ValueTask.FromResult(GetRole(ModelRole.Enrich).Model);
        public ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken token=default)=>throw new NotSupportedException();
        public ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken token=default)=>throw new NotSupportedException();
        public IChatClient? GetChatClient(string model)=>null;
        public IEmbeddingGenerator<string,Embedding<float>>? GetEmbeddingGenerator(string model)=>null;
        public IBatchProvider GetBatchProvider(string model)=>this;
    }

    private static DocumentEnrichment Result(string fingerprint) => new(fingerprint, "A useful summary.", ["generated"], [new("person", "Ada")], [new("Related plan", "Useful context")]);
    private sealed class Crash : ICrashPoints
    {
        public bool Armed; public string Point = CrashPointNames.AfterStatePublishing;
        public ValueTask HitAsync(string point, CancellationToken cancellationToken = default)
        { if (Armed && point == Point) { Armed = false; throw new IOException("Injected interruption"); } return ValueTask.CompletedTask; }
    }
    private sealed class PlainExtractor : IExtractorClient
    {
        public Action? OnInspect;
        public async ValueTask<ExtractionResult> ExtractAsync(Stream input, string hint, CancellationToken token = default)
        {
            using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync(token); OnInspect?.Invoke();
            return new ExtractionResult("extracted", "markdown", "text/markdown", text) { Frontmatter = new(0) };
        }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Options : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue { get; } = new();
        public SecondBrainOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root = new();
        public MigrationTestStores Stores { get; private set; } = null!;
        public Clock Clock { get; } = new(); public Options Options { get; } = new(); public Crash Crashes { get; } = new();
        public PublicationCoordinator Publication { get; private set; } = null!;
        public PlainExtractor Extractor { get; } = new();
        public MutationJournal Journal => journal;
        private InputStager stager = null!;
        private MutationJournal journal = null!; private ManagedDocumentFiles files = null!;
        public SqliteDocumentRepository Documents { get; private set; } = null!;
        public SqliteEnrichmentRepository Enrichment { get; private set; } = null!;
        public IngestionAuthority Actor { get; } = new("actor", 1, 1, "actor", "session");
        public string Fingerprint => EnrichmentPolicy.Fingerprint(Options.CurrentValue);
        public async Task<DocumentRecord> Capture() => (await Documents.CaptureAsync(new("Original document body.", "Example", Tags: ["base"]), Actor)).Document;
        public async Task<(SqliteFolderDocumentRepository Repository, SourceRecord Source, DocumentRecord Document, SourceFileEntry Entry, byte[] Bytes)> Folder(string mode)
        {
            var source = new SourceRecord("test-folder", "folder", "Folder", new(root.Path, ["**"], [], true, mode, true, null, new Dictionary<string,string>(), "poll", 60), "active", null, null, Clock.Now);
            await new SqliteSourceRepository(Stores.State).AddAsync(source);
            var repository = new SqliteFolderDocumentRepository(Stores.State, Publication, Documents, files, optionsMonitor: Options);
            var bytes = Encoding.UTF8.GetBytes("Source body.");
            var entry = new SourceFileEntry("source.md", bytes.Length, Clock.Now, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
            var admitted = (await repository.AdmitSourceAsync(source, new(entry, null, 0), bytes, entry.Sha256!))!;
            var document = (await Documents.CompleteFileAsync(admitted.Document.Id, admitted.Document.Revision, new("extracted", "markdown", "text/markdown", "Source body."))).Document;
            return (repository, source, document, entry, bytes);
        }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); f.Stores = new(f.root.Path); await f.Stores.Runner.MigrateAsync();
            await MigrationTestSql.WriteAsync(f.Stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
            await new AuthRepository(f.Stores.State, f.Clock).AddAsync(new CredentialRecord { Id = "actor", Name = "actor", Kind = "session", Generation = 1, AccountEpoch = 1, Scopes = "read,write,infer,admin", Verifier = "test", Kid = "test", CreatedAt = AuthTime.Format(f.Clock.Now), SteppedUpAt = AuthTime.Format(f.Clock.Now) });
            f.journal = new(f.Stores.State, f.root.Path, f.Crashes, f.Options, f.Clock);
            f.stager = new(Path.Combine(f.root.Path, "staging")); f.files = new(f.root.Path, f.journal);
            f.Publication = new(f.Stores.State, f.Stores.Index, f.Crashes, optionsMonitor: f.Options, clock: f.Clock);
            f.Documents = new(f.Stores.State, f.Stores.Index, f.Publication, f.files, optionsMonitor: f.Options);
            f.Enrichment = new(f.Stores.State, f.Stores.Index, f.Publication, f.Options, f.Clock, f.files, f.stager, f.Extractor); return f;
        }
        public async ValueTask DisposeAsync() { files.Dispose(); stager.Dispose(); journal.Dispose(); await Stores.DisposeAsync(); root.Dispose(); }
    }
}
