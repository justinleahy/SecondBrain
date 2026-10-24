using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ChatApiTests
{
    [Fact]
    public async Task DeleteConversationApiRequiresOwnershipAndWriteScope()
    {
        await using var factory = Factory();
        var owner = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        var actor = new AuthenticatedCredential(owner.Record.Id, "api_key", owner.Record.Generation, owner.Record.AccountEpoch, owner.Record.GrantedScopes, null);
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var turn = (await repository.StartAsync(new("Delete through API"), new("mock", "mock-chat"), actor)).Turn;
        await repository.FinishAsync(turn.Id, "complete", new("Finished", []));
        using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        var path = "/v1/conversations/" + turn.ConversationId;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync(path)).StatusCode);
        var reader = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        client.DefaultRequestHeaders.Authorization = new("Bearer", reader.Plaintext);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(path)).StatusCode);
        var other = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write });
        client.DefaultRequestHeaders.Authorization = new("Bearer", other.Plaintext);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Plaintext);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task BrowserConversationDeletionRequiresAntiforgery()
    {
        await using var factory = Factory();
        await factory.SeedAccountAsync();
        using var browser = factory.CreatePrivateClient();
        browser.DefaultRequestHeaders.Add("Origin", "http://private.test");
        await browser.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        var csrf = (await browser.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        browser.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        var start = (await (await browser.PostAsJsonAsync("/v1/ask", new ChatRequest("Hello"))).Content.ReadFromJsonAsync<ChatStart>())!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((await browser.GetFromJsonAsync<ChatTurn>("/v1/turns/" + start.Turn.Id, deadline.Token))!.Status == "running")
            await Task.Delay(25, deadline.Token);
        browser.DefaultRequestHeaders.Remove(csrf.HeaderName);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.DeleteAsync("/conversations/" + start.Turn.ConversationId)).StatusCode);
        browser.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync("/conversations/" + start.Turn.ConversationId)).StatusCode);
    }

    [Fact]
    public async Task AskApiPersistsItsTurnAndReplaysEventsAfterTheRequestedSequence()
    {
        await using var factory = Factory();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Infer });
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        var request = new ChatRequest("Hello", RequestId: "api-replay");
        using var accepted = await client.PostAsJsonAsync("/v1/ask", request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var start = (await accepted.Content.ReadFromJsonAsync<ChatStart>())!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        ChatTurn? turn;
        do { await Task.Delay(25, deadline.Token); turn = await client.GetFromJsonAsync<ChatTurn>("/v1/turns/" + start.Turn.Id, deadline.Token); }
        while (turn!.Status == "running");
        Assert.Equal("ask", turn.Mode); Assert.Equal("complete", turn.Status);
        using var replayRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/turns/" + turn.Id + "/events");
        replayRequest.Headers.Add("Last-Event-ID", "1");
        using var replay = await client.SendAsync(replayRequest, deadline.Token);
        Assert.Equal("text/event-stream", replay.Content.Headers.ContentType!.MediaType);
        var events = await replay.Content.ReadAsStringAsync(deadline.Token);
        Assert.DoesNotContain("id: 1\n", events); Assert.Contains("event: tool\n", events); Assert.Contains("event: finished\n", events);
        using var repeated = await client.PostAsJsonAsync("/v1/ask", request);
        var duplicate = (await repeated.Content.ReadFromJsonAsync<ChatStart>())!;
        Assert.False(duplicate.Created); Assert.Equal(turn.Id, duplicate.Turn.Id);
        var manifest = await client.GetFromJsonAsync<ChatEgressManifest>("/v1/turns/" + turn.Id + "/manifest");
        Assert.Equal("mock", manifest!.Provider);
        Assert.True(manifest.ChargedTokens > 0);
    }

    [Fact]
    public async Task ApiEnforcesInferenceScopeAndConversationOwnership()
    {
        await using var factory = Factory(); using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var anonymous = await client.PostAsJsonAsync("/v1/chat", new ChatRequest("Hello"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var reader = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        client.DefaultRequestHeaders.Authorization = new("Bearer", reader.Plaintext);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/v1/chat", new ChatRequest("Hello"))).StatusCode);
        var inference = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Infer });
        client.DefaultRequestHeaders.Authorization = new("Bearer", inference.Plaintext);
        var first = (await (await client.PostAsJsonAsync("/v1/ask", new ChatRequest("Hello"))).Content.ReadFromJsonAsync<ChatStart>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", reader.Plaintext);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/turns/" + first.Turn.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/turns/" + first.Turn.Id + "/events")).StatusCode);
    }

    [Fact]
    public async Task ApprovalApiRejectsKeysAndRequiresBrowserAntiforgery()
    {
        await using var factory = Factory();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        var actor = new AuthenticatedCredential(key.Record.Id, "api_key", key.Record.Generation, key.Record.AccountEpoch, key.Record.GrantedScopes, null);
        var turn = (await factory.Services.GetRequiredService<IChatRepository>().StartAsync(new("Save"), new("mock", "mock-chat"), actor)).Turn;
        var proposal = await factory.Services.GetRequiredService<IChatProposalRepository>().ProposeAsync(turn.Id, "create_document",
            JsonSerializer.SerializeToElement(new { title = "API proposal", content = "Needs browser approval" }), actor);
        using var keyClient = factory.CreatePrivateClient(); keyClient.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        Assert.Equal(HttpStatusCode.Forbidden, (await keyClient.PostAsync("/v1/pending/" + proposal.Id + "/approve", null)).StatusCode);
        await factory.SeedAccountAsync(); using var browser = factory.CreatePrivateClient();
        browser.DefaultRequestHeaders.Add("Origin", "http://private.test");
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/v1/pending/" + proposal.Id + "/approve", null)).StatusCode);
        var csrf = (await browser.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        browser.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        using var approval = await browser.PostAsync("/v1/pending/" + proposal.Id + "/approve", null);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        Assert.Equal("executed", (await approval.Content.ReadFromJsonAsync<ChatProposal>())!.Status);
        Assert.Equal("Needs browser approval", await File.ReadAllTextAsync(proposal.Plan!.Destination));
    }

    private static LaneDWebFactory Factory() => new(options => options.Models.Chat!.Limits = options.Models.Chat.Limits with { ContextTokens = 32768 });
}
