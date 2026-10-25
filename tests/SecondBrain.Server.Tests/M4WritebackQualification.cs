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

public sealed class M4WritebackQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_WRITEBACK_HARNESS"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_WRITEBACK_OUTPUT")!; Directory.CreateDirectory(output);
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_WRITEBACK_PORT") ?? "18328", System.Globalization.CultureInfo.InvariantCulture);
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
            new("", "Write-back browser fixture"), "story.md", new(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "fixture", "api_key"));
        var loaded = await factory.Services.GetRequiredService<FileProcessingOrchestrator>().WaitAsync(admitted, 20, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { origin, id = loaded.Document.Id, syntheticData = true, realExtractor = true }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, timeout.Token);
        var finished = (await documents.GetAsync(loaded.Document.Id, true))!;
        Assert.Equal(2, finished.Revision); Assert.True(finished.Enrichment!.WrittenBack);
        Assert.Contains("author: Ada", finished.Text);
        Assert.EndsWith("An article about sustainable transport.", finished.Text);
        await using var read = await factory.Store.OpenReadConnectionAsync();
        var writes = await Dapper.SqlMapper.ExecuteScalarAsync<int>(read.Connection, "SELECT COUNT(*) FROM revisions WHERE cause='enrichment'");
        var calls = await Dapper.SqlMapper.ExecuteScalarAsync<int>(read.Connection, "SELECT COUNT(*) FROM usage WHERE role='enrich'");
        Assert.Equal(1, writes); Assert.Equal(1, calls);
        await File.WriteAllTextAsync(Path.Combine(output, "harness.json"), JsonSerializer.Serialize(new
        { syntheticData = true, realExtractor = true, preservedAuthoredFields = true, bodyUnchanged = true, finalRevision = 2, writebackRevisions = writes, modelCalls = calls }));
    }
}
