using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliIngestTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("search")]
    [InlineData("query")]
    public async Task CommandsAreRegistered(string command)
    {
        var output = new StringWriter();
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync([command, "--help"], new CliServices(false), output, new StringWriter()));
        Assert.Contains(command, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandardInputCaptureRetainsDurableIdsAndProjectionStatus()
    {
        using var handler = new RecordingHandler("""{"document":{"id":"doc","status":"indexed_partial"},"job":{"id":"job"},"keyword":"live","vector":"pending"}""");
        var commands = Commands(handler);
        var output = new StringWriter();
        var services = new CliServices(false) { Knowledge = commands, Input = new StringReader("captured body") };
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(["add", "-", "--title", "Title", "--tags", "one,two", "--props", "{\"author\":\"Me\"}", "--json"], services, output, new StringWriter()));
        Assert.Equal("/v1/documents", handler.Path);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal("captured body", sent.RootElement.GetProperty("content").GetString());
        Assert.Equal("Title", sent.RootElement.GetProperty("title").GetString());
        using var envelope = JsonDocument.Parse(output.ToString());
        Assert.Equal("doc", envelope.RootElement.GetProperty("data").GetProperty("document").GetProperty("id").GetString());
        Assert.Equal("job", envelope.RootElement.GetProperty("data").GetProperty("job").GetProperty("id").GetString());
        Assert.Equal("pending", envelope.RootElement.GetProperty("data").GetProperty("vector").GetString());
        Assert.Equal("Bearer test-key", handler.Authorization);
    }

    [Fact]
    public async Task FileCaptureStreamsMultipartWithWaitAndIdempotency()
    {
        var path = Path.Combine(Path.GetTempPath(), "cli-upload-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, "file body");
            using var handler = new RecordingHandler("{\"document\":{\"id\":\"doc\"},\"job\":{\"id\":\"job\"}}");
            await Commands(handler).AddAsync(path, new CaptureRequest("", Title: "upload"), 5, "capture-once", TextReader.Null, default);
            Assert.Equal("/v1/documents?wait=5", handler.Path);
            Assert.StartsWith("multipart/form-data", handler.ContentType);
            Assert.Contains("file body", handler.Body, StringComparison.Ordinal);
            Assert.Contains("name=file", handler.Body, StringComparison.Ordinal);
            Assert.Contains("name=metadata", handler.Body, StringComparison.Ordinal);
            Assert.Equal("capture-once", handler.Idempotency);
            Assert.True(handler.Length > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SearchReturnsExplicitDegradation()
    {
        using var handler = new RecordingHandler("{\"results\":[],\"degraded\":[\"embed-unavailable\"]}");
        var result = await Commands(handler).SearchAsync(new SearchRequest("topic", Filters: new RetrievalFilters(Type: "note")), default);
        Assert.Equal("/v1/search", handler.Path);
        Assert.Equal("embed-unavailable", ((JsonElement)result.Data!).GetProperty("degraded")[0].GetString());
    }

    [Fact]
    public async Task QueryBindsPredicatesAndContinuation()
    {
        using var handler = new RecordingHandler("{\"records\":[],\"next_cursor\":\"next\",\"truncated\":false,\"coverage\":null}");
        var output = new StringWriter();
        Assert.Equal(0, await SecondBrain.Cli.Program.RunAsync(["query", "--type", "note", "--where", "title=Hello", "--sort", "title", "--limit", "2", "--cursor", "opaque", "--json"],
            new CliServices(false) { Knowledge = Commands(handler) }, output, new StringWriter()));
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal("eq", sent.RootElement.GetProperty("predicates")[0].GetProperty("op").GetString());
        Assert.Equal("Hello", sent.RootElement.GetProperty("predicates")[0].GetProperty("value").GetString());
        Assert.Equal("opaque", sent.RootElement.GetProperty("cursor").GetString());
        Assert.Contains("next_cursor", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, 2)]
    [InlineData(401, 4)]
    [InlineData(403, 4)]
    [InlineData(413, 4)]
    [InlineData(422, 4)]
    [InlineData(429, 4)]
    [InlineData(503, 4)]
    [InlineData(500, 1)]
    public async Task DaemonFailuresKeepExitContract(int status, int exit)
    {
        using var handler = new RecordingHandler("{}", (HttpStatusCode)status);
        var output = new StringWriter();
        Assert.Equal(exit, await SecondBrain.Cli.Program.RunAsync(["search", "topic", "--json"], new CliServices(false) { Knowledge = Commands(handler) }, output, new StringWriter()));
        using var envelope = JsonDocument.Parse(output.ToString());
        Assert.Equal(exit, envelope.RootElement.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("test-key", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search", "topic", "--mode", "invalid")]
    [InlineData("query", "--type", "calendar_event", "--limit", "1")]
    [InlineData("query", "--where", "bad", "--limit", "1")]
    [InlineData("add", "-", "--props", "[]")]
    public async Task InvalidInputsAreUsageFailures(params string[] args)
    {
        using var handler = new RecordingHandler("{}");
        Assert.Equal(2, await SecondBrain.Cli.Program.RunAsync(args.Concat(["--json"]).ToArray(), new CliServices(false) { Knowledge = Commands(handler) }, new StringWriter(), new StringWriter()));
        Assert.Null(handler.Path);
    }

    private static HttpDaemonCommands Commands(RecordingHandler handler)
    {
        var commands = new HttpDaemonCommands(() => new TestCredentials(), (_, _) => Task.FromResult("unused"), () => new HttpClient(handler, disposeHandler: false));
        commands.Configure(() => throw new InvalidOperationException(), "http://daemon.test", "test");
        return commands;
    }
    private sealed class TestCredentials : ICredentialStore
    {
        public Task StoreAsync(Uri origin, string name, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(Uri origin, string name, CancellationToken cancellationToken = default) => Task.FromResult<string?>(name.EndsWith(".access", StringComparison.Ordinal) ? null : "test-key");
    }
    private sealed class RecordingHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public string? Authorization { get; private set; }
        public string? Idempotency { get; private set; }
        public long? Length { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.PathAndQuery;
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content?.Headers.ContentType?.ToString();
            Length = request.Content?.Headers.ContentLength;
            Idempotency = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = JsonContent.Create(JsonSerializer.Deserialize<JsonElement>(response)) };
        }
    }
}
