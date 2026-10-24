using Microsoft.Extensions.DependencyInjection;
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
}
