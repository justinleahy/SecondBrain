using System.Net.Http.Json;
using System.Text.Json;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliSourceTests
{
    private const string Link = "webcal://feed.test/published/2/synthetic-private-link";
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StdinAndHiddenPromptSendOneRegistrationAndDoNotEchoTheLink(bool stdin)
    {
        using var handler = new Handler(); var commands = Commands(handler); var prompts = 0;
        var services = new CliServices(false) { Sources = commands, Input = new StringReader(Link + "\n"),
            ReadCalendarLink = _ => { prompts++; return Task.FromResult(Link); } };
        var output = new StringWriter(); var error = new StringWriter();
        string[] args = stdin ? ["sources", "add-calendar", "--name", "Shared", "--poll-interval", "300", "--url-stdin", "--json"] :
            ["sources", "add-calendar", "--name", "Shared", "--poll-interval", "300", "--json"];
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(args, services, output, error));
        Assert.Equal(stdin ? 0 : 1, prompts);
        var sent = Assert.Single(handler.Sent);
        Assert.Equal("POST /v1/sources", sent.Path);
        using var json = JsonDocument.Parse(sent.Body!);
        Assert.Equal(Link, json.RootElement.GetProperty("url").GetString());
        Assert.Equal(300, json.RootElement.GetProperty("poll_interval_s").GetInt32());
        Assert.Contains("queued", output.ToString());
        Assert.DoesNotContain("synthetic-private-link", output.ToString() + error);
    }
    [Theory]
    [InlineData("https://person:synthetic-private-link@feed.test/path")]
    [InlineData("ftp://feed.test/synthetic-private-link")]
    [InlineData("https://feed.test/first\nhttps://feed.test/second")]
    public async Task InvalidInputDoesNotContactDaemonOrLeakTheValue(string link)
    {
        using var handler = new Handler(); var output = new StringWriter(); var error = new StringWriter();
        var services = new CliServices(false) { Sources = Commands(handler), Input = new StringReader(link) };
        Assert.Equal(2, await SecondBrain.Cli.Program.RunAsync(["sources", "add-calendar", "--name", "Shared", "--url-stdin", "--json"], services, output, error));
        Assert.Empty(handler.Sent); Assert.DoesNotContain("synthetic-private-link", output.ToString() + error);
    }
    [Fact]
    public async Task OversizedInputIsBoundedBeforeSending()
    {
        using var handler = new Handler();
        Assert.Equal(2, await SecondBrain.Cli.Program.RunAsync(["sources", "add-calendar", "--name", "Shared", "--url-stdin", "--json"],
            new CliServices(false) { Sources = Commands(handler), Input = new StringReader(new string('x', 100_000)) }, new StringWriter(), new StringWriter()));
        Assert.Empty(handler.Sent);
    }
    [Fact]
    public async Task ListAndDeepScanUseExistingDaemonCredentials()
    {
        using var handler = new Handler(); var services = new CliServices(false) { Sources = Commands(handler) };
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(["sources", "list", "--json"], services, new StringWriter(), new StringWriter()));
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(["sources", "scan", "source", "--deep", "--json"], services, new StringWriter(), new StringWriter()));
        Assert.Equal(new[] { "GET /v1/sources", "POST /v1/sources/source/scan?deep=1" }, handler.Sent.Select(item => item.Path));
    }
    [Theory]
    [InlineData("pause", "PATCH", "disabled")]
    [InlineData("resume", "PATCH", "active")]
    [InlineData("remove", "DELETE", null)]
    public async Task LifecycleRequiresLoginAndStepUp(string operation, string method, string? status)
    {
        using var handler = new Handler(); var services = new CliServices(false) { Sources = Commands(handler) };
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(["sources", operation, "source", "--json"], services, new StringWriter(), new StringWriter()));
        var change = Assert.Single(handler.Sent, item => item.Path == method + " /v1/sources/source");
        Assert.Contains(handler.Sent, item => item.Path == "POST /auth/login"); Assert.Contains(handler.Sent, item => item.Path == "POST /auth/step-up");
        Assert.Contains(handler.Sent, item => item.Path == "POST /auth/logout");
        if (status is not null) Assert.Equal(status, JsonDocument.Parse(change.Body!).RootElement.GetProperty("status").GetString());
    }
    [Fact]
    public async Task PurgeUsesTheExplicitQueryAndRecentPasswordSession()
    {
        using var handler = new Handler(); var services = new CliServices(false) { Sources = Commands(handler) };
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(["sources", "remove", "source", "--purge", "--json"], services, new StringWriter(), new StringWriter()));
        Assert.Single(handler.Sent, item => item.Path == "DELETE /v1/sources/source?purge=1");
        Assert.Contains(handler.Sent, item => item.Path == "POST /auth/step-up");
        Assert.Contains(handler.Sent, item => item.Path == "POST /auth/logout");
    }
    [Fact]
    public async Task RetentionRunsThroughTheDaemonWithARecentPasswordSession()
    {
        using var handler = new Handler(); var commands = Commands(handler);
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(["maintenance", "retention", "--json"],
            new CliServices(false) { Knowledge = commands }, new StringWriter(), new StringWriter()));
        Assert.Single(handler.Sent, item => item.Path == "POST /v1/maintenance/retention");
        Assert.Contains(handler.Sent, item => item.Path == "POST /auth/step-up");
        Assert.Contains(handler.Sent, item => item.Path == "POST /auth/logout");
    }
    private static HttpDaemonCommands Commands(Handler handler)
    {
        var commands = new HttpDaemonCommands(() => new Credentials(), (_, _) => Task.FromResult("fixture-password"), () => new HttpClient(handler, false));
        commands.Configure(() => throw new InvalidOperationException(), "http://daemon.test", "test"); return commands;
    }
    private sealed class Credentials : ICredentialStore
    {
        public Task StoreAsync(Uri origin, string name, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(Uri origin, string name, CancellationToken cancellationToken = default) => Task.FromResult<string?>(name.EndsWith(".access", StringComparison.Ordinal) ? null : "fixture-key");
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal List<(string Path, string? Body)> Sent { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.PathAndQuery;
            Sent.Add((request.Method + " " + path, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            object response = path switch
            {
                "/auth/antiforgery" => new { headerName = "X-CSRF-TOKEN", token = "fixture" },
                "/auth/login" => new { id = "session" },
                _ => new { id = "source", queued = true }
            };
            return new(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
}
