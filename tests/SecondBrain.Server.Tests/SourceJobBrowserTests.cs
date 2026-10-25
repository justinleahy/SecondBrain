using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Server.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class SourceJobBrowserTests
{
    [Fact]
    public async Task JobsExposeFileFailureAndBindPagingToFiltersAndCredential()
    {
        await using var factory = new LaneDWebFactory();
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var actor = await ActorAsync(factory);
        var note = await documents.CaptureAsync(new("Private body must not be in the job list", "Ordinary note"), actor);
        var pdf = await documents.AdmitFileAsync("%PDF-1.7\ninvalid"u8.ToArray(), new("", "Broken PDF"), "broken.pdf", actor);
        pdf = await factory.Services.GetRequiredService<FileProcessingOrchestrator>().WaitAsync(pdf, 15, CancellationToken.None);
        Assert.Equal("failed", pdf.Document.Status);
        using var client = await ClientAsync(factory, Scope.Read, Scope.Write);
        var failures = (await client.GetFromJsonAsync<CatalogPage<CatalogJob>>("/browse/jobs?status=failed"))!;
        var job = Assert.Single(failures.Items);
        Assert.Equal("broken.pdf", job.FileName); Assert.Equal("Broken PDF", job.Title);
        Assert.NotNull(job.Error); Assert.False(job.CanRetry); Assert.Equal("deterministic-failure", job.RetryRejection);
        using var retry = await client.PostAsync("/jobs/" + job.Id + "/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var first = (await client.GetFromJsonAsync<CatalogPage<CatalogJob>>("/browse/jobs?limit=1"))!;
        Assert.Single(first.Items); Assert.NotNull(first.NextCursor);
        var next = (await client.GetFromJsonAsync<CatalogPage<CatalogJob>>("/browse/jobs?limit=1&cursor=" + Uri.EscapeDataString(first.NextCursor)))!;
        Assert.NotEqual(first.Items[0].Id, Assert.Single(next.Items).Id);
        using var wrongFilter = await client.GetAsync("/browse/jobs?status=failed&cursor=" + Uri.EscapeDataString(first.NextCursor));
        Assert.Equal(HttpStatusCode.BadRequest, wrongFilter.StatusCode);
        using var other = await ClientAsync(factory, Scope.Read);
        using var wrongActor = await other.GetAsync("/browse/jobs?cursor=" + Uri.EscapeDataString(first.NextCursor));
        Assert.Equal(HttpStatusCode.BadRequest, wrongActor.StatusCode);
        await documents.MutateAsync(note.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), actor);
        var remaining = (await client.GetFromJsonAsync<CatalogPage<CatalogJob>>("/browse/jobs"))!;
        Assert.DoesNotContain(remaining.Items, item => item.DocumentId == note.Document.Id);
        var response = await client.GetStringAsync("/browse/jobs");
        Assert.DoesNotContain("Private body", response);
    }

    [Fact]
    public async Task SourceScanErrorsPersistAndCountsRequireAdmin()
    {
        await using var factory = new LaneDWebFactory();
        var sources = factory.Services.GetRequiredService<ISourceRepository>();
        var path = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "source-counts")).FullName;
        var source = new SourceRecord(Ulid.NewUlid().ToString(), "folder", "Counts fixture",
            new(path, [], [], true, "index", false, null, new Dictionary<string, string>(), "poll", 86400), "disabled", null, null, DateTimeOffset.UtcNow);
        await sources.AddAsync(source);
        await sources.RecordScanErrorAsync(source.Id, "invalid-ignore-pattern", DateTimeOffset.UtcNow);
        using var admin = await ClientAsync(factory, Scope.Admin);
        var listed = (await admin.GetFromJsonAsync<SourceRecord[]>("/sources"))!;
        Assert.Equal("invalid-ignore-pattern", listed.Single(item => item.Id == source.Id).LastScanError);
        var counts = (await admin.GetFromJsonAsync<SourceDocumentCounts[]>("/browse/source-counts"))!;
        Assert.Equal(0, counts.Single(item => item.Id == source.Id).Total);
        using var reader = await ClientAsync(factory, Scope.Read);
        using var rejected = await reader.GetAsync("/browse/source-counts");
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        await sources.RecordScanErrorAsync(source.Id, null, DateTimeOffset.UtcNow);
        Assert.Null((await sources.ListAsync()).Single(item => item.Id == source.Id).LastScanError);
    }

    private static async Task<IngestionAuthority> ActorAsync(LaneDWebFactory factory)
    {
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var created = factory.Services.GetRequiredService<CredentialFactory>().CreateSession("source jobs", false, await auth.GetEpochAsync(), 1, 1);
        await auth.AddAsync(created.Record);
        return new(created.Record.Id, created.Record.Generation, created.Record.AccountEpoch, "source jobs", "session");
    }
    private static async Task<HttpClient> ClientAsync(LaneDWebFactory factory, params Scope[] scopes)
    {
        var key = await factory.CreateKeyAsync(scopes.ToHashSet());
        var client = factory.CreatePrivateClient(false); client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        return client;
    }
}
