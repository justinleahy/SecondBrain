using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.MockProvider;
using SecondBrain.Server.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4EnrichmentQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task LocalModelEnrichesAnArticle()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_ENRICHMENT_LIVE"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_ENRICHMENT_OUTPUT")!;
        await using var factory = new LaneDWebFactory(o =>
        {
            o.Enrichment.Enabled = true;
            o.Providers["local-enrichment"] = new() { Kind = "openai_compatible", Endpoint = "http://127.0.0.1:8000/v1", Trusted = true };
            o.Privacy.TrustedServices.Add("http://127.0.0.1:8000");
            o.Models.Enrich = new() { Provider = "local-enrichment", Model = "Qwen/Qwen3-8B", Reasoning = "low",
                Capabilities = new() { StructuredOutput = true, ReasoningControl = true }, Limits = new() { ContextTokens = 32768, MaxOutputTokens = 2048 } };
        }, services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
            new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
        await factory.SeedAccountAsync();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        const string article = "Urban transport: a public policy overview\nBy Science Review\n\nCities can reduce congestion by improving bus frequency and connecting safe walking routes to stations. This article reviews published evidence about network design and explains why reliability matters more than adding lanes alone. Researchers recommend evaluating travel time across the whole network, including time spent waiting and transferring. The resulting benefits include cleaner air and more predictable daily journeys.";
        var admitted = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes(article), new("", "Transport article"), "article.txt",
            new(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "live classification fixture", "api_key"));
        var result = await factory.Services.GetRequiredService<FileProcessingOrchestrator>().WaitAsync(admitted, 40, CancellationToken.None);
        DocumentRecord? enriched = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            enriched = await documents.GetAsync(result.Document.Id, true);
            if (enriched?.Enrichment is not null) break;
            await Task.Delay(500);
        }
        Assert.NotNull(enriched?.Enrichment);
        Assert.NotEmpty(enriched.Enrichment.Summary);
        Assert.Equal(article, enriched.Text);
        Assert.Equal(result.Document.Revision, enriched.Revision);
        await using var read = await factory.Store.OpenReadConnectionAsync();
        var usageCalls = await Dapper.SqlMapper.ExecuteScalarAsync<int>(read.Connection,
            "SELECT COUNT(*) FROM usage WHERE role='enrich' AND provider='local-enrichment' AND settled=1 AND job_id IS NOT NULL AND credential_id IS NOT NULL");
        Assert.True(usageCalls > 0);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "live-enrichment.json"), JsonSerializer.Serialize(new
        { model = "Qwen/Qwen3-8B", syntheticArticle = true, realExtractor = true, summaryCharacters = enriched.Enrichment.Summary.Length,
          tags = enriched.Enrichment.Tags.Length, mentions = enriched.Enrichment.Mentions.Length, suggestedLinks = enriched.Enrichment.SuggestedLinks.Length,
          originalTextUnchanged = true, revisionUnchanged = true, settledUsageCalls = usageCalls }));
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_ENRICHMENT_HARNESS"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_ENRICHMENT_OUTPUT")!; Directory.CreateDirectory(output);
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_ENRICHMENT_PORT") ?? "18327", System.Globalization.CultureInfo.InvariantCulture);
        var origin = "http://127.0.0.1:" + port;
        await using var factory = new LaneDWebFactory(o =>
        {
            o.Enrichment.Enabled = true; o.Server.Listeners = [new() { Bind = "127.0.0.1", Port = port }];
            o.Server.Hosts = ["127.0.0.1:" + port]; o.Server.Origins = [origin];
        }, services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
            new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = c => c.Response.WriteAsJsonAsync(new
        { choices = new[] { new { index = 0, message = new { role = "assistant", content = "{\"summary\":\"Ada describes sustainable transport.\",\"tags\":[\"transport\"],\"mentions\":[{\"kind\":\"person\",\"text\":\"Ada\"}],\"suggested_links\":[{\"target\":\"City planning\",\"reason\":\"Related context\"}]}" }, finish_reason = "stop" } }, usage = new { prompt_tokens = 30, completion_tokens = 10, total_tokens = 40 } });
        factory.UseKestrel(_ => { }); factory.StartServer(); await factory.SeedAccountAsync();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var admitted = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("---\nprops:\n  author: Ada\n---\nAn article about sustainable transport."),
            new("", "Enrichment browser fixture"), "story.md", new(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "fixture", "api_key"));
        var loaded = await factory.Services.GetRequiredService<FileProcessingOrchestrator>().WaitAsync(admitted, 20, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { origin, id = loaded.Document.Id, syntheticData = true, realExtractor = true }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, timeout.Token);
    }
}
