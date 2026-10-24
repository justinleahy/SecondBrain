using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using SecondBrain.MockProvider;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class McpTests
{
    [Theory]
    [InlineData("2026-07-28")]
    [InlineData("2025-11-25")]
    public async Task BothProtocolGenerationsEnforceMethodScopes(string version)
    {
        await using var factory = new LaneDWebFactory();
        var writer = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Write });
        using var client = Client(factory, writer.Plaintext);
        var bootstrap = version == "2026-07-28"
            ? await Rpc(client, "server/discover", new { }, version)
            : await Rpc(client, "initialize", new { protocolVersion = version, clientInfo = new { name = "test", version = "1" }, capabilities = new { } }, version);
        Assert.True(bootstrap.TryGetProperty("result", out _), bootstrap.ToString());
        var list = await Rpc(client, "tools/list", new { }, version);
        Assert.Equal(8, list.GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.True((await Rpc(client, "resources/list", new { }, version)).TryGetProperty("error", out _));
        Assert.True((await Rpc(client, "resources/read", new { uri = "brain://document/missing" }, version)).TryGetProperty("error", out _));
        Assert.True((await Rpc(client, "prompts/get", new { name = "brain_daily_review" }, version)).TryGetProperty("error", out _));
        Assert.True((await Rpc(client, "subscriptions/listen", new { subscriptions = Array.Empty<object>() }, version)).TryGetProperty("error", out _));
        Assert.True((await Rpc(client, "tools/call", new { name = "brain_search", arguments = new { query = "secret" } }, version)).TryGetProperty("error", out _));
        Assert.True((await Rpc(client, "tools/call", new { name = "brain_ask", arguments = new { question = "secret" } }, version)).TryGetProperty("error", out _));
        Assert.True((await Rpc(client, "resources/templates/list", new { }, version)).TryGetProperty("result", out _));
        Assert.True((await Rpc(client, "prompts/list", new { }, version)).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task WriteOnlyCaptureAndUpdateUseCredentialProvenanceAndInteractiveApproval()
    {
        await using var factory = new LaneDWebFactory();
        var writer = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Write });
        using var client = Client(factory, writer.Plaintext);
        var captured = await Rpc(client, "tools/call", new { name = "brain_remember", arguments = new { content = "Original body", title = "MCP note", tags = new[] { "mcp" } } });
        Assert.True(captured.TryGetProperty("result", out _), captured.ToString());
        var doc = captured.GetProperty("result").GetProperty("structuredContent").GetProperty("document");
        var id = doc.GetProperty("id").GetString()!;
        Assert.StartsWith("mcp:", doc.GetProperty("provenance").GetString());
        Assert.Equal("mcp:" + writer.Record.Name, doc.GetProperty("provenance").GetString());
        var proposed = await Rpc(client, "tools/call", new { name = "brain_update", arguments = new { id, revision = 1, content = "Updated body" } });
        Assert.True(proposed.TryGetProperty("result", out _), proposed.ToString());
        var pending = proposed.GetProperty("result").GetProperty("structuredContent").GetProperty("pending_id").GetString()!;
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        Assert.Equal("Original body", (await documents.GetAsync(id, true))!.Text);
        await factory.SeedAccountAsync();
        using var browser = factory.CreatePrivateClient();
        browser.DefaultRequestHeaders.Add("Origin", "http://private.test");
        await browser.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        var csrf = (await browser.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        browser.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        var visible = (await browser.GetFromJsonAsync<ChatProposal[]>("/pending"))!;
        Assert.Contains(visible, item => item.Id == pending);
        using var approved = await browser.PostAsJsonAsync("/pending/" + pending + "/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("Updated body", (await documents.GetAsync(id, true))!.Text);
        var stale = await Rpc(client, "tools/call", new { name = "brain_update", arguments = new { id, revision = 1, content = "stale" } });
        Assert.True(stale.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(2, stale.GetProperty("result").GetProperty("structuredContent").GetProperty("current_revision").GetInt64());
    }

    [Fact]
    public async Task ReadToolsResourcesAndSchemasWorkWithStrictInputs()
    {
        await using var factory = new LaneDWebFactory();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write });
        using var client = Client(factory, key.Plaintext);
        var captured = await Rpc(client, "tools/call", new { name = "brain_remember", arguments = new { content = "Orchard pear knowledge", title = "Orchard", tags = new[] { "fruit" } } });
        var doc = captured.GetProperty("result").GetProperty("structuredContent").GetProperty("document");
        var id = doc.GetProperty("id").GetString()!;
        var search = await Rpc(client, "tools/call", new { name = "brain_search", arguments = new { query = "orchard", mode = "keyword" } });
        Assert.NotEmpty(search.GetProperty("result").GetProperty("structuredContent").GetProperty("results").EnumerateArray());
        var read = await Rpc(client, "tools/call", new { name = "brain_get", arguments = new { id, offset = 0, length = 7 } });
        Assert.Equal("Orchard", read.GetProperty("result").GetProperty("structuredContent").GetProperty("text").GetString());
        Assert.True(read.GetProperty("result").GetProperty("structuredContent").GetProperty("truncated").GetBoolean());
        var tags = await Rpc(client, "tools/call", new { name = "brain_tags", arguments = new { } });
        Assert.Equal("fruit", tags.GetProperty("result").GetProperty("structuredContent").GetProperty("tags")[0].GetProperty("tag").GetString());
        foreach (var name in new[] { "brain_query", "brain_list_recent" })
        {
            var listing = await Rpc(client, "tools/call", new { name, arguments = new { type = "note", limit = 1 } });
            Assert.Single(listing.GetProperty("result").GetProperty("structuredContent").GetProperty("records").EnumerateArray());
        }
        var resources = await Rpc(client, "resources/list", new { });
        var uri = resources.GetProperty("result").GetProperty("resources")[0].GetProperty("uri").GetString();
        var resource = await Rpc(client, "resources/read", new { uri });
        Assert.Equal("Orchard pear knowledge", resource.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString());
        var relativePath = Path.GetRelativePath(Path.Combine(factory.Options.CurrentValue.DataRoot, "vault"), doc.GetProperty("path").GetString()!);
        var byPath = await Rpc(client, "resources/read", new { uri = "brain://note/" + Uri.EscapeDataString(relativePath) });
        Assert.Equal("Orchard pear knowledge", byPath.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString());
        var escaped = await Rpc(client, "resources/read", new { uri = "brain://note/..%2F..%2Fsecret" });
        Assert.True(escaped.TryGetProperty("error", out _));
        var prompt = await Rpc(client, "prompts/get", new { name = "brain_topic_brief", arguments = new { topic = "fruit" } });
        Assert.NotEmpty(prompt.GetProperty("result").GetProperty("messages").EnumerateArray());
        var invalid = await Rpc(client, "tools/call", new { name = "brain_get", arguments = new { id, admin = true } });
        Assert.Equal(-32602, invalid.GetProperty("error").GetProperty("code").GetInt32());
        var list = await Rpc(client, "tools/list", new { });
        foreach (var tool in list.GetProperty("result").GetProperty("tools").EnumerateArray())
            Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task OfficialClientNegotiatesDiscoveryAndInvokesTools()
    {
        await using var factory = new LaneDWebFactory();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        using var http = Client(factory, key.Plaintext);
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("http://private.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
        await using var client = await McpClient.CreateAsync(transport);
        Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
        var tools = await client.ListToolsAsync();
        Assert.Equal(8, tools.Count);
        var tags = await client.CallToolAsync("brain_tags", new Dictionary<string, object?>());
        Assert.False(tags.IsError ?? false);
    }

    [Fact]
    public async Task AskReturnsVerifiedCitationThroughTheSameBoundedExecutor()
    {
        await using var factory = new LaneDWebFactory(options => options.Models.Chat!.Capabilities = options.Models.Chat.Capabilities with { Streaming = false });
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer });
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = context => context.Response.WriteAsJsonAsync(new
        {
            id = "mcp-answer", @object = "chat.completion", model = "mock-chat", created = 1,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "The codename is Blue Otter [1]." }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 64, completion_tokens = 16, total_tokens = 80 }
        });
        using var client = Client(factory, key.Plaintext);
        var captured = await Rpc(client, "tools/call", new { name = "brain_remember", arguments = new { content = "The project codename is Blue Otter.", title = "Project", tags = new[] { "chosen" } } });
        var id = captured.GetProperty("result").GetProperty("structuredContent").GetProperty("document").GetProperty("id").GetString();
        var response = await Rpc(client, "tools/call", new { name = "brain_ask", arguments = new { question = "project codename", type = "note", tags = new[] { "chosen" } } });
        var turn = response.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("complete", turn.GetProperty("status").GetString());
        Assert.Equal("ask", turn.GetProperty("mode").GetString());
        Assert.Equal(id, turn.GetProperty("answer").GetProperty("citations")[0].GetProperty("source").GetProperty("documentId").GetString());
    }

    [Fact]
    public async Task ResourcePaginationIsSignedAndBoundToTheCredential()
    {
        await using var factory = new LaneDWebFactory();
        var owner = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write });
        var other = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        var repository = factory.Services.GetRequiredService<IDocumentRepository>();
        var authority = new IngestionAuthority(owner.Record.Id, owner.Record.Generation, owner.Record.AccountEpoch, owner.Record.Name, "api_key", "mcp");
        for (var index = 0; index < 51; index++) await repository.CaptureAsync(new("Pagination record " + index, "Page " + index), authority);
        using var client = Client(factory, owner.Plaintext);
        var first = (await Rpc(client, "resources/list", new { })).GetProperty("result");
        Assert.Equal(50, first.GetProperty("resources").GetArrayLength());
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = (await Rpc(client, "resources/list", new { cursor })).GetProperty("result");
        Assert.Single(second.GetProperty("resources").EnumerateArray());
        Assert.Empty(first.GetProperty("resources").EnumerateArray().Select(item => item.GetProperty("uri").GetString())
            .Intersect(second.GetProperty("resources").EnumerateArray().Select(item => item.GetProperty("uri").GetString())));
        client.DefaultRequestHeaders.Authorization = new("Bearer", other.Plaintext);
        Assert.True((await Rpc(client, "resources/list", new { cursor })).TryGetProperty("error", out _));
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Plaintext);
        Assert.True((await Rpc(client, "resources/list", new { cursor = cursor + "tampered" })).TryGetProperty("error", out _));
    }

    [Fact]
    public async Task SearchAndTagCursorsRejectTamperingAndOtherCredentials()
    {
        await using var factory = new LaneDWebFactory();
        var owner = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write });
        var other = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        var repository = factory.Services.GetRequiredService<IDocumentRepository>();
        var authority = new IngestionAuthority(owner.Record.Id, owner.Record.Generation, owner.Record.AccountEpoch, owner.Record.Name, "api_key");
        for (var index = 0; index < 5; index++) await repository.CaptureAsync(new("Shared orchard text " + index, "Orchard " + index, Tags: ["fruit" + index]), authority);
        using var client = Client(factory, owner.Plaintext);
        var first = (await Rpc(client, "tools/call", new { name = "brain_search", arguments = new { query = "orchard", mode = "keyword", limit = 2 } }))
            .GetProperty("result").GetProperty("structuredContent");
        var cursor = first.GetProperty("next_cursor").GetString()!;
        var second = (await Rpc(client, "tools/call", new { name = "brain_search", arguments = new { query = "orchard", mode = "keyword", limit = 2, cursor } }))
            .GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(2, second.GetProperty("results").GetArrayLength());
        Assert.Empty(first.GetProperty("results").EnumerateArray().Select(hit => hit.GetProperty("chunk_id").GetString())
            .Intersect(second.GetProperty("results").EnumerateArray().Select(hit => hit.GetProperty("chunk_id").GetString())));
        client.DefaultRequestHeaders.Authorization = new("Bearer", other.Plaintext);
        Assert.True((await Rpc(client, "tools/call", new { name = "brain_search", arguments = new { query = "orchard", mode = "keyword", limit = 2, cursor } })).TryGetProperty("error", out _));
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Plaintext);
        Assert.True((await Rpc(client, "tools/call", new { name = "brain_search", arguments = new { query = "orchard", mode = "keyword", limit = 2, cursor = cursor + "bad" } })).TryGetProperty("error", out _));
        var tags = (await Rpc(client, "tools/call", new { name = "brain_tags", arguments = new { limit = 2 } })).GetProperty("result").GetProperty("structuredContent");
        var tagsCursor = tags.GetProperty("next_cursor").GetString()!;
        var nextTags = (await Rpc(client, "tools/call", new { name = "brain_tags", arguments = new { limit = 2, cursor = tagsCursor } })).GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(2, nextTags.GetProperty("tags").GetArrayLength());
        client.DefaultRequestHeaders.Authorization = new("Bearer", other.Plaintext);
        Assert.True((await Rpc(client, "tools/call", new { name = "brain_tags", arguments = new { limit = 2, cursor = tagsCursor } })).TryGetProperty("error", out _));
    }

    [Fact]
    public async Task OnlyApiKeysCanUseMcpAndOriginsAreChecked()
    {
        await using var factory = new LaneDWebFactory();
        using var anonymous = factory.CreatePrivateClient();
        anonymous.DefaultRequestHeaders.Add("Origin", "http://private.test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/mcp", new { })).StatusCode);
        await factory.SeedAccountAsync();
        await anonymous.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        var csrf = (await anonymous.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        anonymous.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("/mcp", new { })).StatusCode);
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        using var client = Client(factory, key.Plaintext);
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/mcp", new { })).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/mcp")).StatusCode);
    }

    private static HttpClient Client(LaneDWebFactory factory, string key)
    {
        var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        return client;
    }

    private static async Task<JsonElement> Rpc(HttpClient client, string method, object parameters, string version = "2026-07-28")
    {
        var values = JsonSerializer.SerializeToNode(parameters)!.AsObject();
        if (version == "2026-07-28") values["_meta"] = JsonSerializer.SerializeToNode(new Dictionary<string, object>
        { [MetaKeys.ProtocolVersion] = version, [MetaKeys.ClientCapabilities] = new { }, [MetaKeys.ClientInfo] = new { name = "protocol-test", version = "1" } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = values }) };
        request.Headers.Add("MCP-Protocol-Version", version);
        if (version == "2026-07-28")
        {
            request.Headers.Add("Mcp-Method", McpHeaderEncoder.EncodeValue(method));
            var member = method switch { "tools/call" or "prompts/get" => "name", "resources/read" => "uri", _ => null };
            if (member is not null) request.Headers.Add("Mcp-Name", McpHeaderEncoder.EncodeValue(values[member]!.GetValue<string>()));
        }
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode < 500, $"{response.StatusCode}: {text}");
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = text.Split('\n').Last(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        using var document = JsonDocument.Parse(text);
        if (!response.IsSuccessStatusCode) Assert.True(document.RootElement.TryGetProperty("error", out _), $"{response.StatusCode}: {text}");
        return document.RootElement.Clone();
    }
}
