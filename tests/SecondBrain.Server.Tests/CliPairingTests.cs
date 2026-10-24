using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class CliPairingTests
{
    [Fact]
    public async Task PairingExpiresAndSecureChallengeCannotDowngradeToHttp()
    {
        await using var factory = new LaneDWebFactory();
        using var terminal = Client(factory);
        var expiring = await Start(terminal);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.Conflict, (await terminal.PostAsJsonAsync("/auth/cli/poll", new CliPairingPollRequest(expiring.Id, expiring.PollSecret))).StatusCode);
        var service = factory.Services.GetRequiredService<CliPairingService>();
        var secure = await service.BeginAsync("secure terminal", "test", true, default);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => service.ClaimAsync(secure.Id, secure.PollSecret, false, default));
        Assert.Equal("pending", (await service.ClaimAsync(secure.Id, secure.PollSecret, true, default)).Status);
    }

    [Fact]
    public async Task ConcurrentClaimsIssueOnlyOneCredential()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var terminal = Client(factory);
        var start = await Start(terminal);
        using var browser = await Browser(factory);
        var csrf = (await browser.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/cli/approve", Form(start.Code, csrf))).StatusCode);
        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => terminal.PostAsJsonAsync("/auth/cli/poll", new CliPairingPollRequest(start.Id, start.PollSecret))));
        try
        {
            Assert.Single(claims, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(claims, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in claims) response.Dispose(); }
    }

    [Fact]
    public async Task BrowserApprovalIssuesSeparateSessionOnceAndRevocationStopsIt()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var terminal = Client(factory);
        var start = await Start(terminal);
        Assert.DoesNotContain(start.PollSecret, start.ToString());
        var waiting = await Poll(terminal, start);
        Assert.Equal("pending", waiting.Status);
        Assert.Null(waiting.Credential);
        using var browser = await Browser(factory);
        var page = await browser.GetStringAsync("/pair?code=" + start.Code);
        Assert.Contains(start.Code, page);
        Assert.Contains("test terminal", page);
        var csrf = (await browser.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        using var approved = await browser.PostAsync("/auth/cli/approve", Form(start.Code, csrf));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var claim = await Poll(terminal, start);
        Assert.Equal("approved", claim.Status);
        Assert.StartsWith("sbcli_", claim.Credential);
        Assert.DoesNotContain(claim.Credential!, claim.ToString());
        using var repeated = await terminal.PostAsJsonAsync("/auth/cli/poll", new CliPairingPollRequest(start.Id, start.PollSecret));
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        terminal.DefaultRequestHeaders.Authorization = new("Bearer", claim.Credential);
        var identity = await Identity(terminal);
        Assert.True(identity.IsCliSession);
        Assert.Equal("session", identity.Kind);
        Assert.Contains(Scope.Write, identity.Scopes);
        var row = await factory.Services.GetRequiredService<IAuthRepository>().FindAsync(identity.Id);
        Assert.Equal("cli", row!.Name);
        Assert.True(AuthTime.Parse(row.AbsoluteExpiresAt!) <= DateTimeOffset.UtcNow.AddHours(8));
        Assert.NotNull(row.SteppedUpAt);
        Assert.DoesNotContain(claim.Credential!, row.Verifier);
        await factory.Services.GetRequiredService<IAuthRepository>().RevokeAsync(identity.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await terminal.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task PairingRequiresBrowserPasswordAndAntiforgeryAndPollProof()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var terminal = Client(factory);
        var start = await Start(terminal);
        using var wrongProof = await terminal.PostAsJsonAsync("/auth/cli/poll", new CliPairingPollRequest(start.Id, new string('0', 64)));
        Assert.Equal(HttpStatusCode.Conflict, wrongProof.StatusCode);
        using var noBrowser = await terminal.PostAsync("/auth/cli/approve", new FormUrlEncodedContent(new Dictionary<string, string> { ["Code"] = start.Code, ["Password"] = "test-password" }));
        Assert.Equal(HttpStatusCode.Unauthorized, noBrowser.StatusCode);
        using var browser = await Browser(factory);
        using var noCsrf = await browser.PostAsync("/auth/cli/approve", new FormUrlEncodedContent(new Dictionary<string, string> { ["Code"] = start.Code, ["Password"] = "test-password" }));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        var csrf = (await browser.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        using var badPassword = await browser.PostAsync("/auth/cli/approve", Form(start.Code, csrf, "incorrect"));
        Assert.Equal(HttpStatusCode.Unauthorized, badPassword.StatusCode);
        Assert.Equal("pending", (await Poll(terminal, start)).Status);
        var key = await factory.CreateKeyAsync(Enum.GetValues<Scope>().ToHashSet());
        terminal.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminal.PostAsync("/auth/cli/approve", Form(start.Code, csrf))).StatusCode);
    }

    [Fact]
    public async Task RevokedBrowserCannotBeExchangedForAPairedSession()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var terminal = Client(factory);
        var start = await Start(terminal);
        using var browser = await Browser(factory);
        var csrf = (await browser.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/cli/approve", Form(start.Code, csrf))).StatusCode);
        var browserIdentity = await Identity(browser);
        await factory.Services.GetRequiredService<IAuthRepository>().RevokeAsync(browserIdentity.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminal.PostAsJsonAsync("/auth/cli/poll", new CliPairingPollRequest(start.Id, start.PollSecret))).StatusCode);
    }

    [Fact]
    public async Task PairedSessionCanApproveAProposalButCannotAutoApproveOrPairAnotherTerminal()
    {
        await using var factory = new LaneDWebFactory(options => options.Assistant.AutoApproveWrites = true);
        await factory.SeedAccountAsync();
        using var terminal = Client(factory);
        var start = await Start(terminal);
        using var browser = await Browser(factory);
        var csrf = (await browser.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/cli/approve", Form(start.Code, csrf))).StatusCode);
        var claim = await Poll(terminal, start);
        terminal.DefaultRequestHeaders.Authorization = new("Bearer", claim.Credential);
        var actor = await Identity(terminal);
        var turns = factory.Services.GetRequiredService<IChatRepository>();
        var proposals = factory.Services.GetRequiredService<IChatProposalRepository>();
        var turn = (await turns.StartAsync(new("save this"), new("mock", "mock-chat", AutoApproveNotes: true), actor)).Turn;
        var proposal = await proposals.ProposeAsync(turn.Id, "create_document", System.Text.Json.JsonSerializer.SerializeToElement(new { title = "Test", content = "Paired content" }), actor);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => proposals.ApproveAsync(proposal.Id, actor, automatic: true).AsTask());
        using var approval = await terminal.PostAsJsonAsync("/pending/" + proposal.Id + "/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        Assert.Equal("executed", (await approval.Content.ReadFromJsonAsync<ChatProposal>())!.Status);
        var next = await Start(terminal);
        var terminalCsrf = (await terminal.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        Assert.Equal(HttpStatusCode.Forbidden, (await terminal.PostAsync("/auth/cli/approve", Form(next.Code, terminalCsrf))).StatusCode);
    }

    private static HttpClient Client(LaneDWebFactory factory)
    {
        var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        return client;
    }
    private static async Task<AuthenticatedCredential> Identity(HttpClient client)
    {
        var value = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/auth/me");
        return new(value.GetProperty("id").GetString()!, value.GetProperty("kind").GetString()!, value.GetProperty("generation").GetInt64(),
            value.GetProperty("accountEpoch").GetInt64(), value.GetProperty("scopes").EnumerateArray().Select(scope => (Scope)scope.GetInt32()).ToHashSet(),
            value.GetProperty("steppedUpAt").GetString(), value.GetProperty("isCliSession").GetBoolean());
    }
    private static async Task<HttpClient> Browser(LaneDWebFactory factory)
    {
        var client = Client(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"))).StatusCode);
        return client;
    }
    private static async Task<CliPairingStarted> Start(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/auth/cli/start", new CliPairingBeginRequest("test terminal"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CliPairingStarted>())!;
    }
    private static async Task<CliPairingClaim> Poll(HttpClient client, CliPairingStarted request)
    {
        using var response = await client.PostAsJsonAsync("/auth/cli/poll", new CliPairingPollRequest(request.Id, request.PollSecret));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CliPairingClaim>())!;
    }
    private static FormUrlEncodedContent Form(string code, AntiforgeryResponse csrf, string password = "test-password") => new(new Dictionary<string, string>
    {
        ["Code"] = code, ["Password"] = password, ["__RequestVerificationToken"] = csrf.Token
    });
}
