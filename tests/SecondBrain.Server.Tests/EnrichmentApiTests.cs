using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.Extraction;
using System.Text;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Providers;
using SecondBrain.MockProvider;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Http;
using SecondBrain.Server.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class EnrichmentApiTests
{
    [Fact]
    public async Task TriggerRequiresReadAndInferAndReturnsDurableSuggestionsWithUsage()
    {
        await using var factory = Factory(); await factory.SeedAccountAsync();
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var writer = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write });
        var note = (await documents.CaptureAsync(new("A garden plan by Ada.", "Garden plan", Tags: ["garden"]),
            new(writer.Record.Id, writer.Record.Generation, writer.Record.AccountEpoch, "fixture", "api_key"))).Document;
        using var client = factory.CreatePrivateClient(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writer.Plaintext);
        using var denied = await client.PostAsync("/v1/documents/" + note.Id + "/enrich", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var inferOnly = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Infer });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", inferOnly.Plaintext);
        using var noRead = await client.PostAsync("/documents/" + note.Id + "/enrich", null);
        Assert.Equal(HttpStatusCode.Forbidden, noRead.StatusCode);
        var infer = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Infer, Scope.Admin });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", infer.Plaintext);
        using var queued = await client.PostAsync("/v1/documents/" + note.Id + "/enrich", null);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var job = (await queued.Content.ReadFromJsonAsync<EnrichmentJob>())!;
        Assert.Equal("queued", job.Status); Assert.EndsWith(job.Id, queued.Headers.Location!.ToString());
        Assert.True(await factory.Services.GetRequiredService<EnrichmentProcessor>().ProcessAsync());
        var result = (await client.GetFromJsonAsync<DocumentRecord>("/documents/" + note.Id + "?include=text"))!;
        Assert.Equal("Ada planned a garden.", result.Enrichment!.Summary);
        Assert.Contains("plants", result.Tags!); Assert.Equal(note.Revision, result.Revision);
        Assert.Equal("A garden plan by Ada.", result.Text);
        var metadata = (await client.GetFromJsonAsync<DocumentDetails>("/documents/" + note.Id + "/metadata"))!;
        Assert.Contains(metadata.Tags, t => t.Tag == "plants" && t.Origin == "llm");
        Assert.Equal("Ada", Assert.Single(metadata.Enrichment!.Mentions).Text);
        var usage = (await client.GetFromJsonAsync<ModelUsageReport>("/usage"))!;
        Assert.Contains(usage.Rows, r => r.Role == "enrich" && r.CredentialId == infer.Record.Id && r.JobId == job.Id && r.ChargedTokens == 60);
        using var repeated = await client.PostAsync("/documents/" + note.Id + "/enrich", null);
        Assert.Equal("completed", (await repeated.Content.ReadFromJsonAsync<EnrichmentJob>())!.Status);
        using var unknown = await client.PostAsync("/documents/missing/enrich", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task SettingsReloadEnrichmentAndExplicitWriteback()
    {
        await using var factory = Factory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); login.EnsureSuccessStatusCode(); await Csrf(client);
        var before = (await client.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!;
        var settings = JsonSerializer.SerializeToElement(new { enabled = false, writeBackFrontmatter = false, batchBackfill = true });
        using var denied = await client.PutAsJsonAsync("/configuration", new SettingsChange(before.Version, "enrichment", settings));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); step.EnsureSuccessStatusCode(); await Csrf(client);
        using var saved = await client.PutAsJsonAsync("/configuration", new SettingsChange(before.Version, "enrichment", settings)); saved.EnsureSuccessStatusCode();
        var after = (await saved.Content.ReadFromJsonAsync<SettingsSnapshot>())!;
        Assert.False(after.Enrichment.Enabled); Assert.False(factory.Options.CurrentValue.Enrichment.Enabled);
        using var unavailable = await client.PutAsJsonAsync("/configuration", new SettingsChange(after.Version, "enrichment", JsonSerializer.SerializeToElement(new { enabled = true, writeBackFrontmatter = true })));
        unavailable.EnsureSuccessStatusCode();
        Assert.True(factory.Options.CurrentValue.Enrichment.WriteBackFrontmatter);
    }

    [Fact]
    public async Task RealExtractorWritesManagedFrontmatterAndRepeatRequestDoesNotRegenerate()
    {
        await using var factory = Factory(realExtractor: true, writeback: true); await factory.SeedAccountAsync();
        var writer = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write });
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        const string body = "---\ntitle: Authored\ntags: [garden]\nprops:\n  author: Ada\ncustom: keep\n---\nOriginal garden plan.";
        var note = (await documents.CaptureAsync(new(body, "Garden plan", Tags: ["garden"]), new(writer.Record.Id, writer.Record.Generation, writer.Record.AccountEpoch, "writer", "api_key"))).Document;
        var infer = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Infer });
        using var client = factory.CreatePrivateClient(false); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", infer.Plaintext);
        using var request = await client.PostAsync("/documents/" + note.Id + "/enrich", null); request.EnsureSuccessStatusCode();
        Assert.True(await factory.Services.GetRequiredService<EnrichmentProcessor>().ProcessAsync());
        var enriched = (await client.GetFromJsonAsync<DocumentRecord>("/documents/" + note.Id + "?include=text"))!;
        Assert.Equal(2, enriched.Revision); Assert.True(enriched.Enrichment!.WrittenBack);
        Assert.Contains("custom: keep", enriched.Text); Assert.Contains("secondbrain_enrichment:", enriched.Text);
        Assert.EndsWith("Original garden plan.", enriched.Text);
        Assert.Contains("garden", enriched.Tags!);
        using var repeat = await client.PostAsync("/documents/" + note.Id + "/enrich", null); repeat.EnsureSuccessStatusCode();
        Assert.Equal("completed", (await repeat.Content.ReadFromJsonAsync<EnrichmentJob>())!.Status);
        Assert.False(await factory.Services.GetRequiredService<EnrichmentProcessor>().ProcessAsync());
        Assert.Equal(2, (await documents.GetAsync(note.Id, true))!.Revision);
    }

    private static LaneDWebFactory Factory(bool realExtractor = false, bool writeback = false)
    {
        var factory = new LaneDWebFactory(o => { o.Enrichment.Enabled = true; o.Enrichment.WriteBackFrontmatter = writeback; }, services =>
        {
            if (realExtractor) services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
                new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath)));
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EnrichmentOrchestrator)).ToArray())
                services.Remove(descriptor);
        });
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
            await context.Response.WriteAsJsonAsync(new { choices = new[] { new { index = 0, message = new { role = "assistant", content = "{\"summary\":\"Ada planned a garden.\",\"tags\":[\"plants\"],\"mentions\":[{\"kind\":\"person\",\"text\":\"Ada\"}],\"suggested_links\":[{\"target\":\"Planting guide\",\"reason\":\"Garden context\"}]}" }, finish_reason = "stop" } }, usage = new { prompt_tokens = 40, completion_tokens = 20, total_tokens = 60 } });
        return factory;
    }
    private static async Task Csrf(HttpClient client)
    { var token = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!; client.DefaultRequestHeaders.Remove(token.HeaderName); client.DefaultRequestHeaders.Add(token.HeaderName, token.Token); }
}
