using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Chat;

namespace SecondBrain.Cli.Commands;

public sealed partial class HttpDaemonCommands : IAssistantCommands
{
    private const int MaximumPromptCharacters = 16_000;
    private const int MaximumEventCharacters = 2_000_000;

    public async Task<CliResult> AskAsync(string question, bool deep, CancellationToken cancellationToken)
    {
        ValidatePrompt(question);
        using var client = await OpenAsync(apiKey: true, cancellationToken, interactive: true);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(6));
        var start = await StartTurnAsync(client, new(question, Mode: "ask", RequestId: Guid.NewGuid().ToString("N"), Deep: deep), deadline.Token);
        var turn = await ObserveTurnAsync(client, start.Turn, null, deadline.Token);
        return TurnResult(turn);
    }

    public async Task<CliResult> ChatAsync(string? conversationId, TextReader input, TextWriter output, TextWriter error,
        bool json, CancellationToken cancellationToken)
    {
        if (conversationId is not null) ValidateId(conversationId);
        string? lastTurnId = null;
        using var client = await OpenAsync(apiKey: true, cancellationToken, interactive: true);
        client.Timeout = Timeout.InfiniteTimeSpan;
        if (!json) await error.WriteLineAsync("/help lists commands; /new starts a conversation; /exit quits. Answers stream provisionally until verified.");
        if (conversationId is not null)
        {
            using var response = await SendAsync(client, HttpMethod.Get, "/v1/conversations/" + Uri.EscapeDataString(conversationId), null, cancellationToken);
            var history = await response.Content.ReadFromJsonAsync<ChatTurn[]>(Json, cancellationToken) ?? throw new JsonException();
            foreach (var previous in history)
            {
                lastTurnId = previous.Id;
                if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { kind = "history", turn = previous }, Json));
                else
                {
                    await output.WriteLineAsync("You: " + AssistantOutput.Text(previous.Prompt));
                    if (previous.Status != "running") await AssistantOutput.WriteTurnAsync(previous, output, error);
                }
                if (previous.Status == "running") await DisplayTurnAsync(previous);
            }
        }
        while (true)
        {
            if (!json) { await output.WriteAsync("You: "); await output.FlushAsync(cancellationToken); }
            var prompt = await ReadBoundedLineAsync(input, MaximumPromptCharacters, cancellationToken);
            if (prompt is null || prompt.Trim() is "/exit" or "/quit") break;
            if (string.IsNullOrWhiteSpace(prompt)) continue;
            if (prompt.Trim() == "/new") { conversationId = null; lastTurnId = null; continue; }
            if (prompt.Trim() is "/retry" or "/retry --fallback")
            {
                if (lastTurnId is null) { await error.WriteLineAsync("There is no turn to retry."); continue; }
                try
                {
                    using var retryResponse = await SendAsync(client, HttpMethod.Post, "/v1/turns/" + Uri.EscapeDataString(lastTurnId) + "/retry",
                        new ChatRetryRequest(Guid.NewGuid().ToString("N"), prompt.Contains("--fallback", StringComparison.Ordinal)), cancellationToken);
                    var retried = await retryResponse.Content.ReadFromJsonAsync<ChatStart>(Json, cancellationToken) ?? throw new JsonException();
                    await DisplayTurnAsync(retried.Turn, cancellationToken, retried.ReplayAfter);
                }
                catch (DaemonCommandException rejected) { await CliOutput.WriteAsync(rejected.Result, json, output, error); }
                continue;
            }
            if (prompt.Trim() == "/pending" || prompt.StartsWith("/approve ", StringComparison.Ordinal) || prompt.StartsWith("/discard ", StringComparison.Ordinal))
            {
                var parts = prompt.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var operation = parts[0] == "/pending" ? "list" : parts[0][1..];
                try { await CliOutput.WriteAsync(await PendingAsync(operation, parts.Length == 2 ? parts[1] : null, cancellationToken), json, output, error); }
                catch (DaemonCommandException rejected) { await CliOutput.WriteAsync(rejected.Result, json, output, error); }
                continue;
            }
            if (prompt.Trim() == "/help")
            {
                await error.WriteLineAsync("/save [text], /new, /search <query>, /query <type> [JSON filters], /recent [count], /sources, /retry [--fallback], /pending, /approve ID, /discard ID, /exit. Enter a message to continue the current conversation.");
                continue;
            }
            ValidatePrompt(prompt);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(6));
            var start = await StartTurnAsync(client, new(prompt, conversationId, RequestId: Guid.NewGuid().ToString("N")), deadline.Token);
            conversationId = start.Turn.ConversationId;
            lastTurnId = start.Turn.Id;
            if (!json) await error.WriteLineAsync("Conversation: " + conversationId);
            await DisplayTurnAsync(start.Turn, deadline.Token);
            using var pendingResponse = await SendAsync(client, HttpMethod.Get, "/v1/pending?conversationId=" + Uri.EscapeDataString(conversationId), null, deadline.Token);
            var pending = (await pendingResponse.Content.ReadFromJsonAsync<ChatProposal[]>(Json, deadline.Token) ?? [])
                .Where(proposal => proposal.Status == "pending").ToArray();
            if (pending.Length > 0)
            {
                if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { kind = "pending", proposals = pending }, Json));
                else
                {
                    foreach (var proposal in pending) await output.WriteLineAsync(JsonSerializer.Serialize(proposal, new JsonSerializerOptions(Json) { WriteIndented = true }));
                    await error.WriteLineAsync("Review each exact proposal above. /approve ID applies it; /discard ID dismisses it. Both require browser pairing.");
                }
            }
        }
        return CliResult.Ok("Chat closed.", new { conversationId });

        async Task DisplayTurnAsync(ChatTurn initial, CancellationToken token = default, long after = 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(6));
            var streamed = new StringBuilder();
            var turn = await ObserveTurnAsync(client, initial, async item =>
            {
                if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { kind = "event", turnId = initial.Id, item }, Json));
                else if (!item.Redacted && item.Kind == "delta" && item.Data is { } data && data.TryGetProperty("text", out var text))
                {
                    var delta = text.GetString() ?? "";
                    if (streamed.Length + delta.Length > MaximumEventCharacters) throw new JsonException("Streamed answer is too large.");
                    streamed.Append(delta);
                    await output.WriteAsync(AssistantOutput.Text(delta));
                    await output.FlushAsync(deadline.Token);
                }
            }, deadline.Token, after);
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { kind = "turn", turn }, Json));
            else
            {
                if (streamed.Length != 0) await output.WriteLineAsync();
                await AssistantOutput.WriteTurnAsync(turn, output, error, streamed.Length == 0 ? null : streamed.ToString());
                await error.WriteLineAsync("Resume with: brain chat " + turn.ConversationId);
            }
        }
    }

    private static CliResult TurnResult(ChatTurn turn) => new(turn.Status == "complete" ? CliExitCode.Ok : CliExitCode.Error,
        turn.Status == "complete" ? "ok" : "turn-" + turn.Status, "Turn " + turn.Status + ".", turn);

    private static async Task<ChatStart> StartTurnAsync(HttpClient client, ChatRequest request, CancellationToken token)
    {
        using var response = await SendAsync(client, HttpMethod.Post, request.Mode == "ask" ? "/v1/ask" : "/v1/chat", request, token);
        return await response.Content.ReadFromJsonAsync<ChatStart>(Json, token) ?? throw new JsonException();
    }

    private static async Task<ChatTurn> ObserveTurnAsync(HttpClient client, ChatTurn initial, Func<ChatEvent, Task>? observe, CancellationToken token, long after = 0)
    {
        ValidateId(initial.Id);
        var turn = initial;
        var failures = 0;
        // Only reconnect the observer. Never resubmit an accepted turn after a lost connection.
        while (turn.Status == "running")
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/turns/" + Uri.EscapeDataString(turn.Id) + "/events?after=" + after.ToString(CultureInfo.InvariantCulture));
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode) ThrowResponseFailure(response);
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new JsonException("Expected an event stream.");
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var data = new StringBuilder();
                while (await ReadBoundedLineAsync(reader, MaximumEventCharacters, token) is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (data.Length == 0) continue;
                        var item = JsonSerializer.Deserialize<ChatEvent>(data.ToString(), Json) ?? throw new JsonException();
                        data.Clear();
                        if (item.Sequence <= after) continue;
                        if (observe is not null) await observe(item);
                        after = item.Sequence;
                        failures = 0;
                    }
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length + line.Length > MaximumEventCharacters) throw new JsonException("Event is too large.");
                        if (data.Length != 0) data.Append('\n');
                        data.Append(line.AsSpan(5).TrimStart());
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                if (++failures > 3) throw new DaemonUnreachableException("Event replay was interrupted. Resume with brain chat " + initial.ConversationId + ".");
            }
            using var current = await SendAsync(client, HttpMethod.Get, "/v1/turns/" + Uri.EscapeDataString(turn.Id), null, token);
            turn = await current.Content.ReadFromJsonAsync<ChatTurn>(Json, token) ?? throw new JsonException();
            if (turn.Status == "running") await Task.Delay(TimeSpan.FromMilliseconds(300), token);
        }
        return turn;
    }

    private static void ValidatePrompt(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumPromptCharacters)
            throw new CliUsageException("A question must contain 1–16000 characters.");
    }
    private static void ValidateId(string value)
    {
        if (value.Length != 26 || value[0] > '7' || value.Any(character => !"0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(char.ToUpperInvariant(character))) || !Ulid.TryParse(value, out _))
            throw new CliUsageException("A conversation or turn ID must be a ULID.");
    }

    internal static async Task<string?> ReadBoundedLineAsync(TextReader input, int maximum, CancellationToken token)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (await input.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length == maximum) throw new CliUsageException("Input exceeds the line length limit.");
            line.Append(buffer[0]);
        }
        return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
    }

    private static void ThrowResponseFailure(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var exit = status == 400 ? CliExitCode.Usage : status is >= 400 and < 500 || status == 503 ? CliExitCode.PreconditionFailed : CliExitCode.Error;
        throw new DaemonCommandException(new(exit, "daemon-rejected", $"The daemon rejected the request (HTTP {status})."));
    }
}
