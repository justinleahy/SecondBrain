using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.MockProvider;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ChatWorkflowTests
{
    [Fact]
    public async Task ModelWriteRemainsPendingUntilBrowserApprovesAndThenCreatesOneNote()
    {
        await using var factory = BufferedFactory();
        var actor = await CreateActorAsync(factory);
        var requests = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            if (Interlocked.Increment(ref requests) == 1)
                await ToolResponseAsync(context, "create_document", new { title = "Saved by assistant", content = "The project codename is Blue Otter." });
            else await TextResponseAsync(context, "The note is proposed and waiting for your approval.");
        };
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var proposals = factory.Services.GetRequiredService<IChatProposalRepository>();
        var started = await factory.Services.GetRequiredService<ChatService>().StartAsync(new("Please save this project note"), actor);
        var turn = await FinishAsync(repository, started.Turn.Id, actor);
        Assert.Equal("complete", turn.Status);
        var proposal = Assert.Single(await proposals.ListAsync(null, actor));
        Assert.Equal("pending", proposal.Status);
        Assert.False(File.Exists(proposal.Plan!.Destination));
        Assert.Equal("executed", (await proposals.ApproveAsync(proposal.Id, actor)).Status);
        Assert.Equal("The project codename is Blue Otter.", await File.ReadAllTextAsync(proposal.Plan.Destination));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task AskCannotExecuteAWriteEvenWhenTheProviderInventsOne()
    {
        await using var factory = BufferedFactory();
        var actor = await CreateActorAsync(factory);
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = context =>
            ToolResponseAsync(context, "create_document", new { title = "Forbidden", content = "Do not write" });
        var started = await factory.Services.GetRequiredService<ChatService>().StartAsync(new("Answer a question", Mode: "ask"), actor);
        var turn = await FinishAsync(factory.Services.GetRequiredService<IChatRepository>(), started.Turn.Id, actor);
        Assert.NotEqual("complete", turn.Status);
        Assert.Empty(await factory.Services.GetRequiredService<IChatProposalRepository>().ListAsync(null, actor));
        Assert.False(Directory.Exists(Path.Combine(factory.Options.CurrentValue.DataRoot, "vault", "notes")));
    }

    [Fact]
    public async Task InventedDocumentIdIsRejectedBeforeItsContentReachesTheProvider()
    {
        await using var factory = BufferedFactory();
        var actor = await CreateActorAsync(factory);
        var secret = (await factory.Services.GetRequiredService<IDocumentRepository>().CaptureAsync(new("UNRELATED PRIVATE SECRET", "Other"),
            new(actor.Id, actor.Generation, actor.AccountEpoch, "browser", "session"))).Document;
        var calls = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            Assert.DoesNotContain("UNRELATED PRIVATE SECRET", request.RootElement.GetRawText(), StringComparison.Ordinal);
            Interlocked.Increment(ref calls);
            await ToolResponseAsync(context, "read_document", new { id = secret.Id });
        };
        var started = await factory.Services.GetRequiredService<ChatService>().StartAsync(new("saffron astronauts", Mode: "ask"), actor);
        var turn = await FinishAsync(factory.Services.GetRequiredService<IChatRepository>(), started.Turn.Id, actor);
        Assert.NotEqual("complete", turn.Status);
        Assert.Equal(1, calls);
        Assert.Empty(await factory.Services.GetRequiredService<IChatRepository>().SourcesAsync(turn.Id, actor));
    }

    [Fact]
    public async Task AcceptedTurnSurvivesCallerCancellationAndPersistsSourcesAndReplay()
    {
        await using var factory = new LaneDWebFactory();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        var actor = new AuthenticatedCredential(key.Record.Id, key.Record.Kind, key.Record.Generation, key.Record.AccountEpoch, key.Record.GrantedScopes, null);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        await documents.CaptureAsync(new("The project codename is Blue Otter.", "Project note"),
            new(actor.Id, actor.Generation, actor.AccountEpoch, key.Record.Name, actor.Kind));
        var service = factory.Services.GetRequiredService<ChatService>();
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        using var request = new CancellationTokenSource();
        var started = await service.StartAsync(new("project codename", Mode: "ask", RequestId: "workflow"), actor, request.Token);
        request.Cancel();
        ChatTurn finished;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do
        {
            await Task.Delay(25, deadline.Token);
            finished = await repository.GetTurnAsync(started.Turn.Id, actor, deadline.Token);
        } while (finished.Status == "running");
        Assert.Equal("complete", finished.Status);
        Assert.Equal(MockProviderApplication.ChatResponse, finished.Answer!.Text);
        Assert.Contains("no_verified_sources", finished.Answer.Warnings!);
        Assert.Contains("Blue Otter", Assert.Single(await repository.SourcesAsync(finished.Id, actor)).Excerpt);
        var events = await repository.EventsAsync(finished.Id, 0, actor);
        Assert.Contains(events, item => item.Kind == "delta");
        Assert.Equal("finished", events[^1].Kind);
        Assert.Contains(events, item => item.Kind == "tool");
        var replay = await service.StartAsync(new("project codename", Mode: "ask", RequestId: "workflow"), actor);
        Assert.False(replay.Created); Assert.Equal(finished.Id, replay.Turn.Id);
    }

    [Fact]
    public async Task ReadOnlyCredentialCannotStartAnInferenceTurn()
    {
        await using var factory = new LaneDWebFactory();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        var actor = new AuthenticatedCredential(key.Record.Id, key.Record.Kind, key.Record.Generation, key.Record.AccountEpoch,
            new HashSet<Scope> { Scope.Read, Scope.Infer }, null);
        var service = factory.Services.GetRequiredService<ChatService>();
        await Assert.ThrowsAsync<AuthorityChangedException>(() => service.StartAsync(new("Unauthorized inference"), actor).AsTask());
        Assert.Equal(0, factory.MockProvider.Services.GetRequiredService<MockProviderState>().GetRequestCount("/v1/chat/completions"));
    }

    private static LaneDWebFactory BufferedFactory() => new(options =>
    {
        options.Models.Chat!.Capabilities = options.Models.Chat.Capabilities with { Streaming = false };
        options.Models.Chat.Limits = options.Models.Chat.Limits with { ContextTokens = 32768 };
    });
    private static async Task<AuthenticatedCredential> CreateActorAsync(LaneDWebFactory factory)
    {
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var created = factory.Services.GetRequiredService<CredentialFactory>().CreateSession("chat test", false, await auth.GetEpochAsync(), 12, 7);
        await auth.AddAsync(created.Record);
        return new(created.Record.Id, "session", created.Record.Generation, created.Record.AccountEpoch, created.Record.GrantedScopes, null);
    }
    private static async Task<ChatTurn> FinishAsync(IChatRepository repository, string turnId, AuthenticatedCredential actor)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        ChatTurn turn;
        do { await Task.Delay(25, deadline.Token); turn = await repository.GetTurnAsync(turnId, actor, deadline.Token); }
        while (turn.Status == "running");
        return turn;
    }
    private static Task ToolResponseAsync(HttpContext context, string tool, object arguments) => RespondAsync(context,
        new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = tool, arguments = JsonSerializer.Serialize(arguments) } } } }, "tool_calls");
    private static Task TextResponseAsync(HttpContext context, string text) => RespondAsync(context, new { role = "assistant", content = text }, "stop");
    private static Task RespondAsync(HttpContext context, object message, string finish) => context.Response.WriteAsJsonAsync(new
    {
        id = "chatcmpl-scripted", @object = "chat.completion", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = "mock-chat",
        choices = new[] { new { index = 0, message, finish_reason = finish } },
        usage = new { prompt_tokens = 5, completion_tokens = 5, total_tokens = 10 }
    });
}
