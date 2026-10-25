using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>Opt-in private input. Never emits transcript bytes, names, prompts, or answers into evidence.</summary>
public sealed class M4RealTranscriptQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task ImportAndAskWithLocalProviders()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_REAL_TRANSCRIPT"));
        var path = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_REAL_TRANSCRIPT_PATH")!;
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_REAL_TRANSCRIPT_OUTPUT")!;
        var bytes = await File.ReadAllBytesAsync(path);
        var raw = System.Text.Encoding.UTF8.GetString(bytes);
        var speakers = Regex.Matches(raw, @"<v\s+([^>]+)>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).Distinct().ToArray();
        Assert.True(speakers.Length > 0, "The real export must contain voice labels.");
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Providers["live-chat"] = new() { Kind = "openai_compatible", Endpoint = "http://127.0.0.1:8000/v1", Trusted = true };
            options.Providers["live-embed"] = new() { Kind = "openai_compatible", Endpoint = "http://127.0.0.1:8001/v1", Trusted = true };
            options.Privacy.TrustedServices.AddRange(["http://127.0.0.1:8000", "http://127.0.0.1:8001"]);
            options.Models.Chat = new() { Provider = "live-chat", Model = "Qwen/Qwen3-8B", Reasoning = "low",
                Capabilities = new() { Tools = true, Streaming = false, ReasoningControl = true }, Limits = new() { ContextTokens = 32768, MaxOutputTokens = 4096 } };
            options.Models.Embed = new() { Provider = "live-embed", Model = "Qwen/Qwen3-Embedding-0.6B", Limits = new() { EmbedDimensions = 1024, EmbedMaxInputTokens = 8192, EmbedBatchMax = 32 } };
            options.Limits.PerCredential.ModelTokensPerHour = 2_000_000; options.Budgets.DailyTokens = 5_000_000;
        }, services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
            new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
        await factory.SeedAccountAsync();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Plaintext);
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "real-transcript-qualification");
        var metadata = new CaptureRequest("", "Private real-export qualification", "transcript",
            JsonSerializer.SerializeToElement(new { participants = speakers, started_at = "2026-10-24T13:00:00Z" }));
        async Task<CaptureResult> Upload()
        {
            using var body = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("text/vtt");
            body.Add(file, "file", "meeting.vtt"); body.Add(new StringContent(JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web))), "metadata");
            using var response = await client.PostAsync("/v1/documents?wait=30", body);
            Assert.True(response.IsSuccessStatusCode, "Real transcript upload must succeed.");
            return (await response.Content.ReadFromJsonAsync<CaptureResult>())!;
        }
        var captured = await Upload(); var repeated = await Upload();
        Assert.True(captured.Document.Id == repeated.Document.Id, "An idempotent repeat must preserve document identity.");
        var repository = factory.Services.GetRequiredService<IDocumentRepository>();
        var loaded = (await repository.GetAsync(captured.Document.Id, true))!;
        Assert.True(loaded.Type == "transcript" && loaded.Segments?.Count > 0, "Real VTT extraction must publish transcript segments.");
        Assert.True(loaded.Segments!.Count == Regex.Matches(raw, " --> ").Count, "The export's cue count must be preserved.");
        var segment = loaded.Segments.Where(s => !string.IsNullOrWhiteSpace(s.Speaker) && s.Length > 40).OrderByDescending(s => s.Length).First();
        var query = string.Join(" ", Regex.Matches(loaded.Text!.Substring(segment.Start, segment.Length), @"[\p{L}]{5,}").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
        Assert.True(query.Length > 0, "A bounded real-export search query is required.");
        using var search = await client.PostAsJsonAsync("/v1/search", new SearchRequest(query, "keyword", new(Type: "transcript", People: [new(segment.Speaker!, "speaker")])));
        Assert.True(search.IsSuccessStatusCode, "Speaker-filtered transcript search must succeed.");
        var found = (await search.Content.ReadFromJsonAsync<SearchResponse>())!;
        Assert.True(found.Results.Any(hit => hit.DocumentId == loaded.Id && hit.Transcript?.Speaker == segment.Speaker), "Search must carry the matching speaker locator.");
        await factory.Services.GetRequiredService<EmbeddingOrchestrator>().TryCompleteAsync(captured, CancellationToken.None);
        var actor = new AuthenticatedCredential(key.Record.Id, "api_key", key.Record.Generation, key.Record.AccountEpoch, key.Record.GrantedScopes, null);
        var started = await factory.Services.GetRequiredService<ChatService>().StartAsync(new(
            "Using the meeting transcript " + loaded.Id + ", what did " + segment.Speaker + " say about " + query + "? Cite their statements.",
            Mode: "ask", Filters: new(Type: "transcript", People: [new(segment.Speaker!, "speaker")])), actor);
        var turns = factory.Services.GetRequiredService<IChatRepository>();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        ChatTurn turn;
        do { await Task.Delay(100); turn = await turns.GetTurnAsync(started.Turn.Id, actor); }
        while (turn.Status == "running" && DateTimeOffset.UtcNow < deadline);
        Assert.True(turn.Status == "complete", "The fresh local-model answer must complete.");
        Assert.True(turn.Answer?.Citations.Any(c => c.Source.DocumentId == loaded.Id && c.Source.Transcript?.Speaker == segment.Speaker) == true,
            "The answer must cite a timestamped segment from the requested speaker.");
        Assert.True(turn.Answer!.Citations.All(c => c.State == "current" && c.Source.Transcript is { StartMs: >= 0 } locator && locator.EndMs >= locator.StartMs),
            "Every citation must carry current transcript timing metadata.");
        // Optional private review material is separate from sanitized qualification evidence.
        // The caller supplies an existing private directory; never print this content in test logs.
        if (Environment.GetEnvironmentVariable("SECONDBRAIN_M4_PRIVATE_REVIEW_DIRECTORY") is { } privateReview)
        {
            Assert.True(Path.IsPathFullyQualified(privateReview) && Directory.Exists(privateReview));
            Assert.Null(new DirectoryInfo(privateReview).LinkTarget);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(privateReview));
            var reviewOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                reviewOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var review = new FileStream(Path.Combine(privateReview, "review.json"), reviewOptions);
            await JsonSerializer.SerializeAsync(review, new
            {
                requestedSpeaker = segment.Speaker, topic = query,
                selectedSegment = loaded.Text!.Substring(segment.Start, segment.Length),
                segment.StartMs, segment.EndMs, answer = turn.Answer,
                inputSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
            });
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
        {
            inputSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), inputBytes = bytes.Length,
            cues = loaded.Segments.Count, speakerCount = speakers.Length, multipartImport = true, repeatedImportSameIdentity = true,
            speakerFilteredSearch = true, freshLocalModels = true, chatModel = "Qwen/Qwen3-8B", embeddingModel = "Qwen/Qwen3-Embedding-0.6B",
            answerComplete = true, timestampedCitations = turn.Answer.Citations.Count,
            privateContentInEvidence = false, usesDisposableStore = true, metadataStartTimeSuppliedByTest = true,
            pricingVocabularyPresent = Regex.IsMatch(raw, @"\b(pricing|price|cost|budget)\b", RegexOptions.IgnoreCase)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
