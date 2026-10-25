using System.Net;
using System.Net.Http.Json;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliUsageTests
{
    [Fact]
    public async Task UsageForwardsTheBoundedDateRangeAndReturnsTheReport()
    {
        using var handler = new Handler();
        var commands = new HttpDaemonCommands(() => new Credentials(), (_, _) => throw new InvalidOperationException(), () => new HttpClient(handler, false));
        commands.Configure(() => throw new InvalidOperationException(), "http://daemon.test", "fixture");
        var output = new StringWriter();
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(["usage", "--from", "2026-10-20T00:00:00Z", "--to", "2026-10-21T00:00:00Z", "--json"],
            new CliServices(false) { Knowledge = commands }, output, new StringWriter()));
        Assert.Contains("dailyTokens", output.ToString());
        Assert.Equal("/v1/usage", handler.Path); Assert.Contains("2026-10-20", handler.Query);
        Assert.Equal("Bearer", handler.Scheme);
    }
    [Fact]
    public async Task EnrichForwardsTheDocumentAndReturnsTheQueuedJob()
    {
        using var handler = new Handler();
        var commands = new HttpDaemonCommands(() => new Credentials(), (_, _) => throw new InvalidOperationException(), () => new HttpClient(handler, false));
        commands.Configure(() => throw new InvalidOperationException(), "http://daemon.test", "fixture");
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(["enrich", "doc 1", "--json"],
            new CliServices(false) { Knowledge = commands }, new StringWriter(), new StringWriter()));
        Assert.Equal("/v1/documents/doc%201/enrich", handler.Path);
        Assert.Equal("POST", handler.Method);
    }
    private sealed class Credentials : ICredentialStore
    {
        public Task StoreAsync(Uri origin, string name, string secret, CancellationToken token = default) => throw new NotSupportedException();
        public Task<string?> ReadAsync(Uri origin, string name, CancellationToken token = default) => Task.FromResult<string?>(name.EndsWith(".access", StringComparison.Ordinal) ? null : "fixture.credential");
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal string? Path, Query, Scheme, Method;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Method = request.Method.Method; Path = request.RequestUri!.AbsolutePath; Query = Uri.UnescapeDataString(request.RequestUri.Query); Scheme = request.Headers.Authorization?.Scheme;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { dailyTokens = 120, dailyLimit = 2000, rows = Array.Empty<object>() }) });
        }
    }
}
