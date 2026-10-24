using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.Configuration;
using SecondBrain.MockProvider;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed partial class ChatWorkflowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReasoningPreferenceIsCapabilityGatedAndPrivateReasoningIsNotDisplayed(bool supported)
    {
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Models.Chat!.Reasoning = "high";
            options.Models.Chat.Capabilities = options.Models.Chat.Capabilities with { Streaming = false, ReasoningControl = supported };
            options.Models.Chat.Limits = options.Models.Chat.Limits with { ContextTokens = 32768, MaxOutputTokens = 4096 };
        });
        var actor = await CreateActorAsync(factory);
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            if (supported) Assert.Equal("high", request.RootElement.GetProperty("reasoning_effort").GetString());
            else Assert.False(request.RootElement.TryGetProperty("reasoning_effort", out _));
            Assert.Equal(4096, request.RootElement.GetProperty("max_completion_tokens").GetInt32());
            await context.Response.WriteAsJsonAsync(new
            {
                id = "reasoning-test", model = "mock-chat", @object = "chat.completion", created = 0,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "Visible answer.", reasoning_content = "PRIVATE_REASONING_TRACE" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 10, completion_tokens = 10, total_tokens = 20 }
            });
        };
        var started = await factory.Services.GetRequiredService<ChatService>().StartAsync(new("Question"), actor);
        var final = await FinishAsync(factory.Services.GetRequiredService<IChatRepository>(), started.Turn.Id, actor);
        Assert.Equal("complete", final.Status);
        Assert.Equal("Visible answer.", final.Answer!.Text);
        Assert.Equal(supported ? "high" : null, final.Policy.Reasoning);
        Assert.Equal(!supported, final.Answer.Warnings!.Contains("reasoning_ignored"));
    }

    [Fact]
    public async Task ExplicitFallbackBeforeAnyWriteReusesTheTurnAndRecordedRetrieval()
    {
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Models.Chat!.Capabilities = options.Models.Chat.Capabilities with { Streaming = false };
            options.Models.Chat.Limits = options.Models.Chat.Limits with { ContextTokens = 32768 };
            options.Models.Chat.Fallback = new ModelBindingOptions
            {
                Provider = "mock", Model = "fallback-chat", Capabilities = new() { Tools = true, Streaming = false },
                Limits = new() { ContextTokens = 32768, MaxOutputTokens = 1024 }
            };
        });
        var actor = await CreateActorAsync(factory);
        var requests = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            if (Interlocked.Increment(ref requests) == 1) context.Response.StatusCode = 503;
            else
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body);
                Assert.Equal("fallback-chat", body.RootElement.GetProperty("model").GetString());
                await TextResponseAsync(context, "The fallback answered.");
            }
        };
        var service = factory.Services.GetRequiredService<ChatService>();
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var turn = await service.StartAsync(new("Question"), actor);
        Assert.Equal("failed", (await FinishAsync(repository, turn.Turn.Id, actor)).Status);
        var retried = await service.RetryAsync(turn.Turn.Id, new("fallback", UseFallback: true), actor);
        Assert.Equal(turn.Turn.Id, retried.Turn.Id);
        var completed = await FinishAsync(repository, retried.Turn.Id, actor);
        Assert.Equal("complete", completed.Status);
        Assert.Equal("fallback-chat", completed.Policy.Model);
        Assert.Single((await repository.TranscriptAsync(completed.ConversationId, actor))[0].Calls, call => call.Name == "search");
        var manifest = await factory.Services.GetRequiredService<IChatExecutionRepository>().ManifestAsync(completed.Id, actor);
        Assert.Equal(new[] { "mock-chat", "fallback-chat" }, manifest.ProviderCalls!.Where(call => call.Model != "mock-embed").Select(call => call.Model));
    }

    [Fact]
    public async Task RetryingAnApprovedAppendDoesNotAppendAgain()
    {
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Models.Chat!.Capabilities = options.Models.Chat.Capabilities with { Streaming = false };
            options.Models.Chat.Limits = options.Models.Chat.Limits with { ContextTokens = 32768 };
            options.Assistant.AutoApproveWrites = true;
        }, services => services.AddSingleton<IRetrievalRepository>(new EmptyRetryRetrieval()));
        var actor = await CreateActorAsync(factory);
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var proposals = factory.Services.GetRequiredService<IChatProposalRepository>();
        var seed = (await repository.StartAsync(new("Seed note"), new("mock", "mock-chat"), actor)).Turn;
        var original = await proposals.ProposeAsync(seed.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Journal", content = "Original" }), actor);
        await proposals.ApproveAsync(original.Id, actor);
        await repository.FinishAsync(seed.Id, "complete", new("Seeded", []));
        var id = original.Plan!.DocumentId;
        var requests = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            var count = Interlocked.Increment(ref requests);
            if (count is 1 or 3) await ToolResponseAsync(context, "update_document", new { id, revision = 1, content = "Append once", append = true });
            else if (count == 2) context.Response.StatusCode = 503;
            else await TextResponseAsync(context, "The append was already applied.");
        };
        var service = factory.Services.GetRequiredService<ChatService>();
        var turn = await service.StartAsync(new("Append to " + id), actor);
        Assert.NotEqual("complete", (await FinishAsync(repository, turn.Turn.Id, actor)).Status);
        var retry = await service.RetryAsync(turn.Turn.Id, new("append-retry"), actor);
        Assert.Equal("complete", (await FinishAsync(repository, retry.Turn.Id, actor)).Status);
        var document = (await factory.Services.GetRequiredService<IDocumentRepository>().GetAsync(id, true))!;
        Assert.Equal(2, document.Revision);
        Assert.Equal("Original\nAppend once", await File.ReadAllTextAsync(document.Path!));
        Assert.Single(await proposals.ListAsync(null, actor), item => item.Tool == "update_document");
    }

    [Fact]
    public async Task ProviderSwitchBetweenTurnsUsesPortableHistoryAndNewBinding()
    {
        await using var factory = BufferedFactory();
        var actor = await CreateActorAsync(factory);
        await using var secondProvider = MockProviderApplication.Build(["--urls", "http://127.0.0.1:0"]);
        await secondProvider.StartAsync();
        var firstRequests = 0;
        var secondRequests = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            Interlocked.Increment(ref firstRequests);
            await TextResponseAsync(context, "PORTABLE_PRIOR_ANSWER");
        };
        secondProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            Interlocked.Increment(ref secondRequests);
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            Assert.Contains("PORTABLE_PRIOR_ANSWER", body.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("previous_response_id", body.RootElement.GetRawText(), StringComparison.Ordinal);
            await TextResponseAsync(context, "I can read the prior conversation.");
        };
        var service = factory.Services.GetRequiredService<ChatService>();
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var first = await service.StartAsync(new("Remember this first exchange"), actor);
        Assert.Equal("complete", (await FinishAsync(repository, first.Turn.Id, actor)).Status);
        var next = new YamlConfigurationLoader(environment: _ => null).Load(factory.ConfigPath);
        next.Providers["second"] = new ProviderOptions { Kind = "openai_compatible", Endpoint = secondProvider.Urls.Single() + "/v1", Trusted = true };
        next.Privacy.TrustedServices = next.Privacy.TrustedServices.Append(secondProvider.Urls.Single()).ToList();
        next.Models.Chat!.Provider = "second";
        factory.Options.Update(next);
        var second = await service.StartAsync(new("Continue from the previous answer", first.Turn.ConversationId), actor);
        var completed = await FinishAsync(repository, second.Turn.Id, actor);
        Assert.Equal("complete", completed.Status);
        Assert.Equal("second", completed.Policy.Provider);
        Assert.Equal(1, firstRequests);
        Assert.Equal(1, secondRequests);
        var transcript = await repository.TranscriptAsync(completed.ConversationId, actor);
        Assert.Equal(2, transcript.Count);
        Assert.Contains(transcript[0].Calls, call => call.Name == "search");
    }

    [Fact]
    public async Task RetryReplaysACommittedCreateInsteadOfCreatingItAgain()
    {
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Models.Chat!.Capabilities = options.Models.Chat.Capabilities with { Streaming = false };
            options.Models.Chat.Limits = options.Models.Chat.Limits with { ContextTokens = 32768 };
            options.Assistant.AutoApproveWrites = true;
        });
        var actor = await CreateActorAsync(factory);
        var requests = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var count = Interlocked.Increment(ref requests);
            if (count is 1 or 3)
            {
                if (count == 3) Assert.Contains("executed", body.RootElement.GetRawText(), StringComparison.Ordinal);
                await ToolResponseAsync(context, "create_document", new { title = "Exactly once", content = "One durable body" });
            }
            else if (count == 2) context.Response.StatusCode = 503;
            else await TextResponseAsync(context, "The note has already been saved.");
        };
        var service = factory.Services.GetRequiredService<ChatService>();
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var proposals = factory.Services.GetRequiredService<IChatProposalRepository>();
        var original = await service.StartAsync(new("Save a note"), actor);
        var failed = await FinishAsync(repository, original.Turn.Id, actor);
        Assert.NotEqual("complete", failed.Status);
        var proposal = Assert.Single(await proposals.ListAsync(null, actor));
        Assert.Equal("executed", proposal.Status);
        var retry = await service.RetryAsync(original.Turn.Id, new("retry-once"), actor);
        var completed = await FinishAsync(repository, retry.Turn.Id, actor);
        Assert.Equal("complete", completed.Status);
        Assert.Equal(original.Turn.Id, completed.Id);
        Assert.Single(await proposals.ListAsync(null, actor));
        Assert.Equal("One durable body", await File.ReadAllTextAsync(proposal.Plan!.Destination));
        Assert.Equal(4, requests);
        var transcript = Assert.Single(await repository.TranscriptAsync(completed.ConversationId, actor));
        Assert.Contains(transcript.Calls, call => call.Name == "create_document" && call.Status == "executed");
        var duplicate = await service.RetryAsync(original.Turn.Id, new("retry-once"), actor);
        Assert.False(duplicate.Created);
        Assert.Equal(4, requests);
    }

    private sealed class EmptyRetryRetrieval : IRetrievalRepository
    {
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, float[]? queryVector = null, string? spaceId = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(new SearchResponse([], []));
        public ValueTask<QueryPage> QueryAsync(QueryRequest request, IngestionAuthority authority, CancellationToken cancellationToken = default) => ValueTask.FromResult(new QueryPage([], null, false));
        public ValueTask<IReadOnlyList<ContextPassage>> ExpandAsync(IReadOnlyList<SearchHit> selected, int tokenBudget, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ContextPassage>>([]);
        public ValueTask RecordTraceAsync(SearchRequest request, SearchResponse response, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
