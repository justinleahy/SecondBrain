using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>A real Kestrel/browser harness with temporary stores and the host's configured local vLLM models.</summary>
public sealed class ChatBrowserQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task LocalBrowserAndProvider()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_CHAT_BROWSER_HARNESS"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_CHAT_BROWSER_OUTPUT") ?? throw new InvalidOperationException("Set the harness output directory.");
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_CHAT_BROWSER_PORT") ?? "17172", System.Globalization.CultureInfo.InvariantCulture);
        var origin = "http://127.0.0.1:" + port;
        Directory.CreateDirectory(output);
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Server.Listeners = [new ListenerOptions { Bind = "127.0.0.1", Port = port }];
            options.Server.Hosts = ["127.0.0.1:" + port, "localhost:" + port];
            options.Server.Origins = [origin, "http://localhost:" + port];
            options.Providers = new()
            {
                ["local-chat"] = new() { Kind = "openai_compatible", Endpoint = "http://127.0.0.1:8000/v1", Trusted = true },
                ["local-embed"] = new() { Kind = "openai_compatible", Endpoint = "http://127.0.0.1:8001/v1", Trusted = true }
            };
            options.Privacy.TrustedServices = ["http://127.0.0.1:8000", "http://127.0.0.1:8001"];
            options.Models.Chat = new() { Provider = "local-chat", Model = "Qwen/Qwen3-8B", Capabilities = new() { Tools = true, Streaming = true }, Limits = new() { ContextTokens = 32768, MaxOutputTokens = 2048 } };
            options.Models.Enrich = options.Models.Chat;
            options.Models.Embed = new() { Provider = "local-embed", Model = "Qwen/Qwen3-Embedding-0.6B", Dimensions = 1024,
                Limits = new() { EmbedDimensions = 1024, EmbedMaxInputTokens = 8192, EmbedBatchMax = 32 } };
        });
        factory.UseKestrel(_ => { });
        factory.StartServer();
        await factory.SeedAccountAsync();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var authority = new IngestionAuthority(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, key.Record.Name, "api_key");
        var note = await documents.CaptureAsync(new("The Project Lighthouse codename is Blue Otter. Its launch review is on 2026-11-06. Maya Chen owns the launch checklist.", "Project Lighthouse launch", Tags: ["lighthouse"]), authority);
        var other = await documents.CaptureAsync(new("The garden needs watering on Sundays. The shed key is on the kitchen hook.", "Garden routine"), authority);
        await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new
        {
            origin, dataRoot = factory.Options.CurrentValue.DataRoot, projectDocument = note.Document.Id,
            unrelatedDocument = other.Document.Id, temporaryTestAccount = true
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, deadline.Token);
    }
}
