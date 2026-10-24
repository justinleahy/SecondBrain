using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class McpBridgeTests
{
    [Fact]
    public async Task ForwardsRpcVerbatimOnlyToConfiguredOriginWithOwnedCredentials()
    {
        using var handler = new BridgeHandler([0]);
        var store = new Store();
        store.Values["bridge.access"] = """{"clientId":"operator-id","secret":"operator-secret"}""";
        const string request = """{"jsonrpc":"2.0","id":17,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","Authorization":"Bearer injected","destination":"https://evil.invalid/","headers":{"Host":"evil.invalid"}}}}""";
        var output = new StringWriter();
        var error = new StringWriter();
        var services = Services(handler, store, request + "\n");
        Assert.Equal(0, await Cli.Program.RunAsync(["mcp", "--json"], services, output, error));
        Assert.Equal(request, handler.Forwarded);
        Assert.All(handler.Requests, item => Assert.StartsWith("http://daemon.test/", item));
        Assert.Equal("Bearer dedicated-key", handler.Authorization);
        Assert.Equal("operator-id", handler.AccessId);
        Assert.Equal("operator-secret", handler.AccessSecret);
        Assert.Equal("2026-07-28", handler.Version);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal(17, response.RootElement.GetProperty("id").GetInt32());
        Assert.Empty(error.ToString());
        Assert.DoesNotContain("operator-secret", output.ToString());
        Assert.DoesNotContain("stopped", output.ToString());
    }

    [Theory]
    [InlineData(new[] { 0, 1 }, null, 4)]
    [InlineData(new[] { 0, 1 }, "capture", 0)]
    [InlineData(new[] { 0, 3 }, "capture", 4)]
    public async Task DefaultIsReadOnlyAndAdministratorKeysAreAlwaysRejected(int[] scopes, string? name, int expected)
    {
        using var handler = new BridgeHandler(scopes);
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(expected, await Cli.Program.RunAsync(name is null ? ["mcp"] : ["mcp", "--credential", name], Services(handler, new(), ""), output, error));
        Assert.Empty(output.ToString());
        Assert.Null(handler.Forwarded);
    }

    [Fact]
    public async Task NotificationsHaveNoReplyAndLegacyVersionIsForwarded()
    {
        using var handler = new BridgeHandler([0]);
        const string input = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","clientInfo":{"name":"test","version":"1"},"capabilities":{}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """;
        var output = new StringWriter();
        Assert.Equal(0, await Cli.Program.RunAsync(["mcp"], Services(handler, new(), input), output, new StringWriter()));
        Assert.Equal("2025-11-25", handler.Version);
        Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task AccessLoginStoresOnlyAnOriginBoundSecretAndDoesNotPrintIt()
    {
        var store = new Store();
        var output = new StringWriter();
        var services = new CliServices(false) { CredentialStore = store };
        Assert.Equal(0, await Cli.Program.RunAsync(["login", "https://public.test", "--name", "bridge", "--access-client-id", "public-id", "--access-client-secret", "private-secret", "--json"], services, output, new StringWriter()));
        Assert.DoesNotContain("private-secret", output.ToString());
        Assert.Contains("private-secret", store.Values["bridge.access"]);
        Assert.Equal("https://public.test", store.StoredOrigin);
    }

    private static CliServices Services(BridgeHandler handler, Store store, string input)
    {
        var commands = new HttpDaemonCommands(() => store, (_, _) => Task.FromResult("unused"), () => new HttpClient(handler, false));
        commands.Configure(() => throw new InvalidOperationException(), "http://daemon.test", "bridge");
        return new CliServices(false) { Mcp = commands, Input = new StringReader(input) };
    }
    private sealed class Store : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? StoredOrigin { get; private set; }
        public Task StoreAsync(Uri origin, string name, string key, CancellationToken token)
        { Values[name] = key; StoredOrigin = origin.GetLeftPart(UriPartial.Authority); return Task.CompletedTask; }
        public Task<string?> ReadAsync(Uri origin, string name, CancellationToken token) => Task.FromResult<string?>(Values.GetValueOrDefault(name) ?? (name.EndsWith(".access", StringComparison.Ordinal) ? null : "dedicated-key"));
    }
    private sealed class BridgeHandler(int[] scopes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? Forwarded { get; private set; }
        public string? Authorization { get; private set; }
        public string? AccessId { get; private set; }
        public string? AccessSecret { get; private set; }
        public string? Version { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            Authorization = request.Headers.Authorization?.ToString();
            AccessId = request.Headers.TryGetValues("Cf-Access-Client-Id", out var ids) ? ids.Single() : null;
            AccessSecret = request.Headers.TryGetValues("Cf-Access-Client-Secret", out var secrets) ? secrets.Single() : null;
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { kind = "api_key", scopes }) };
            Forwarded = await request.Content!.ReadAsStringAsync(cancellationToken);
            Version = request.Headers.GetValues("MCP-Protocol-Version").Single();
            using var document = JsonDocument.Parse(Forwarded);
            if (!document.RootElement.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { jsonrpc = "2.0", id = id.Clone(), result = new { tools = Array.Empty<object>() } }) };
        }
    }
}
