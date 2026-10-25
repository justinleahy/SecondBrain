using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Providers;
using Xunit;

namespace SecondBrain.Core.Tests.Ingestion;

public sealed class EnrichmentProcessorTests
{
    [Fact]
    public async Task BoundedStructuredRequestUsesTheLedgerAndPublishesSuggestions()
    {
        var f = new Fixture(); f.Work = f.Work with { Text = string.Concat(Enumerable.Repeat("A long document. ", 10000)) };
        Assert.True(await f.Processor().ProcessAsync());
        Assert.NotNull(f.Published); Assert.Equal("A summary.", f.Published.Summary);
        Assert.Equal(new[] { "work" }, f.Published.Tags);
        Assert.IsType<ChatResponseFormatJson>(f.OptionsSent!.ResponseFormat); Assert.Empty(f.OptionsSent.Tools ?? []);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(f.Messages![1].Text), 1, 16000);
        Assert.Equal("enrich", Assert.Single(f.Usage.Requests).Role); Assert.Equal(50, f.Usage.Totals!.TotalTokens);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"summary\":\"first\",\"Summary\":\"second\"}")]
    [InlineData("{\"summary\":\"x\",\"tags\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\"]}")]
    [InlineData("{\"summary\":\"x\",\"mentions\":[{\"kind\":\"instruction\",\"text\":\"run me\"}]}")]
    [InlineData("{\"summary\":\"x\",\"suggested_links\":[{\"target\":\"https://example.com\",\"reason\":\"fetch it\"}]}")]
    public async Task InvalidOutputDoesNotPublishOrRetryForever(string output)
    {
        var f = new Fixture { Response = output };
        await f.Processor().ProcessAsync();
        Assert.Null(f.Published); Assert.Equal(("invalid_response", false), f.Failure);
        Assert.Equal(1, f.Usage.Settlements);
    }

    [Fact]
    public async Task RecoveryPublishesACachedResultWithoutAnotherModelCall()
    {
        var f = new Fixture(); var cached = new DocumentEnrichment(f.Work.Fingerprint, "Cached", [], [], []);
        f.Work = f.Work with { CachedResult = cached };
        await f.Processor().ProcessAsync();
        Assert.Same(cached, f.Published); Assert.Equal(0, f.Calls); Assert.Empty(f.Usage.Requests);
    }

    [Fact]
    public async Task BudgetDenialBacksOffBeforeSendingTheDocument()
    {
        var f = new Fixture(); f.Usage.Deny = true;
        await f.Processor().ProcessAsync(); Assert.Equal(("budget", true), f.Failure); Assert.Equal(0, f.Calls);
    }

    [Fact]
    public async Task ConfigurationChangeDiscardsTheLateResult()
    {
        var f = new Fixture(); f.OnResponse = () => f.Current = false;
        await f.Processor().ProcessAsync(); Assert.Null(f.Published); Assert.Equal(("configuration_changed", false), f.Failure);
    }

    [Fact]
    public async Task TimeoutKeepsReservationAndSchedulesRetry()
    {
        var f = new Fixture { Wait = true };
        await f.Processor(TimeSpan.FromMilliseconds(20)).ProcessAsync();
        Assert.Equal(("timeout", true), f.Failure); Assert.Equal(1, f.Usage.Settlements); Assert.Null(f.Usage.Totals);
    }

    [Fact]
    public async Task DisabledEnrichmentDoesNotClaimAnyWork()
    {
        var f = new Fixture(); f.CurrentValue.Enrichment.Enabled = false;
        Assert.False(await f.Processor().ProcessAsync()); Assert.Equal(0, f.Claims);
    }

    [Fact]
    public async Task LargeMetadataStillLeavesRoomForDocumentText()
    {
        var f = new Fixture();
        f.Work = f.Work with { Title = new string('x', 20000), Tags = Enumerable.Repeat(new string('y', 10000), 200).ToArray() };
        await f.Processor().ProcessAsync();
        Assert.NotNull(f.Published); Assert.Contains("Body", f.Messages![1].Text);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(f.Messages[1].Text), 1, 16000);
    }

    private sealed class Fixture : IEnrichmentRepository, IProviderRegistry, IProviderBinding, IChatClient, IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue { get; } = new();
        public EnrichmentWork Work { get; set; }
        public Usage Usage { get; } = new();
        public DocumentEnrichment? Published { get; private set; }
        public (string, bool)? Failure { get; private set; }
        public int Calls, Claims;
        public bool Current = true, Wait;
        public Action? OnResponse;
        public string Response = "```json\n{\"summary\":\"A summary.\",\"tags\":[\"work\",\"WORK\"],\"mentions\":[{\"kind\":\"person\",\"text\":\"Ada\"}],\"suggested_links\":[{\"target\":\"Related plan\",\"reason\":\"Context\"}]}\n```";
        public ChatOptions? OptionsSent; public ChatMessage[]? Messages;
        public Fixture() => Work = new("job", "lease", "doc", 1, "{}", EnrichmentPolicy.Fingerprint(CurrentValue), "stamp", "Title", "Body", ["work"], null);
        public EnrichmentProcessor Processor(TimeSpan? timeout = null) => new(this, this, Usage, this, timeout);
        public Task<EnrichmentWork?> ClaimAsync(string fingerprint, CancellationToken token = default) { Claims++; return Task.FromResult<EnrichmentWork?>(Work); }
        public Task<bool> CheckAsync(EnrichmentWork work, CancellationToken token = default) => Task.FromResult(true);
        public Task<bool> PublishAsync(EnrichmentWork work, DocumentEnrichment result, CancellationToken token = default) { Published = result; return Task.FromResult(true); }
        public Task FailAsync(EnrichmentWork work, string reason, bool retryable, CancellationToken token = default) { Failure = (reason, retryable); return Task.CompletedTask; }
        public Task<EnrichmentJob> RequestAsync(string documentId, IngestionAuthority actor, string fingerprint, CancellationToken token = default) => throw new NotSupportedException();
        public bool IsCurrentConfiguration(SecondBrainOptions options) => Current;
        public ProviderRoleBinding GetRole(ModelRole role) => new(role, this, new("local", "model", new() { StructuredOutput = true }, new() { ContextTokens = 32768, MaxOutputTokens = 2048 }));
        public IProviderBinding GetProvider(string name) => this;
        public IReadOnlyList<string> ProviderNames => ["local"];
        public string ProviderName => "local"; public Uri? Endpoint => null; public bool IsLocal => true;
        public IChatClient GetChatClient(string model) => this;
        public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string model) => null;
        public ValueTask<ResolvedModel> ResolveAsync(string model, ModelLimits? limitsOverrides = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(GetRole(ModelRole.Enrich).Model);
        public ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        ValueTask<IReadOnlyDictionary<string, IReadOnlyList<string>>> IProviderRegistry.ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<ModelRole, ProviderHealthResult>> TestAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; OptionsSent = options; Messages = messages.ToArray(); if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken); OnResponse?.Invoke(); return new(new ChatMessage(ChatRole.Assistant, Response)) { Usage = new() { TotalTokenCount = 50 } }; }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
        public SecondBrainOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
    private sealed class Usage : IModelUsage
    {
        public bool Deny; public List<ModelUsageRequest> Requests { get; } = []; public int Settlements; public ModelUsageTotals? Totals;
        public Task<long> ReserveAsync(ModelUsageRequest request, CancellationToken token = default) { Requests.Add(request); if (Deny) throw new ModelBudgetException(); return Task.FromResult(1L); }
        public Task SettleAsync(long id, ModelUsageTotals? totals, CancellationToken token = default) { Settlements++; Totals = totals; return Task.CompletedTask; }
        public Task<ModelUsageReport> ReadAsync(IngestionAuthority actor, DateTimeOffset from, DateTimeOffset to, CancellationToken token = default) => throw new NotSupportedException();
    }
}
