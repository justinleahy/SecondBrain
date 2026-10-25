using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>Real local model/browser acceptance and a fresh server instance over retained synthetic state.</summary>
public sealed class M3AcceptanceQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task LiveAssistantAndServerRestart()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M3_ACCEPTANCE"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M3_ACCEPTANCE_OUTPUT") ?? throw new InvalidOperationException("Set an isolated output directory.");
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M3_ACCEPTANCE_PORT") ?? "18264", System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(output);
        var root = Path.Combine(output, "private-state");
        var incoming = Path.Combine(output, "incoming");
        Directory.CreateDirectory(root); Directory.CreateDirectory(incoming);
        var ring = new TestKeyRing();
        var origin = "http://127.0.0.1:" + port;
        LaneDWebFactory Start()
        {
            var app = new LaneDWebFactory(options =>
            {
                options.DataRoot = root;
                options.Sources.IncomingRoot = incoming;
                options.Sources.AllowedRoots = [incoming];
                options.Server.Listeners = [new() { Bind = "127.0.0.1", Port = port }];
                options.Server.Hosts = ["127.0.0.1:" + port]; options.Server.Origins = [origin];
                options.Providers = new()
                {
                    ["local-chat"] = new() { Kind = "openai_compatible", Endpoint = "http://127.0.0.1:8000/v1", Trusted = true },
                    ["local-embed"] = new() { Kind = "openai_compatible", Endpoint = "http://127.0.0.1:8001/v1", Trusted = true }
                };
                options.Privacy.TrustedServices = ["http://127.0.0.1:8000", "http://127.0.0.1:8001"];
                options.Models.Chat = new() { Provider = "local-chat", Model = "Qwen/Qwen3-8B", Reasoning = "low", Capabilities = new() { Tools = true, Streaming = true }, Limits = new() { ContextTokens = 32768, MaxOutputTokens = 4096 } };
                options.Models.Enrich = options.Models.Chat;
                options.Models.Embed = new() { Provider = "local-embed", Model = "Qwen/Qwen3-Embedding-0.6B", Dimensions = 1024, Limits = new() { EmbedDimensions = 1024, EmbedMaxInputTokens = 8192, EmbedBatchMax = 32 } };
            }, services =>
            {
                services.RemoveAll<IKeyRing>(); services.AddSingleton<IKeyRing>(ring);
                services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton(ring.DataProtectionProvider);
            });
            app.UseKestrel(_ => { }); app.StartServer(); return app;
        }
        LaneDWebFactory? factory = null;
        try
        {
            factory = Start(); await factory.SeedAccountAsync();
            var credential = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
            var authority = new IngestionAuthority(credential.Record.Id, credential.Record.Generation, credential.Record.AccountEpoch, "M3 acceptance", "api_key");
            var documents = factory.Services.GetRequiredService<IDocumentRepository>();
            var protocol = await documents.CaptureAsync(new("# Retrieval evaluation approach\n\nWe maintain a human-labelled gold set of 40 queries. We report recall@5 and precision@5. We compare keyword, semantic, and hybrid search using the same filters. We inspect missed relevant documents every week. Similarity scores alone do not establish relevance.", "Retrieval evaluation approach", Tags: ["evaluation"]), authority);
            var reading = await documents.CaptureAsync(new("# Retrieval evaluation reading\n\nThe reading distinguishes context precision from context recall. Context precision measures how much retrieved context is relevant; context recall measures how much of the needed evidence was retrieved. Our evaluation combines these measurements with inspection of missed queries.", "Retrieval evaluation reading", "article", JsonSerializer.SerializeToElement(new { author = "Maya Chen", published_at = "2026-09-01T10:00:00Z" }), ["evaluation"]), authority);
            var oldWork = await documents.CaptureAsync(new("Last month I migrated the archived notes for Project Vega. The archive migration was completed last month.", "Older Project Vega work"), authority);
            var currentWork = await documents.CaptureAsync(new("This week I built the Project Orion retrieval benchmark and reviewed its failure cases. The benchmark compares keyword and hybrid retrieval on a human-labelled evaluation set.", "This week's Project Orion benchmark", Tags: ["work"]), authority);
            var currentReview = await documents.CaptureAsync(new("This week I reviewed the Project Orion benchmark with Maya Chen and wrote an evaluation checklist. The next step is to expand the query set.", "This week's evaluation review", Tags: ["work"]), authority);
            var past = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
            foreach (var id in new[] { protocol.Document.Id, reading.Document.Id, oldWork.Document.Id })
                await factory.Store.QueueWriteAsync(async (connection, transaction, token) =>
                {
                    await using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "UPDATE documents SET created_at=$past,updated_at=$past,occurred_at=$past WHERE id=$id";
                    var date = command.CreateParameter(); date.ParameterName = "$past"; date.Value = past; command.Parameters.Add(date);
                    var identity = command.CreateParameter(); identity.ParameterName = "$id"; identity.Value = id; command.Parameters.Add(identity);
                    return await command.ExecuteNonQueryAsync(token);
                });
            await documents.RebuildAsync();
            await factory.Services.GetRequiredService<EmbeddingProcessor>().ProcessAsync();
            var generation = 1;
            async Task Ready() => await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new
            {
                origin, generation, protocol = protocol.Document.Id, reading = reading.Document.Id,
                oldWork = oldWork.Document.Id, currentWork = currentWork.Document.Id, currentReview = currentReview.Document.Id,
                temporaryTestAccount = true, liveLocalModels = true
            }));
            await Ready();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            while (!File.Exists(Path.Combine(output, "stop")))
            {
                if (File.Exists(Path.Combine(output, "restart")))
                {
                    File.Delete(Path.Combine(output, "restart"));
                    await factory.DisposeAsync(); factory = null;
                    factory = Start(); generation++;
                    await Ready();
                }
                await Task.Delay(200, deadline.Token);
            }
        }
        finally { if (factory is not null) await factory.DisposeAsync(); }
    }
}
