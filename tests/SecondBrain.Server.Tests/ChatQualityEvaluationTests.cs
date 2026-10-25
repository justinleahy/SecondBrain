using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.MockProvider;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace SecondBrain.Server.Tests;

public sealed class ChatQualityEvaluationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string DataPath = Path.Combine(AppContext.BaseDirectory, "AssistantEvaluation");

    [Fact]
    [Trait("Category", "Qualification")]
    public async Task RecordLocalProvider()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_RECORD_ASSISTANT"));
        var destination = Environment.GetEnvironmentVariable("SECONDBRAIN_ASSISTANT_RECORDING") ?? throw new InvalidOperationException("Set the recording output path.");
        await RunAsync(destination, recording: true);
    }

    [Fact]
    public async Task RecordedProviderMeetsClaimSupportAndIrrelevantCorpusAbstentionThresholds()
    {
        var recording = Path.Combine(DataPath, "recording.json");
        await RunAsync(recording, recording: false);
        var bytes = await File.ReadAllBytesAsync(recording);
        var saved = JsonSerializer.Deserialize<EvalRecording>(bytes, Json)!;
        var annotations = JsonSerializer.Deserialize<EvalAnnotations>(await File.ReadAllTextAsync(Path.Combine(DataPath, "annotations.json")), Json)!;
        Assert.Equal(Hash(bytes), annotations.RecordingSha256);
        var questions = Lines<EvalQuestion>(await File.ReadAllBytesAsync(Path.Combine(DataPath, "questions.jsonl")));
        Assert.Equal(questions.Length, annotations.Cases.Length);
        var supported = 0; var totalSupported = 0; var unsupportedSourced = 0; var abstained = 0; var totalIrrelevant = 0;
        foreach (var question in questions)
        {
            var entry = Assert.Single(saved.Cases, item => item.Id == question.Id);
            var annotation = Assert.Single(annotations.Cases, item => item.Id == question.Id);
            var answer = JsonSerializer.Deserialize<ChatAnswer>(entry.Answer!, Json)!;
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(entry.Answer!)), annotation.AnswerSha256);
            Assert.Equal("complete", entry.Status);
            foreach (var claim in annotation.Claims)
            {
                Assert.Contains(claim.Quote, answer.Text, StringComparison.Ordinal);
                Assert.Contains("[" + claim.Citation + "]", claim.Quote, StringComparison.Ordinal);
                var citation = Assert.Single(answer.Citations, item => item.Source.Number == claim.Citation);
                if (claim.Supported)
                {
                    Assert.False(string.IsNullOrWhiteSpace(claim.SourceQuote));
                    Assert.Contains(claim.SourceQuote!, citation.Source.Excerpt, StringComparison.Ordinal);
                }
                else { Assert.False(string.IsNullOrWhiteSpace(claim.Reason)); unsupportedSourced++; }
            }
            var unreviewed = annotation.Claims.Aggregate(answer.Text, (text, claim) => text.Replace(claim.Quote, "", StringComparison.Ordinal));
            Assert.False(Regex.IsMatch(unreviewed, @"\[\d+\]"), "Every sourced claim must be covered by the review: " + question.Id);
            if (question.Category == "supported")
                foreach (var expected in question.Expected!)
                {
                    totalSupported++;
                    if (annotation.Claims.Any(claim => claim.Supported && claim.Quote.Contains(expected, StringComparison.Ordinal) &&
                        answer.Citations.Any(citation => citation.Source.Number == claim.Citation && citation.Source.DocumentId == "{document:" + question.Document + "}" && citation.Source.Excerpt.Contains(expected, StringComparison.Ordinal)))) supported++;
                }
            if (question.Category == "irrelevant")
            {
                totalIrrelevant++;
                Assert.True(entry.SourceCount > 0, "Irrelevant-corpus cases must actually retrieve nonempty evidence.");
                if (annotation.Abstained) abstained++;
            }
        }
        var supportedRate = supported / (double)totalSupported;
        var unsupportedRate = unsupportedSourced / (double)questions.Count(question => question.Category == "unsupported");
        var abstentionRate = abstained / (double)totalIrrelevant;
        output.WriteLine($"Supported facts cited: {supported}/{totalSupported} ({supportedRate:P1}); unsupported sourced: {unsupportedSourced} ({unsupportedRate:P1}); irrelevant abstention: {abstained}/{totalIrrelevant} ({abstentionRate:P1}).");
        Assert.True(supportedRate >= .90, "Supported fact citation threshold is 0.90.");
        Assert.True(unsupportedRate <= .05, "Unsupported claims presented as sourced exceed 0.05.");
        Assert.True(abstentionRate >= .90, "Irrelevant-corpus abstention threshold is 0.90.");
    }

    private async Task RunAsync(string recordingPath, bool recording)
    {
        var documentBytes = await File.ReadAllBytesAsync(Path.Combine(DataPath, "documents.jsonl"));
        var questionBytes = await File.ReadAllBytesAsync(Path.Combine(DataPath, "questions.jsonl"));
        var documents = Lines<EvalDocument>(documentBytes);
        var questions = Lines<EvalQuestion>(questionBytes);
        var saved = recording ? new EvalRecording(Hash(documentBytes), Hash(questionBytes), DateTimeOffset.UtcNow.ToString("O"), "Qwen/Qwen3-8B", [])
            : JsonSerializer.Deserialize<EvalRecording>(await File.ReadAllTextAsync(recordingPath), Json)!;
        Assert.Equal(Hash(documentBytes), saved.DocumentsSha256);
        Assert.Equal(Hash(questionBytes), saved.QuestionsSha256);
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Models.Chat!.Model = saved.Model;
            options.Models.Chat.Capabilities = options.Models.Chat.Capabilities with { Streaming = false, ReasoningControl = true };
            options.Models.Chat.Reasoning = "high";
            options.Models.Chat.Limits = options.Models.Chat.Limits with { ContextTokens = 32768, MaxOutputTokens = 4096 };
            options.Models.Embed!.Model = "Qwen/Qwen3-Embedding-0.6B";
            options.Models.Embed.Limits = options.Models.Embed.Limits with { EmbedDimensions = 1024 };
            options.Limits.PerCredential.ModelTokensPerHour = 2_000_000;
            options.Budgets.DailyTokens = 5_000_000;
        }, services =>
        {
            // This is the immutable M2 recording's environment. New M4 types require
            // fresh live-model qualification; they must not rewrite these recorded calls.
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(services,
                ServiceDescriptor.Singleton(new SecondBrain.Core.Domain.TypeRegistry(
                    new SecondBrain.Core.Domain.TypeRegistry().Types.Where(type => type.Key is "note" or "article" or "file"))));
        });
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        var actor = new AuthenticatedCredential(key.Record.Id, "api_key", key.Record.Generation, key.Record.AccountEpoch, key.Record.GrantedScopes, null);
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var capture = factory.Services.GetRequiredService<IDocumentRepository>();
        Dictionary<string, string> identities = [];
        foreach (var document in documents)
        {
            var result = await capture.CaptureAsync(new(document.Content, document.Title), new(actor.Id, actor.Generation, actor.AccountEpoch, "evaluation", "api_key"));
            identities[result.Document.Id] = "{document:" + document.Id + "}";
        }
        using var live = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };
        var embeddingIndex = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().EmbeddingsResponse = async context =>
        {
            var request = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
            if (recording)
            {
                using var response = await live.PostAsync("http://127.0.0.1:8001/v1/embeddings", new StringContent(request, Encoding.UTF8, "application/json"), context.RequestAborted);
                var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
                saved.Embeddings.Add(new(request, body, Normalize(request, identities), Normalize(body, identities), (int)response.StatusCode));
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(body, context.RequestAborted);
            }
            else
            {
                Assert.True(embeddingIndex < saved.Embeddings.Count);
                var call = saved.Embeddings[embeddingIndex];
                Assert.Equal(call.NormalizedRequest, Normalize(request, identities));
                context.Response.StatusCode = call.Status;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(call.RawResponse, context.RequestAborted);
            }
            embeddingIndex++;
        };
        await factory.Services.GetRequiredService<EmbeddingProcessor>().ProcessAsync();
        if (recording)
            saved.ProviderMetadata = JsonDocument.Parse(await live.GetStringAsync("http://127.0.0.1:8000/v1/models")).RootElement.Clone();
        var service = factory.Services.GetRequiredService<ChatService>();
        foreach (var question in questions)
        {
            var entry = recording ? new EvalCase(question.Id, [], null, 0) : Assert.Single(saved.Cases, item => item.Id == question.Id);
            var callIndex = 0;
            factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
            {
                var request = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
                var normalizedRequest = Normalize(request, identities);
                if (recording)
                {
                    using var response = await live.PostAsync("http://127.0.0.1:8000/v1/chat/completions", new StringContent(request, Encoding.UTF8, "application/json"), context.RequestAborted);
                    var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
                    entry.Calls.Add(new(request, body, normalizedRequest, Normalize(body, identities), (int)response.StatusCode));
                    context.Response.StatusCode = (int)response.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(body, context.RequestAborted);
                }
                else
                {
                    Assert.True(callIndex < entry.Calls.Count, "The loop requested an additional provider call for " + question.Id);
                    var call = entry.Calls[callIndex];
                    Assert.Equal(call.NormalizedRequest, LegacyM2Request(normalizedRequest));
                    var response = call.NormalizedResponse;
                    foreach (var identity in identities) response = response.Replace(identity.Value, identity.Key, StringComparison.Ordinal);
                    context.Response.StatusCode = call.Status;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(response, context.RequestAborted);
                }
                callIndex++;
            };
            var start = await service.StartAsync(new(question.Query, Mode: "ask"), actor);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            ChatTurn final;
            do
            {
                await Task.Delay(20, deadline.Token);
                final = await repository.GetTurnAsync(start.Turn.Id, actor, deadline.Token);
            } while (final.Status == "running");
            var sources = await repository.SourcesAsync(final.Id, actor);
            var normalizedAnswer = Normalize(JsonSerializer.Serialize(final.Answer, Json), identities);
            output.WriteLine($"{question.Id}: {final.Status}; sources={sources.Count}; calls={callIndex}");
            if (recording)
            {
                entry.Answer = normalizedAnswer; entry.SourceCount = sources.Count; entry.Status = final.Status; entry.FailureCode = final.FailureCode;
                saved.Cases.Add(entry);
                await File.WriteAllTextAsync(recordingPath, JsonSerializer.Serialize(saved, Json));
            }
            else
            {
                Assert.Equal(entry.Calls.Count, callIndex);
                Assert.Equal(entry.Status, final.Status);
                Assert.Equal(entry.Answer, LegacyM2Answer(normalizedAnswer));
                Assert.Equal(entry.SourceCount, sources.Count);
            }
        }
        Assert.Equal(questions.Length, saved.Cases.Count);
        Assert.Equal(saved.Embeddings.Count, embeddingIndex);
        Assert.Empty(await factory.Services.GetRequiredService<IChatProposalRepository>().ListAsync(null, actor));
    }

    // The immutable M2 recordings predate the optional calendar cancellation flag.
    // Check its exact backwards-compatible default before omitting only that schema
    // addition from historical request comparison. M4 model quality needs fresh runs.
    private static string LegacyM2Request(string normalized)
    {
        var root = JsonNode.Parse(normalized)!;
        foreach (var tool in root["tools"]?.AsArray() ?? [])
        {
            var function = tool?["function"];
            if (function?["name"]?.GetValue<string>() != "query_records") continue;
            var properties = function["parameters"]?["properties"]?["query"]?["properties"]?.AsObject();
            if (properties?["include_cancelled"] is not { } flag) continue;
            Assert.Equal(2, flag.AsObject().Count);
            Assert.Equal("boolean", flag["type"]!.GetValue<string>());
            Assert.False(flag["default"]!.GetValue<bool>());
            properties.Remove("include_cancelled");
        }
        return Sort(root).ToJsonString();
    }

    // The immutable M2 answers predate explicit citation granularity. Verify the
    // historical passage contract before removing only this additive field.
    private static string LegacyM2Answer(string normalized)
    {
        var root = JsonNode.Parse(normalized)!;
        foreach (var citation in root["citations"]?.AsArray() ?? [])
        {
            Assert.Equal("passage", citation!["granularity"]!.GetValue<string>());
            Assert.False(citation.AsObject().ContainsKey("spans"));
            citation.AsObject().Remove("granularity");
        }
        return Sort(root).ToJsonString();
    }

    private static string Normalize(string json, Dictionary<string, string> identities)
    {
        foreach (var identity in identities) json = json.Replace(identity.Key, identity.Value, StringComparison.Ordinal);
        json = Regex.Replace(json, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})", "{timestamp}");
        return Sort(JsonNode.Parse(json)).ToJsonString();
    }
    private static JsonNode Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, pair.Value is null ? null : Sort(pair.Value))).ToArray()),
        JsonArray array => new JsonArray(array.Select(value => value is null ? null : Sort(value)).ToArray()),
        _ => node!.DeepClone()
    };
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static T[] Lines<T>(byte[] value) => Encoding.UTF8.GetString(value).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<T>(line, Json)!).ToArray();
    private sealed record EvalDocument(string Id, string Title, string Content);
    private sealed record EvalQuestion(string Id, string Category, string Query, string? Document, string[]? Expected, string? Claim);
    private sealed record EvalClaim(string Quote, int Citation, bool Supported, string? SourceQuote, string? Reason);
    private sealed record EvalReview(string Id, string AnswerSha256, EvalClaim[] Claims, bool Abstained, string Notes);
    private sealed record EvalAnnotations(string RecordingSha256, string Reviewer, EvalReview[] Cases);
    private sealed record EvalCall(string RawRequest, string RawResponse, string NormalizedRequest, string NormalizedResponse, int Status);
    private sealed record EvalCase(string Id, List<EvalCall> Calls, string? Answer, int SourceCount)
    {
        public string? Answer { get; set; } = Answer;
        public int SourceCount { get; set; } = SourceCount;
        public string? Status { get; set; }
        public string? FailureCode { get; set; }
    }
    private sealed record EvalRecording(string DocumentsSha256, string QuestionsSha256, string RecordedAt, string Model, List<EvalCase> Cases)
    {
        public JsonElement? ProviderMetadata { get; set; }
        public List<EvalCall> Embeddings { get; init; } = [];
    }
}
