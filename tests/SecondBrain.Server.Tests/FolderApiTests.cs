using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Core.Sources;
using SecondBrain.Server.Sources;
using SecondBrain.Server.Auth;
using SecondBrain.MockProvider;
using SecondBrain.Server.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace SecondBrain.Server.Tests;

public sealed class FolderApiTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SteppedUpSessionCanDisableReactivateAndRemoveSource()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password"));
        Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Remove(csrf.HeaderName);
        client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        var path = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "lifecycle")).FullName;
        using var create = await client.PostAsJsonAsync("/v1/sources", new CreateSourceRequest { Path = path, Watch = "poll", PollIntervalSeconds = 600 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var source = (await create.Content.ReadFromJsonAsync<SourceRecord>())!;
        using var disable = await client.PatchAsJsonAsync("/v1/sources/" + source.Id, new SourceStatusRequest("disabled"));
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        using var reactivate = await client.PatchAsJsonAsync("/v1/sources/" + source.Id, new SourceStatusRequest("active"));
        Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);
        using var remove = await client.DeleteAsync("/v1/sources/" + source.Id);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Equal("removed", (await client.GetFromJsonAsync<SourceRecord[]>("/v1/sources"))!.Single(item => item.Id == source.Id).Status);
    }

    [Fact]
    public async Task SourceDisableAndRemovalRejectApiKeysEvenWithAdminScope()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory);
        using var disable = await client.PatchAsJsonAsync("/v1/sources/inbox", new SourceStatusRequest("disabled"));
        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
        using var remove = await client.DeleteAsync("/v1/sources/inbox");
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
    }

    [Theory]
    [InlineData("events", 30, 10)]
    [InlineData("poll", 2, 4)]
    public async Task AutomaticSourceEditsReachSearchWithinTimingBudget(string watch, int pollSeconds, int budgetSeconds)
    {
        // Use wall-clock scheduling: this fixture ordinarily supplies a fake clock for authentication tests.
        await using var factory = new LaneDWebFactory(configureServices: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(TimeProvider.System)));
        using var client = await ClientAsync(factory);
        var path = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "automatic-" + watch)).FullName;
        var markdown = Path.Combine(path, "note.md");
        await File.WriteAllTextAsync(markdown, "Initial tarsier content.");
        using var created = await client.PostAsJsonAsync("/v1/sources", new CreateSourceRequest { Path = path, Name = "automatic-" + watch, Watch = watch, PollIntervalSeconds = pollSeconds });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var initial = await WaitForHitAsync(client, "tarsier");
        var elapsed = Stopwatch.StartNew();
        await File.WriteAllTextAsync(markdown, "Edited numbat markdown content reaches search automatically.");
        SearchHit? edited = null;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(budgetSeconds))
        {
            using var response = await client.PostAsJsonAsync("/v1/search", new SearchRequest("numbat", "keyword"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            edited = (await response.Content.ReadFromJsonAsync<SearchResponse>())!.Results.FirstOrDefault();
            if (edited is not null) break;
            await Task.Delay(50);
        }
        output.WriteLine($"Automatic {watch} edit to keyword search: {elapsed.Elapsed.TotalSeconds:F3}s; poll interval {pollSeconds}s; budget {budgetSeconds}s.");
        Assert.True(edited is not null, $"{watch} edit exceeded {budgetSeconds}s; observed {elapsed.Elapsed.TotalSeconds:F3}s. Poll budget is configured 2s plus 2s for scheduling, descriptor staging, and keyword publication.");
        Assert.Equal(initial.DocumentId, edited.DocumentId);
        Assert.True(edited.Revision > initial.Revision);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(budgetSeconds));
    }

    [Fact]
    public async Task FolderDeepScanPublishesEditsAndUnambiguousMovePreservesIdentity()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory);
        var path = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "notes")).FullName;
        await File.WriteAllTextAsync(Path.Combine(path, "note.md"), "The badger is learning.");
        using var created = await client.PostAsJsonAsync("/v1/sources", new CreateSourceRequest { Path = path, Name = "notes", Watch = "poll", PollIntervalSeconds = 600 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = (await created.Content.ReadFromJsonAsync<SourceRecord>())!;
        await ScanAsync(client, source.Id);
        var first = await WaitForHitAsync(client, "badger");
        await File.WriteAllTextAsync(Path.Combine(path, "note.md"), "The wombat replaces the badger.");
        await ScanAsync(client, source.Id);
        var updated = await WaitForHitAsync(client, "wombat");
        Assert.Equal(first.DocumentId, updated.DocumentId);
        Assert.True(updated.Revision > first.Revision);
        File.Move(Path.Combine(path, "note.md"), Path.Combine(path, "moved.md"));
        await ScanAsync(client, source.Id);
        var moved = await WaitForHitAsync(client, "wombat");
        Assert.Equal(first.DocumentId, moved.DocumentId);
    }

    [Fact]
    public async Task UnambiguousSourceMovePreservesIdentityWithoutEmbeddingProviderCalls()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory);
        var path = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "move-cost")).FullName;
        await File.WriteAllTextAsync(Path.Combine(path, "original.md"), "Unique echidna movement fixture.");
        using var created = await client.PostAsJsonAsync("/v1/sources", new CreateSourceRequest { Path = path, Name = "move-cost", Watch = "poll", PollIntervalSeconds = 600 });
        var source = (await created.Content.ReadFromJsonAsync<SourceRecord>())!;
        await ScanAsync(client, source.Id);
        var first = await WaitForHitAsync(client, "echidna");
        DocumentRecord? indexed = null;
        for (var attempt = 0; attempt < 160; attempt++)
        {
            indexed = await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + first.DocumentId);
            if (indexed!.Status == "indexed") break;
            await Task.Delay(50);
        }
        Assert.Equal("indexed", indexed!.Status);
        var provider = factory.MockProvider.Services.GetRequiredService<MockProviderState>();
        var before = provider.GetRequestCount("/v1/embeddings");
        File.Move(Path.Combine(path, "original.md"), Path.Combine(path, "renamed.md"));
        await ScanAsync(client, source.Id);
        var moved = await WaitForHitAsync(client, "echidna");
        Assert.Equal(first.DocumentId, moved.DocumentId);
        for (var attempt = 0; attempt < 240; attempt++)
        {
            indexed = await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + moved.DocumentId);
            if (indexed!.Status == "indexed") break;
            await Task.Delay(50);
        }
        Assert.Equal("indexed", indexed!.Status);
        Assert.Equal(before, provider.GetRequestCount("/v1/embeddings"));
        var jobs = (await client.GetFromJsonAsync<ProcessingJob[]>("/v1/jobs"))!;
        Assert.DoesNotContain(jobs, job => job.DocumentId == first.DocumentId && job.Status is "queued" or "running");
    }

    [Fact]
    public async Task InboxClaimsAndImportsThenRewrittenOriginalCreatesSeparateIdentity()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory);
        var inbox = Path.Combine(factory.Options.CurrentValue.Sources.IncomingRoot, "inbox");
        await File.WriteAllTextAsync(Path.Combine(inbox, "capture.txt"), "The axolotl enters the inbox.");
        await ScanAsync(client, "inbox");
        var first = await WaitForHitAsync(client, "axolotl");
        await File.WriteAllTextAsync(Path.Combine(inbox, "capture.txt"), "The quokka enters the rewritten inbox path.");
        await ScanAsync(client, "inbox");
        var second = await WaitForHitAsync(client, "quokka");
        Assert.NotEqual(first.DocumentId, second.DocumentId);
        Assert.False(File.Exists(Path.Combine(inbox, "capture.txt")));
        // Successful extraction finalizes each claim; original immutable copies remain in the managed vault.
        for (var attempt = 0; attempt < 100 && Directory.EnumerateFiles(Path.Combine(inbox, ".claimed")).Any(); attempt++) await Task.Delay(50);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(inbox, ".claimed")));
    }

    [Fact]
    public async Task PolicyInvalidInboxAndSourceEntriesPresentAtStartupNeverStopTheDaemon()
    {
        await using var factory = new LaneDWebFactory();
        var inbox = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.IncomingRoot, "inbox")).FullName;
        // Present before the host starts, exactly as an unchanged trigger is after a restart.
        await using (var oversized = File.Create(Path.Combine(inbox, "huge.txt"))) oversized.SetLength(SecondBrain.Infrastructure.Ingestion.InputStager.MaximumInputBytes + 1);
        var outside = Path.Combine(factory.Options.CurrentValue.Sources.IncomingRoot, "outside.txt");
        await File.WriteAllTextAsync(outside, "The narwhal must never be imported through a second link.");
        Assert.Equal(0, Link(outside, Path.Combine(inbox, "linked.txt")));
        await File.WriteAllTextAsync(Path.Combine(inbox, "good.txt"), "The capybara arrives beside rejected neighbours.");
        using var client = await ClientAsync(factory);
        var lifetime = factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        await WaitForHitAsync(client, "capybara");
        // The startup scan already met both rejections; repeated scans meet them again without failing.
        for (var round = 0; round < 3; round++) await ScanAsync(client, "inbox");
        // A registered folder source with the same entries fails nothing beyond those entries either.
        var folder = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "policy")).FullName;
        await using (var oversized = File.Create(Path.Combine(folder, "huge.md"))) oversized.SetLength(SecondBrain.Infrastructure.Ingestion.InputStager.MaximumInputBytes + 1);
        await File.WriteAllTextAsync(Path.Combine(folder, "fine.md"), "The quetzal is admitted from a registered folder.");
        using var created = await client.PostAsJsonAsync("/v1/sources", new CreateSourceRequest { Path = folder, Name = "policy", Watch = "poll", PollIntervalSeconds = 600 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var registered = (await created.Content.ReadFromJsonAsync<SourceRecord>())!;
        await ScanAsync(client, registered.Id);
        await WaitForHitAsync(client, "quetzal");
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.True(File.Exists(Path.Combine(inbox, "huge.txt")));
        Assert.True(File.Exists(Path.Combine(inbox, "linked.txt")));
        using var search = await client.PostAsJsonAsync("/v1/search", new SearchRequest("narwhal", "keyword"));
        Assert.Empty((await search.Content.ReadFromJsonAsync<SearchResponse>())!.Results);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string existing, string link);

    [Fact]
    public async Task FolderPolicyRejectionsAreDurableVisibleAndNotRestagedUntilTheEntryChanges()
    {
        CountingStager? stager = null;
        await using var factory = new LaneDWebFactory(configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton<IInputStager>(provider => stager = new CountingStager(provider.GetRequiredService<SecondBrain.Infrastructure.Ingestion.InputStager>()))));
        using var client = await ClientAsync(factory);
        var root = factory.Options.CurrentValue.Sources.AllowedRoots[0];
        var folder = Directory.CreateDirectory(Path.Combine(root, "rejections")).FullName;
        await using (var oversized = File.Create(Path.Combine(folder, "huge.md"))) oversized.SetLength(SecondBrain.Infrastructure.Ingestion.InputStager.MaximumInputBytes + 1);
        var outside = Path.Combine(root, "outside.md");
        await File.WriteAllTextAsync(outside, "The narwhal is admitted only once its second link is gone.");
        Assert.Equal(0, Link(outside, Path.Combine(folder, "linked.md")));
        await File.WriteAllTextAsync(Path.Combine(folder, "fine.md"), "The quetzal is admitted beside rejected neighbours.");
        var locked = Path.Combine(folder, "locked.md");
        await File.WriteAllTextAsync(locked, "The tamarin is unreadable for now.");
        var unreadable = OperatingSystem.IsLinux() && Environment.UserName != "root";
        if (OperatingSystem.IsLinux() && unreadable) File.SetUnixFileMode(locked, UnixFileMode.None);
        using var created = await client.PostAsJsonAsync("/v1/sources", new CreateSourceRequest { Path = folder, Name = "rejections", Watch = "poll", PollIntervalSeconds = 600 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = (await created.Content.ReadFromJsonAsync<SourceRecord>())!;
        await ScanAsync(client, source.Id);
        await WaitForHitAsync(client, "quetzal");

        // Each rejection is a failed `stage` job naming its reason, on a metadata-only document findable by title.
        var stages = await StageJobsAsync(client);
        Assert.Equal(["input_bytes", "source_multiple_links"], stages.Select(job => job.Error).Order(StringComparer.Ordinal));
        Assert.All(stages, job => Assert.Equal("failed", job.Status));
        var huge = stages.Single(job => job.Error == "input_bytes"); var linked = stages.Single(job => job.Error == "source_multiple_links");
        var hugeDocument = (await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + huge.DocumentId))!;
        Assert.Equal(("huge.md", "skipped", ""), (hugeDocument.Title, hugeDocument.Status, hugeDocument.ContentHash));
        var linkedDocument = (await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + linked.DocumentId))!;
        Assert.Equal(("linked.md", "failed", ""), (linkedDocument.Title, linkedDocument.Status, linkedDocument.ContentHash));
        foreach (var (name, id) in new[] { ("huge.md", huge.DocumentId), ("linked.md", linked.DocumentId) })
        {
            using var query = await client.PostAsJsonAsync("/v1/query", new QueryRequest(Source: source.Id,
                PathPrefix: Path.Combine(folder, name), Predicates: [new("title", "eq", JsonSerializer.SerializeToElement(name))]));
            Assert.Equal(HttpStatusCode.OK, query.StatusCode);
            Assert.Equal(id, Assert.Single((await query.Content.ReadFromJsonAsync<QueryPage>())!.Records).Id);
            using var search = await client.PostAsJsonAsync("/v1/search", new SearchRequest(Path.GetFileNameWithoutExtension(name), "keyword", new RetrievalFilters(Source: source.Id)));
            Assert.Contains((await search.Content.ReadFromJsonAsync<SearchResponse>())!.Results, hit => hit.DocumentId == id);
        }
        Assert.Equal(0L, await ScalarAsync(factory, "SELECT COALESCE(SUM(size_bytes),0) FROM documents WHERE content_hash='' AND source_id=$key", source.Id));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(factory.Options.CurrentValue.DataRoot, "vault", "originals"), huge.DocumentId + ".*"));
        // A transient I/O failure is not a deterministic rejection, so nothing is recorded for it.
        Assert.Equal(unreadable ? 3L : 4L, await ScalarAsync(factory, "SELECT COUNT(*) FROM documents WHERE source_id=$key", source.Id));

        // Repeated shallow and deep scans, and a scanner with no process memory (a restart), neither restage nor duplicate.
        var opened = stager!.Opens("huge.md") + stager.Opens("linked.md");
        foreach (var deep in new[] { false, true, false, true }) await ScanAsync(client, source.Id, deep);
        var restarted = ActivatorUtilities.CreateInstance<FolderProcessingOrchestrator>(factory.Services);
        await restarted.ScanAsync(source, deep: true, CancellationToken.None);
        await restarted.ScanAsync(source, deep: false, CancellationToken.None);
        Assert.Equal(opened, stager.Opens("huge.md") + stager.Opens("linked.md"));
        Assert.Equal(stages.Select(job => job.Id).Order(), (await StageJobsAsync(client)).Select(job => job.Id).Order());
        Assert.Equal(hugeDocument.Revision, (await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + huge.DocumentId))!.Revision);
        Assert.Equal(unreadable ? 3L : 4L, await ScalarAsync(factory, "SELECT COUNT(*) FROM documents WHERE source_id=$key", source.Id));
        Assert.Equal(1L, await ScalarAsync(factory, "SELECT COUNT(*) FROM jobs WHERE document_id=$key", huge.DocumentId));
        using (var retry = await client.PostAsync("/v1/jobs/" + huge.Id + "/retry", JsonContent.Create(new { })))
            Assert.NotEqual(HttpStatusCode.OK, retry.StatusCode);

        // A rejected entry that leaves is missing like any other; when it returns unchanged its outcome is restored.
        File.Move(Path.Combine(folder, "huge.md"), Path.Combine(root, "huge-away.md"));
        await ScanAsync(client, source.Id);
        Assert.Equal(DocumentStatusNames.Missing, (await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + huge.DocumentId))!.Status);
        File.Move(Path.Combine(root, "huge-away.md"), Path.Combine(folder, "huge.md"));
        await ScanAsync(client, source.Id, deep: false);
        Assert.Equal("skipped", (await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + huge.DocumentId))!.Status);
        Assert.Equal(huge.DocumentId, Assert.Single(await StageJobsAsync(client), job => job.Error == "input_bytes").DocumentId);

        // A valid replacement resumes the same document through ordinary staging and clears its outcome.
        File.Delete(Path.Combine(folder, "huge.md"));
        await File.WriteAllTextAsync(Path.Combine(folder, "huge.md"), "The okapi replaces an oversized original.");
        await ScanAsync(client, source.Id);
        Assert.Equal(huge.DocumentId, (await WaitForHitAsync(client, "okapi")).DocumentId);
        // Removing the second link changes only the policy verdict; even a shallow scan retries it.
        File.Delete(outside);
        await ScanAsync(client, source.Id, deep: false);
        Assert.Equal(linked.DocumentId, (await WaitForHitAsync(client, "narwhal")).DocumentId);
        Assert.Empty(await StageJobsAsync(client));
        if (OperatingSystem.IsLinux() && unreadable)
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await ScanAsync(client, source.Id);
            await WaitForHitAsync(client, "tamarin");
        }
        Assert.False(factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task ReadableDocumentKeepsServingWhileItsReplacementIsRejected()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory);
        var folder = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "grown")).FullName;
        var note = Path.Combine(folder, "note.md");
        await File.WriteAllTextAsync(note, "The axolotl is readable before its file grows.");
        using var created = await client.PostAsJsonAsync("/v1/sources", new CreateSourceRequest { Path = folder, Name = "grown", Watch = "poll", PollIntervalSeconds = 600 });
        var source = (await created.Content.ReadFromJsonAsync<SourceRecord>())!;
        await ScanAsync(client, source.Id);
        var id = (await WaitForHitAsync(client, "axolotl")).DocumentId;
        var before = (await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + id))!;
        await using (var grown = new FileStream(note, FileMode.Open)) grown.SetLength(SecondBrain.Infrastructure.Ingestion.InputStager.MaximumInputBytes + 1);
        foreach (var deep in new[] { false, true, false }) await ScanAsync(client, source.Id, deep);
        // The rejection is recorded against the readable document, which is neither replaced nor marked missing.
        var stage = Assert.Single(await StageJobsAsync(client));
        Assert.Equal((id, "input_bytes", before.Revision), (stage.DocumentId, stage.Error, stage.Revision));
        var after = (await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + id + "?include=text"))!;
        Assert.Equal((before.Revision, before.ContentHash), (after.Revision, after.ContentHash));
        Assert.False(DocumentStatusNames.IsUnavailable(after.Status));
        Assert.Contains("axolotl", after.Text);
        Assert.Equal(id, (await WaitForHitAsync(client, "axolotl")).DocumentId);
        File.Delete(note);
        await File.WriteAllTextAsync(note, "The tapir replaces the oversized edit.");
        await ScanAsync(client, source.Id);
        Assert.Equal(id, (await WaitForHitAsync(client, "tapir")).DocumentId);
        Assert.Empty(await StageJobsAsync(client));
    }

    [Fact]
    public async Task InboxRejectionsAreRecordedOnceAndValidReplacementsKeepTheirIdentity()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory);
        var incoming = factory.Options.CurrentValue.Sources.IncomingRoot;
        var inbox = Directory.CreateDirectory(Path.Combine(incoming, "inbox")).FullName;
        await using (var oversized = File.Create(Path.Combine(inbox, "huge.txt"))) oversized.SetLength(SecondBrain.Infrastructure.Ingestion.InputStager.MaximumInputBytes + 1);
        // An already-claimed entry that is linked elsewhere is rejected where it stands, in .claimed.
        var outside = Path.Combine(incoming, "outside.txt");
        await File.WriteAllTextAsync(outside, "The pangolin waits for its second link to go.");
        Directory.CreateDirectory(Path.Combine(inbox, ".claimed"));
        Assert.Equal(0, Link(outside, Path.Combine(inbox, ".claimed", Guid.NewGuid().ToString("N") + "--held.txt")));
        for (var round = 0; round < 3; round++) await ScanAsync(client, "inbox");
        var stages = await StageJobsAsync(client);
        Assert.Equal(["input_bytes", "source_multiple_links"], stages.Select(job => job.Error).Order(StringComparer.Ordinal));
        var huge = stages.Single(job => job.Error == "input_bytes"); var held = stages.Single(job => job.Error == "source_multiple_links");
        Assert.Equal("huge.txt", (await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + huge.DocumentId))!.Title);
        Assert.Equal("held.txt", (await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + held.DocumentId))!.Title);
        Assert.True(File.Exists(Path.Combine(inbox, "huge.txt")));
        Assert.Equal(2L, await ScalarAsync(factory, "SELECT COUNT(*) FROM documents WHERE source_id=$key", "inbox"));

        File.Delete(Path.Combine(inbox, "huge.txt"));
        await File.WriteAllTextAsync(Path.Combine(inbox, "huge.txt"), "The capybara replaces an oversized drop.");
        File.Delete(outside);
        await ScanAsync(client, "inbox");
        Assert.Equal(huge.DocumentId, (await WaitForHitAsync(client, "capybara")).DocumentId);
        Assert.Equal(held.DocumentId, (await WaitForHitAsync(client, "pangolin")).DocumentId);
        Assert.Empty(await StageJobsAsync(client));
        Assert.Equal(2L, await ScalarAsync(factory, "SELECT COUNT(*) FROM documents WHERE source_id=$key", "inbox"));
    }

    [Fact]
    public async Task UnstageableNamesAreRecordedPerEntryWithoutStarvingNeighbours()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory);
        var folder = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "names")).FullName;
        // Legal Linux names that staging cannot carry: a control character, whitespace only, a trailing backslash.
        foreach (var name in new[] { "bad\u0001.md", "   ", "trail\\" }) await File.WriteAllTextAsync(Path.Combine(folder, name), "The jackal name is never imported.");
        await File.WriteAllTextAsync(Path.Combine(folder, "good.md"), "The marmot is admitted beside hostile names.");
        using var created = await client.PostAsJsonAsync("/v1/sources", new CreateSourceRequest { Path = folder, Name = "names", Watch = "poll", PollIntervalSeconds = 600 });
        var source = (await created.Content.ReadFromJsonAsync<SourceRecord>())!;
        foreach (var deep in new[] { false, true, false }) await ScanAsync(client, source.Id, deep);
        await WaitForHitAsync(client, "marmot");
        var inbox = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.IncomingRoot, "inbox", ".claimed")).FullName;
        await File.WriteAllTextAsync(Path.Combine(inbox, Guid.NewGuid().ToString("N") + "--x\u0001"), "The jackal claim is never imported.");
        await File.WriteAllTextAsync(Path.Combine(inbox, "..", "valid.txt"), "The wombat arrives beside a hostile claim.");
        for (var round = 0; round < 2; round++) await ScanAsync(client, "inbox");
        await WaitForHitAsync(client, "wombat");
        var stages = await StageJobsAsync(client);
        Assert.Equal(4, stages.Length);
        Assert.All(stages, job => Assert.Equal(("failed", "source_name"), (job.Status, job.Error)));
        Assert.Empty((await (await client.PostAsJsonAsync("/v1/search", new SearchRequest("jackal", "keyword"))).Content.ReadFromJsonAsync<SearchResponse>())!.Results);
    }

    [Fact]
    public async Task RejectionRecordsRespectSourceCapacity()
    {
        await using var factory = new LaneDWebFactory(options => options.Limits.Global.FilesPerSource = 1);
        using var client = await ClientAsync(factory);
        var folder = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "full")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "fine.md"), "The ibis takes the only slot.");
        using var created = await client.PostAsJsonAsync("/v1/sources", new CreateSourceRequest { Path = folder, Name = "full", Watch = "poll", PollIntervalSeconds = 600 });
        var source = (await created.Content.ReadFromJsonAsync<SourceRecord>())!;
        await ScanAsync(client, source.Id);
        await WaitForHitAsync(client, "ibis");
        await using (var oversized = File.Create(Path.Combine(folder, "huge.md"))) oversized.SetLength(SecondBrain.Infrastructure.Ingestion.InputStager.MaximumInputBytes + 1);
        for (var round = 0; round < 2; round++) await ScanAsync(client, source.Id);
        // A metadata-only record is still a document: it never exceeds the per-source file bound.
        Assert.Equal(1L, await ScalarAsync(factory, "SELECT COUNT(*) FROM documents WHERE source_id=$key", source.Id));
        Assert.Empty(await StageJobsAsync(client));
        Assert.False(factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    private static async Task<ProcessingJob[]> StageJobsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ProcessingJob[]>("/v1/jobs"))!.Where(job => job.Stage == "stage").ToArray();

    private static async Task<long> ScalarAsync(LaneDWebFactory factory, string sql, string key)
    {
        await using var lease = await factory.Services.GetRequiredService<SecondBrain.Storage.IStateStore>().OpenReadConnectionAsync();
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter(); parameter.ParameterName = "$key"; parameter.Value = key; command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class CountingStager(IInputStager inner) : IInputStager
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> opens = new(StringComparer.Ordinal);
        public int Opens(string path) => opens.GetValueOrDefault(path);
        public ValueTask<StagedInput> StageAsync(Stream input, string filename, CancellationToken cancellationToken = default) => inner.StageAsync(input, filename, cancellationToken);
        public ValueTask<StagedInput> OpenSourceAsync(string sourceRoot, string relativePath, CancellationToken cancellationToken = default)
        {
            opens.AddOrUpdate(relativePath, 1, (_, count) => count + 1);
            return inner.OpenSourceAsync(sourceRoot, relativePath, cancellationToken);
        }
    }

    [Fact]
    public async Task InboxPdfTextLayerCompletesWithinThirtySecondsAndMalformedPdfDoesNotBlockSearch()
    {
        await using var factory = new LaneDWebFactory(configureServices: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(TimeProvider.System)));
        using var client = await ClientAsync(factory);
        var inbox = Path.Combine(factory.Options.CurrentValue.Sources.IncomingRoot, "inbox");
        // Start after the empty startup scan so the acceptance deadline includes a complete inbox poll interval.
        SourceRecord? source = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            source = (await client.GetFromJsonAsync<SourceRecord[]>("/v1/sources"))!.FirstOrDefault(item => item.Id == "inbox");
            if (source?.LastScanAt is not null) break;
            await Task.Delay(50);
        }
        Assert.NotNull(source?.LastScanAt);
        var elapsed = Stopwatch.StartNew();
        await File.WriteAllBytesAsync(Path.Combine(inbox, "layer.pdf"), Pdf("BT /F1 12 Tf 72 700 Td (Platypus text layer acceptance) Tj ET"));
        await File.WriteAllBytesAsync(Path.Combine(inbox, "crafted.pdf"), "%PDF-1.7\nbroken"u8.ToArray());
        var healthy = await WaitForHitAsync(client, "platypus", 30);
        Assert.Equal(1, healthy.Page);
        output.WriteLine($"Automatic inbox PDF drop to keyword search: {elapsed.Elapsed.TotalSeconds:F3}s; configured poll 20s; budget 30s.");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), "Text-layer PDF exceeded the inbox acceptance deadline.");
        var document = await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + healthy.DocumentId + "?include=text,chunks");
        Assert.Contains("Platypus text layer acceptance", document!.Text!, StringComparison.Ordinal);
        Assert.All(document.Chunks!, chunk => Assert.Equal(1, chunk.Page));
        ProcessingJob? failed = null;
        for (var attempt = 0; attempt < 160 && failed is null; attempt++)
        {
            failed = (await client.GetFromJsonAsync<ProcessingJob[]>("/v1/jobs"))!.FirstOrDefault(job => job.Status == "failed");
            if (failed is null) await Task.Delay(50);
        }
        Assert.NotNull(failed);
        Assert.Equal("malformed_document", failed.Error);
        Assert.Equal(healthy.DocumentId, (await WaitForHitAsync(client, "platypus")).DocumentId);
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(inbox, ".claimed")), path => Path.GetFileName(path).EndsWith("crafted.pdf", StringComparison.Ordinal));
    }

    private static byte[] Pdf(string content)
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"];
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i+1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = text.Length; text.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static async Task ScanAsync(HttpClient client, string id, bool deep = true)
    {
        using var response = await client.PostAsync("/v1/sources/" + id + "/scan?deep=" + (deep ? "true" : "false"), JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    private static async Task<SearchHit> WaitForHitAsync(HttpClient client, string text, int timeoutSeconds = 8)
    {
        for (var attempt = 0; attempt < timeoutSeconds * 20; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/v1/search", new SearchRequest(text, "keyword"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var hits = (await response.Content.ReadFromJsonAsync<SearchResponse>())!.Results;
            if (hits.Count > 0) return hits[0];
            await Task.Delay(50);
        }
        Assert.Fail($"A durable source capture did not become keyword searchable within {timeoutSeconds} seconds.");
        throw new InvalidOperationException();
    }
    private static async Task<HttpClient> ClientAsync(LaneDWebFactory factory)
    {
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        var created = factory.Services.GetRequiredService<CredentialFactory>().CreateApiKey("folder-api-test", new HashSet<Scope> { Scope.Admin, Scope.Read, Scope.Write }, await repository.GetEpochAsync());
        await repository.AddAsync(created.Record);
        var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", created.Plaintext);
        return client;
    }
}
