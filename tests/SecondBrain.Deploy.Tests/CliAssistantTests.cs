using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using SecondBrain.Core.Chat;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliAssistantTests
{
    [Fact]
    public async Task RetryUsesTheExistingTurnAndStartsAfterItsResumeMarker()
    {
        using var handler = new AssistantHandler { FailFirstTurn = true };
        var output = new StringWriter();
        Assert.Equal(0, await Cli.Program.RunAsync(["chat"], Services(handler, "first\n/retry\n/exit\n"), output, new StringWriter()));
        Assert.Single(handler.Submissions);
        Assert.Equal(1, handler.Retries);
        Assert.Contains(handler.Paths, path => path.EndsWith("/events?after=2", StringComparison.Ordinal));
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Conversation = Ulid.NewUlid().ToString();
    private static readonly string TurnId = Ulid.NewUlid().ToString();
    private static ChatTurn Turn(string status = "complete", string? failure = null) => new(TurnId, Conversation, 1, status, "ask", "question",
        new("mock", "mock"), "2026-10-24T00:00:00Z", null, failure,
        status == "running" ? null : new("Blue Otter [1]", [new(new(1, "doc", 2, "generation", "chunk", 0, null, "Project", "Codename: Blue Otter", "hash"))]));

    [Theory]
    [InlineData("ask")]
    [InlineData("chat")]
    public async Task AssistantCommandsHaveHelp(string name)
    {
        var output = new StringWriter();
        Assert.Equal(0, await Cli.Program.RunAsync([name, "--help"], new CliServices(false), output, new StringWriter()));
        Assert.Contains(name, output.ToString());
    }

    [Fact]
    public async Task AskWaitsForDurableAnswerAndPreservesCitationsAndDeepRequest()
    {
        using var handler = new AssistantHandler();
        var output = new StringWriter();
        var exit = await Cli.Program.RunAsync(["ask", "project codename", "--deep", "--json"], Services(handler), output, new StringWriter());
        Assert.Equal(0, exit);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("Blue Otter [1]", result.RootElement.GetProperty("data").GetProperty("answer").GetProperty("text").GetString());
        Assert.Equal("Codename: Blue Otter", result.RootElement.GetProperty("data").GetProperty("answer").GetProperty("citations")[0].GetProperty("source").GetProperty("excerpt").GetString());
        Assert.Equal("ask", handler.Submissions.Single().Mode);
        Assert.True(handler.Submissions.Single().Deep);
        Assert.NotNull(handler.Submissions.Single().RequestId);
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer test-key", value));
    }

    [Fact]
    public async Task PartialAskHasNonzeroExitAndRetainsPartialAnswer()
    {
        using var handler = new AssistantHandler { Final = Turn("partial", "limit_reached") };
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(1, await Cli.Program.RunAsync(["ask", "question"], Services(handler), output, error));
        Assert.Contains("Blue Otter", output.ToString());
        Assert.Contains("Codename: Blue Otter", output.ToString());
        Assert.Contains("limit_reached", error.ToString());
    }

    [Fact]
    public async Task ChatReplaysWithoutRepeatingDeltasAndContinuesThenStartsNewConversation()
    {
        using var handler = new AssistantHandler { Reconnect = true };
        var output = new StringWriter();
        var exit = await Cli.Program.RunAsync(["chat"], Services(handler, "first\nfollow up\n/new\nanother\n/exit\n"), output, new StringWriter());
        Assert.Equal(0, exit);
        Assert.Equal(3, handler.Submissions.Count);
        Assert.Null(handler.Submissions[0].ConversationId);
        Assert.Equal(Conversation, handler.Submissions[1].ConversationId);
        Assert.Null(handler.Submissions[2].ConversationId);
        Assert.Equal(3, handler.Submissions.Select(item => item.RequestId).Distinct().Count());
        Assert.Contains("?after=1", handler.Paths.Single(path => path.EndsWith("?after=1", StringComparison.Ordinal)));
        Assert.DoesNotContain("Blue Blue", output.ToString());
        Assert.Contains("Blue Otter", output.ToString());
    }

    [Fact]
    public async Task ResumeObservesRunningTurnWithoutResubmittingIt()
    {
        using var handler = new AssistantHandler();
        var output = new StringWriter();
        Assert.Equal(0, await Cli.Program.RunAsync(["chat", Conversation], Services(handler, "/exit\n"), output, new StringWriter()));
        Assert.Empty(handler.Submissions);
        Assert.Contains("Blue Otter", output.ToString());
    }

    [Fact]
    public async Task StreamCannotWriteTerminalControlsAndJsonChatIsLineDelimited()
    {
        using var handler = new AssistantHandler { Delta = "\u001b]52;c;secret\u0007Blue Otter [1]" };
        var output = new StringWriter();
        Assert.Equal(0, await Cli.Program.RunAsync(["chat"], Services(handler, "first\n/exit\n"), output, new StringWriter()));
        Assert.DoesNotContain('\u001b', output.ToString());
        Assert.DoesNotContain('\u0007', output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Cli.Program.RunAsync(["chat", "--json"], Services(handler, "first\n/exit\n"), output, new StringWriter()));
        foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    [Theory]
    [InlineData("ask", " ")]
    [InlineData("chat", "https://elsewhere.invalid/")]
    public async Task InvalidInputIsRejectedBeforeAnyRequest(string command, string argument)
    {
        using var handler = new AssistantHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var result = await Cli.Program.RunAsync([command, argument], Services(handler), output, error);
        Assert.True(result == 2, $"Exit {result}; output: {output}; error: {error}; requests: {string.Join(',', handler.Paths)}");
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task UnauthorizedReplayDoesNotResubmitAcceptedTurn()
    {
        using var handler = new AssistantHandler { ReplayStatus = HttpStatusCode.Unauthorized };
        Assert.Equal(4, await Cli.Program.RunAsync(["ask", "question"], Services(handler), new StringWriter(), new StringWriter()));
        Assert.Single(handler.Submissions);
    }

    private static CliServices Services(AssistantHandler handler, string input = "")
    {
        var commands = new HttpDaemonCommands(() => new Credentials(), (_, _) => Task.FromResult("unused"), () => new HttpClient(handler, false));
        commands.Configure(() => throw new InvalidOperationException(), "http://daemon.test", "test");
        return new CliServices(false) { Assistant = commands, Input = new StringReader(input) };
    }

    private sealed class Credentials : ICredentialStore
    {
        public Task StoreAsync(Uri origin, string name, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(Uri origin, string name, CancellationToken cancellationToken = default) => Task.FromResult<string?>(name.EndsWith(".access", StringComparison.Ordinal) ? null : "test-key");
    }

    private sealed class AssistantHandler : HttpMessageHandler
    {
        public ChatTurn Final { get; init; } = Turn();
        public string Delta { get; init; } = "Blue Otter [1]";
        public bool Reconnect { get; init; }
        public HttpStatusCode ReplayStatus { get; init; } = HttpStatusCode.OK;
        public bool FailFirstTurn { get; init; }
        public int Retries { get; private set; }
        public List<ChatRequest> Submissions { get; } = [];
        public List<string> Paths { get; } = [];
        public List<string?> Authorization { get; } = [];
        private int replays;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Paths.Add(path);
            Authorization.Add(request.Headers.Authorization?.ToString());
            if (request.Method == HttpMethod.Post)
            {
                if (path.EndsWith("/retry", StringComparison.Ordinal))
                {
                    Retries++;
                    return Response(new ChatStart(Turn("running"), true, 2));
                }
                Submissions.Add((await request.Content!.ReadFromJsonAsync<ChatRequest>(Json, cancellationToken))!);
                return Response(new ChatStart(Turn("running"), true));
            }
            if (path.StartsWith("/v1/conversations/", StringComparison.Ordinal)) return Response(new[] { Turn("running") });
            if (path.StartsWith("/v1/pending", StringComparison.Ordinal)) return Response(Array.Empty<ChatProposal>());
            if (path.Contains("/events?", StringComparison.Ordinal))
            {
                if (ReplayStatus != HttpStatusCode.OK) return new(ReplayStatus);
                replays++;
                string Event(long sequence, string value) => "data: " + JsonSerializer.Serialize(new ChatEvent(sequence, "delta", JsonSerializer.SerializeToElement(new { text = value }), false, "now"), Json) + "\n\n";
                var data = Reconnect && replays == 1 ? Event(1, "Blue ")
                    : Reconnect && replays == 2 ? Event(1, "Blue ") + Event(2, "Otter [1]") : Event(Retries > 0 ? 3 : 1, Delta);
                return new(ReplayStatus) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
            }
            return Response(Reconnect && replays == 1 ? Turn("running") : FailFirstTurn && Retries == 0 ? Turn("partial", "provider_or_tool_failed") : Final);
        }
        private static HttpResponseMessage Response<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) };
    }
}
