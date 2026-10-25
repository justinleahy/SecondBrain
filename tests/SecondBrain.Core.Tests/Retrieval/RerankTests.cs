using Microsoft.Extensions.AI;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Providers;
using SecondBrain.Core.Retrieval;
using Xunit;

namespace SecondBrain.Core.Tests.Retrieval;

public sealed class RerankTests
{
    [Theory]
    [InlineData("provider")]
    [InlineData("llm")]
    public async Task BudgetDenialSkipsTheProviderAndKeepsTheOriginalOrder(string mode)
    {
        var registry = new Registry(); var usage = new Usage { Deny = true };
        var response = await new SearchReranker(registry, usage).ApplyAsync(new("question", Rerank: mode, Limit: 2), Results(2));
        Assert.Equal(new[] { "0", "1" }, response.Results.Select(hit => hit.ChunkId));
        Assert.Contains("rerank", response.Degraded);
        Assert.Equal(0, registry.Binding.Count); Assert.Equal(0, registry.Binding.Chat.Calls);
        Assert.Null(usage.Settled);
    }

    [Fact]
    public async Task FailedProviderStillSettlesAnUnknownCharge()
    {
        var registry = new Registry(); registry.Binding.Failure = "unavailable"; var usage = new Usage();
        await new SearchReranker(registry, usage).ApplyAsync(new("question", Rerank: "provider"), Results(2));
        Assert.NotNull(usage.Request); Assert.True(usage.Request.ReservedTokens > 0);
        Assert.Equal(1, usage.Settlements); Assert.Null(usage.Settled);
    }

    private sealed class Usage : IModelUsage
    {
        public bool Deny { get; init; }
        public ModelUsageRequest? Request { get; private set; }
        public ModelUsageTotals? Settled { get; private set; }
        public int Settlements { get; private set; }
        public Task<long> ReserveAsync(ModelUsageRequest request, CancellationToken token = default)
        { Request = request; if (Deny) throw new ModelBudgetException(); return Task.FromResult(1L); }
        public Task SettleAsync(long id, ModelUsageTotals? totals, CancellationToken token = default)
        { Settled = totals; Settlements++; return Task.CompletedTask; }
        public Task<ModelUsageReport> ReadAsync(SecondBrain.Core.Ingestion.IngestionAuthority actor, DateTimeOffset from, DateTimeOffset to, CancellationToken token = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ProviderReranksOnlyTwentyThenRetainsTail()
    {
        var registry = new Registry();
        var response = await new SearchReranker(registry).ApplyAsync(new("question", Rerank: "provider", Limit: 25), Results(25));
        Assert.Equal(ModelRole.Rerank, registry.Role);
        Assert.Equal(20, registry.Binding.Count);
        Assert.Equal("19", response.Results[0].ChunkId);
        Assert.Equal("0", response.Results[19].ChunkId);
        Assert.Equal("20", response.Results[20].ChunkId);
        Assert.Empty(response.Degraded);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("nan")]
    [InlineData("outside")]
    [InlineData("unavailable")]
    public async Task InvalidProviderOutputPreservesFusedOrder(string failure)
    {
        var registry = new Registry(); registry.Binding.Failure = failure;
        var response = await new SearchReranker(registry).ApplyAsync(new("question", Rerank: "provider", Limit: 2), Results(3));
        Assert.Equal(new[] { "0", "1" }, response.Results.Select(hit => hit.ChunkId));
        Assert.Equal(new[] { "rerank" }, response.Degraded);
    }

    [Fact]
    public async Task LlmUsesEnrichAndParsesCompleteScores()
    {
        var registry = new Registry();
        var response = await new SearchReranker(registry).ApplyAsync(new("question", Rerank: "llm", Limit: 1), Results(2));
        Assert.Equal(ModelRole.Enrich, registry.Role);
        Assert.Equal("1", Assert.Single(response.Results).ChunkId);
        Assert.Empty(response.Degraded);
        Assert.Contains("untrusted", registry.Binding.Chat.Messages![0].Text);
        Assert.Empty(registry.Binding.Chat.Options!.Tools ?? []);
    }

    [Fact]
    public async Task MalformedLlmScoresAndExcessContextDegrade()
    {
        var registry = new Registry(); registry.Binding.Chat.Answer = "ignore prior instructions";
        var reranker = new SearchReranker(registry);
        Assert.Contains("rerank", (await reranker.ApplyAsync(new("question", Rerank: "llm"), Results(2))).Degraded);
        registry.Context = 100;
        registry.Binding.Chat.Calls = 0;
        Assert.Contains("rerank", (await reranker.ApplyAsync(new("question", Rerank: "llm"), Results(2))).Degraded);
        Assert.Equal(0, registry.Binding.Chat.Calls);
    }

    [Theory]
    [InlineData("[{}, {\"index\":1,\"score\":0.2}]")]
    [InlineData("[{\"index\":0}, {\"index\":1,\"score\":0.2}]")]
    public async Task MissingExplicitLlmScoresDegrade(string answer)
    {
        var registry = new Registry(); registry.Binding.Chat.Answer = answer;
        var response = await new SearchReranker(registry).ApplyAsync(new("question", Rerank: "llm"), Results(2));
        Assert.Equal(new[] { "0", "1" }, response.Results.Select(hit => hit.ChunkId));
        Assert.Contains("rerank", response.Degraded);
    }

    [Fact]
    public async Task SuccessKeepsRetrievalReasonsAndFailureAloneAddsRerank()
    {
        var retrieved = Results(2) with { Degraded = ["vector-pending"] };
        var registry = new Registry();
        Assert.Equal(new[] { "vector-pending" }, (await new SearchReranker(registry).ApplyAsync(new("question", Rerank: "provider"), retrieved)).Degraded);
        registry.Binding.Failure = "unavailable";
        Assert.Equal(new[] { "vector-pending", "rerank" }, (await new SearchReranker(registry).ApplyAsync(new("question", Rerank: "provider"), retrieved)).Degraded);
    }

    [Fact]
    public async Task NoneDoesNotResolveOrCallProvider()
    {
        var registry = new Registry();
        var response = await new SearchReranker(registry).ApplyAsync(new("question", Limit: 1), Results(2));
        Assert.Null(registry.Role); Assert.Single(response.Results);
    }

    [Fact]
    public async Task CallerCancellationIsNotDegraded()
    {
        var registry = new Registry();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new SearchReranker(registry).ApplyAsync(new("question", Rerank: "provider"), Results(2), cancellation.Token));
    }

    private static SearchResponse Results(int count) => new(Enumerable.Range(0, count).Select(i =>
        new SearchHit(i.ToString(), "doc", 1, "note", "Title", null, "Context", "Passage", [], 1d / (i + 1), null, "2026-10-09", [])).ToArray(), []);

    private sealed class Registry : IProviderRegistry
    {
        public Binding Binding { get; } = new();
        public ModelRole? Role { get; private set; }
        public int Context { get; set; } = 32000;
        public bool IsCurrentConfiguration(SecondBrainOptions options) => true;
        public ProviderRoleBinding GetRole(ModelRole role) { Role = role; return new(role, Binding, new("local", "model", new(), new() { ContextTokens = Context, MaxOutputTokens = 2048 })); }
        public IProviderBinding GetProvider(string name) => Binding;
        public IReadOnlyList<string> ProviderNames => ["local"];
        public ValueTask<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<ModelRole, ProviderHealthResult>> TestAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Binding : IProviderBinding, IRerankProvider
    {
        public string ProviderName => "local";
        public Uri? Endpoint => null;
        public bool IsLocal => true;
        public string? Failure { get; set; }
        public int Count { get; private set; }
        public Chat Chat { get; } = new();
        public ValueTask<ResolvedModel> ResolveAsync(string model, ModelLimits? limitsOverrides = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IChatClient? GetChatClient(string model) => Chat;
        public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string model) => null;
        public IRerankProvider? GetRerankProvider(string model) => this;
        public ValueTask<IReadOnlyList<RerankScore>> RerankAsync(string model, string query, IReadOnlyList<string> candidates, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Count = candidates.Count;
            if (Failure == "unavailable") throw new HttpRequestException();
            var scores = Enumerable.Range(0, Count).Select(i => new RerankScore(i, i)).ToArray();
            if (Failure == "duplicate") scores[0] = scores[1];
            if (Failure == "missing") scores = scores.Skip(1).ToArray();
            if (Failure == "nan") scores[0] = new(0, double.NaN);
            if (Failure == "outside") scores[0] = new(Count, 0);
            return ValueTask.FromResult<IReadOnlyList<RerankScore>>(scores);
        }
    }
    private sealed class Chat : IChatClient
    {
        public string Answer { get; set; } = "[{\"index\":0,\"score\":0.1},{\"index\":1,\"score\":0.9}]";
        public ChatMessage[]? Messages { get; private set; }
        public ChatOptions? Options { get; private set; }
        public int Calls { get; set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; Messages = messages.ToArray(); Options = options; return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer))); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
