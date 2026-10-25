using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Server.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4TranscriptQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_TRANSCRIPT_HARNESS"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_TRANSCRIPT_OUTPUT") ?? throw new InvalidOperationException("Set the harness output directory.");
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_TRANSCRIPT_PORT") ?? "18304", System.Globalization.CultureInfo.InvariantCulture);
        var origin = "http://127.0.0.1:" + port;
        Directory.CreateDirectory(output);
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Server.Listeners = [new ListenerOptions { Bind = "127.0.0.1", Port = port }];
            options.Server.Hosts = ["127.0.0.1:" + port]; options.Server.Origins = [origin];
        }, services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
            new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
        factory.UseKestrel(_ => { }); factory.StartServer(); await factory.SeedAccountAsync();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var authority = new IngestionAuthority(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "M4 transcript fixture", "api_key");
        const string vtt = "WEBVTT\n\n00:01.000 --> 00:03.000\n<v Ada>Pricing should be reviewed.</v>\n\n00:02.000 --> 00:04.000\n<v Bob>Pricing can stay unchanged.</v>\n\n00:04.000 --> 00:05.000\n<v Ada>&lt;script&gt;window.transcriptExecuted=true&lt;/script&gt;</v>\n";
        var note = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes(vtt), new("", "Transcript browser fixture", "transcript",
            JsonSerializer.SerializeToElement(new { participants = new[] { "ada@example.com", "bob@example.com" }, started_at = "2026-10-24T13:00:00Z" })), "meeting.vtt", authority);
        var files = factory.Services.GetRequiredService<FileProcessingOrchestrator>(); files.Notify();
        note = await files.WaitAsync(note, 15, CancellationToken.None);
        var loaded = (await documents.GetAsync(note.Document.Id, true))!;
        Assert.Equal(3, loaded.Segments!.Count);
        await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { origin, id = loaded.Id, temporaryTestAccount = true, realExtractor = true }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, deadline.Token);
    }
}
