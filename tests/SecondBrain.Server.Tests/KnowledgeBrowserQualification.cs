using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Extraction;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Tests.Support;
using SecondBrain.Server.Ingestion;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>Isolated real-HTTP fixture for reproducible M3 browser scenarios.</summary>
public sealed class KnowledgeBrowserQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task BrowserWorkflows()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M3_BROWSER_HARNESS"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M3_BROWSER_OUTPUT") ?? throw new InvalidOperationException("Set the harness output directory.");
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M3_BROWSER_PORT") ?? "17173", System.Globalization.CultureInfo.InvariantCulture);
        var origin = "http://127.0.0.1:" + port;
        Directory.CreateDirectory(output);
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Server.Listeners = [new ListenerOptions { Bind = "127.0.0.1", Port = port }];
            options.Server.Hosts = ["127.0.0.1:" + port, "localhost:" + port];
            options.Server.Origins = [origin, "http://localhost:" + port];
        }, services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
            new TransientFixtureExtractor(new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath)))));
        factory.UseKestrel(_ => { });
        factory.StartServer();
        await factory.SeedAccountAsync();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var authority = new IngestionAuthority(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "M3 browser fixture", "api_key");
        var note = await documents.CaptureAsync(new("# Retrieval evaluation\n\nBlue Otter uses recall and precision to evaluate retrieval.\n\n<script>window.untrustedDocumentExecuted = true</script>\n\n![remote](https://example.invalid/pixel.png)", "Retrieval evaluation", Tags: ["evaluation", "project"]), authority);
        var article = await documents.CaptureAsync(new("A garden needs water and sunlight.", "Garden article", "article", JsonSerializer.SerializeToElement(new { author = "Maya Chen", published_at = "2026-10-23T10:00:00Z" }), ["garden"]), authority);
        var pdf = await documents.AdmitFileAsync(PdfFixture.TwoPages(), new("", "Two-page PDF"), "two-page.pdf", authority);
        var files = factory.Services.GetRequiredService<FileProcessingOrchestrator>();
        files.Notify();
        pdf = await files.WaitAsync(pdf, 15, CancellationToken.None);
        Assert.Contains(pdf.Document.Status, new[] { "indexed", "indexed_partial" });
        var sourcePaths = new Dictionary<int, string>();
        var conflictNotes = new Dictionary<int, object>();
        foreach (var width in new[] { 1280, 390 })
        {
            var conflict = await documents.CaptureAsync(new("Original conflict text " + width, "File conflict " + width), authority);
            var original = (await documents.GetAsync(conflict.Document.Id, true))!;
            conflictNotes[width] = new { id = original.Id, path = original.Path };
            var path = Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "browser-source-" + width);
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "retry-me.md"), "M3_TRANSIENT_" + width + "\n\nA recoverable extraction fixture.");
            await File.WriteAllTextAsync(Path.Combine(path, "broken.pdf"), "%PDF-1.7\nThis is an invalid PDF fixture.");
            sourcePaths[width] = path;
        }
        await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new
        {
            origin, note = note.Document.Id, article = article.Document.Id, pdf = pdf.Document.Id, sourcePaths, conflictNotes, temporaryTestAccount = true
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, deadline.Token);
    }
}
