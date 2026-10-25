using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Providers;
using SecondBrain.Extraction.Contracts;
using Xunit;

namespace SecondBrain.Core.Tests.Ingestion;

public sealed class ClassificationProcessorTests
{
    private static readonly ClassificationContext Work = new("doc", 1, "{}", "hash", "stamp", "file", "format-default", null, null, null);
    private static readonly ExtractionResult Extracted = new("extracted", "text", "text/plain", "A publication about the history of computing.");

    [Theory]
    [InlineData("override", "note", "user")]
    [InlineData("door", "article", "door")]
    [InlineData("frontmatter", "note", "frontmatter")]
    [InlineData("format", "transcript", "format")]
    [InlineData("map", "article", "type-map")]
    [InlineData("source", "note", "source")]
    public async Task StrongerRulesNeverSpendOnClassification(string rule, string type, string origin)
    {
        var work = rule switch
        {
            "override" => Work with { OverrideType = "note", Type = "article", TypeOrigin = "door" },
            "door" => Work with { Type = "article", TypeOrigin = "door" },
            "map" => Work with { MappedType = "article", SourceDefault = "note" },
            "source" => Work with { SourceDefault = "note" }, _ => Work
        };
        var extraction = rule switch { "frontmatter" => Extracted with { DeclaredType = "note" }, "format" => Extracted with { PhysicalFormat = "vtt" }, _ => Extracted };
        var fixture = new Fixture(work);
        Assert.Null(await fixture.Processor().ClassifyAsync("doc", 1, extraction));
        Assert.Equal(new ContentTypeChoice(type, origin), ContentTypeSelection.Fixed(work, extraction));
        Assert.Equal(0, fixture.Client.Calls); Assert.Empty(fixture.Usage.Requests);
    }

    [Fact]
    public async Task AcceptedResultUsesStructuredOutputBoundedInputAndADurableCache()
    {
        var f = new Fixture(); f.Client.Text = "```json\n{\"type\":\"article\",\"confidence\":0.94}\n```";
        var processor = f.Processor(); var extraction = Extracted with { LosslessText = string.Concat(Enumerable.Repeat("🌻 Long article. ", 4000)) };
        var result = await processor.ClassifyAsync("doc", 1, extraction);
        Assert.NotNull(result); Assert.Equal("article", result.Type); Assert.Equal(.94, result.Confidence);
        Assert.Equal("llm", ContentTypeSelection.Select(Work, extraction, result, f.Options.CurrentValue).Origin);
        Assert.IsType<ChatResponseFormatJson>(f.Client.Options!.ResponseFormat); Assert.Empty(f.Client.Options.Tools ?? []);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(f.Client.Messages![1].Text), 1, 8192);
        Assert.Equal("classify", Assert.Single(f.Usage.Requests).Role); Assert.Equal(1, f.Usage.Settlements);
        Assert.Equal(result, await processor.ClassifyAsync("doc", 1, extraction)); Assert.Equal(1, f.Client.Calls);
    }

    [Theory]
    [InlineData("not json", "invalid-response")]
    [InlineData("{\"type\":\"calendar_event\",\"confidence\":1}", "invalid-response")]
    [InlineData("{\"type\":\"article\",\"confidence\":1.1}", "invalid-response")]
    [InlineData("{\"type\":\"article\",\"type\":\"note\",\"confidence\":1}", "invalid-response")]
    [InlineData("{\"type\":\"article\",\"confidence\":0.2}", "low-confidence")]
    public async Task InvalidAndUncertainResultsFallBackWithoutBlocking(string response, string status)
    {
        var f = new Fixture(); f.Client.Text = response;
        var result = await f.Processor().ClassifyAsync("doc", 1, Extracted);
        Assert.NotNull(result); Assert.Equal(status, result.Status);
        Assert.Equal(new ContentTypeChoice("note", "format-default"), ContentTypeSelection.Select(Work, Extracted, result, f.Options.CurrentValue));
    }

    [Fact]
    public async Task BudgetDenialDoesNotReachTheProvider()
    {
        var f = new Fixture(); f.Usage.Deny = true;
        Assert.Equal("budget", (await f.Processor().ClassifyAsync("doc", 1, Extracted))!.Status);
        Assert.Equal(0, f.Client.Calls); Assert.Equal(0, f.Usage.Settlements);
    }

    [Fact]
    public async Task TimeoutFallsBackAndKeepsTheUsageReservation()
    {
        var f = new Fixture(); f.Client.Wait = true;
        Assert.Equal("timeout", (await f.Processor(TimeSpan.FromMilliseconds(20)).ClassifyAsync("doc", 1, Extracted))!.Status);
        Assert.Equal(1, f.Usage.Settlements); Assert.Null(f.Usage.Totals);
    }

    [Fact]
    public async Task ShutdownCancellationDoesNotCacheAFallback()
    {
        var f = new Fixture(); f.Client.Wait = true; using var cancellation = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Processor().ClassifyAsync("doc", 1, Extracted, cancellation.Token));
        Assert.Null(f.Repository.Saved); Assert.Equal(1, f.Usage.Settlements);
    }

    [Fact]
    public async Task LateResponseCannotPublishAcrossConfigurationOrDocumentChanges()
    {
        var f = new Fixture(); f.Client.OnResponse = () => f.Registry.Current = false;
        Assert.Null(await f.Processor().ClassifyAsync("doc", 1, Extracted)); Assert.Null(f.Repository.Saved);
        f = new Fixture(); f.Repository.CanSave = false;
        Assert.Null(await f.Processor().ClassifyAsync("doc", 1, Extracted));
    }

    private sealed class Fixture
    {
        public Repository Repository { get; }
        public Client Client { get; } = new();
        public Registry Registry { get; }
        public Usage Usage { get; } = new();
        public Monitor Options { get; } = new();
        public Fixture(ClassificationContext? work = null) { Repository = new(work ?? Work); Registry = new(Client); }
        public ClassificationProcessor Processor(TimeSpan? timeout = null) => new(Repository, Registry, Usage, Options, timeout);
    }
    private sealed class Repository(ClassificationContext context) : IClassificationWorkRepository
    {
        public ContentClassification? Saved { get; private set; }
        public bool CanSave { get; set; } = true;
        public Task<ClassificationContext?> ReadAsync(string documentId, long revision, CancellationToken token = default) => Task.FromResult<ClassificationContext?>(context);
        public Task<ContentClassification?> CachedAsync(ClassificationContext work, string fingerprint, CancellationToken token = default) => Task.FromResult(Saved?.Fingerprint == fingerprint ? Saved : null);
        public Task<bool> SaveAsync(ClassificationContext work, ContentClassification result, CancellationToken token = default)
        { if (CanSave) Saved = result; return Task.FromResult(CanSave); }
    }
    private sealed class Monitor : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue { get; } = new();
        public SecondBrainOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
    private sealed class Usage : IModelUsage
    {
        public bool Deny { get; set; }
        public List<ModelUsageRequest> Requests { get; } = [];
        public int Settlements { get; private set; }
        public ModelUsageTotals? Totals { get; private set; }
        public Task<long> ReserveAsync(ModelUsageRequest request, CancellationToken token = default)
        { Requests.Add(request); if (Deny) throw new ModelBudgetException(); return Task.FromResult(1L); }
        public Task SettleAsync(long id, ModelUsageTotals? totals, CancellationToken token = default) { Settlements++; Totals = totals; return Task.CompletedTask; }
        public Task<ModelUsageReport> ReadAsync(IngestionAuthority actor, DateTimeOffset from, DateTimeOffset to, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class Registry(Client client) : IProviderRegistry, IProviderBinding
    {
        public bool Current { get; set; } = true;
        public bool IsCurrentConfiguration(SecondBrainOptions options) => Current;
        public ProviderRoleBinding GetRole(ModelRole role) => new(role, this, new("local", "model", new() { StructuredOutput = true }, new() { ContextTokens = 32768, MaxOutputTokens = 512 }));
        public string ProviderName => "local";
        public Uri? Endpoint => null;
        public bool IsLocal => true;
        public IReadOnlyList<string> ProviderNames => ["local"];
        public IProviderBinding GetProvider(string name) => this;
        public IChatClient GetChatClient(string model) => client;
        public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string model) => null;
        public ValueTask<ResolvedModel> ResolveAsync(string model, ModelLimits? limitsOverrides = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(GetRole(ModelRole.Enrich).Model);
        public ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>(["model"]);
        public ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        ValueTask<IReadOnlyDictionary<string, IReadOnlyList<string>>> IProviderRegistry.ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<ModelRole, ProviderHealthResult>> TestAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Client : IChatClient
    {
        public string Text { get; set; } = "{\"type\":\"article\",\"confidence\":0.9}";
        public int Calls { get; private set; }
        public bool Wait { get; set; }
        public Action? OnResponse { get; set; }
        public ChatOptions? Options { get; private set; }
        public ChatMessage[]? Messages { get; private set; }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; Options = options; Messages = messages.ToArray(); if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken); OnResponse?.Invoke(); return new(new ChatMessage(ChatRole.Assistant, Text)) { Usage = new() { TotalTokenCount = 50 } }; }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
