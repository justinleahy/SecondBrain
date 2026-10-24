using System.Net.Http.Json;
using System.Text.Json;

namespace SecondBrain.Cli.Commands;

public sealed partial class HttpDaemonCommands
{
    public async Task<LoginCredential> PairAsync(Uri origin, string name, TextWriter instructions, CancellationToken cancellationToken)
    {
        ValidateOrigin(origin);
        using var client = NewClient(origin);
        await AddAccessHeadersAsync(client, origin, name, cancellationToken);
        using var start = await SendAsync(client, HttpMethod.Post, "/v1/auth/cli/start", new { name }, cancellationToken);
        var request = await ReadJsonAsync(start, cancellationToken);
        var code = request.GetProperty("code").GetString()!;
        if (code.Length != 12 || code.Any(character => !Uri.IsHexDigit(character))) throw new JsonException("Invalid pairing code.");
        await instructions.WriteLineAsync("Open " + new Uri(origin, "/pair?code=" + code).AbsoluteUri);
        await instructions.WriteLineAsync("Confirm code " + code + " in your signed-in browser. This request expires in five minutes.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            using var response = await SendAsync(client, HttpMethod.Post, "/v1/auth/cli/poll",
                new { id = request.GetProperty("id").GetString(), pollSecret = request.GetProperty("pollSecret").GetString() }, deadline.Token);
            var state = await ReadJsonAsync(response, deadline.Token);
            if (state.GetProperty("status").GetString() == "pending") continue;
            var credential = state.GetProperty("credential").GetString()!;
            if (!credential.StartsWith("sbcli_", StringComparison.Ordinal)) throw new JsonException("Expected a CLI session.");
            return new(credential, state.GetProperty("credentialId").GetString());
        } while (true);
    }

    public async Task<CliResult> PendingAsync(string operation, string? id, CancellationToken cancellationToken)
    {
        if (operation is not ("list" or "approve" or "discard")) throw new CliUsageException("Unknown pending operation.");
        if (operation != "list") ValidateId(id ?? "");
        using var client = await OpenAsync(apiKey: true, cancellationToken, interactive: true);
        if (operation != "list" && client.DefaultRequestHeaders.Authorization?.Parameter?.StartsWith("sbcli_", StringComparison.Ordinal) != true)
            throw new CliPreconditionException("Pair through the browser first: brain login <url> --interactive --name <credential-name>.");
        using var response = await SendAsync(client, operation == "list" ? HttpMethod.Get : HttpMethod.Post,
            operation == "list" ? "/v1/pending" : "/v1/pending/" + Uri.EscapeDataString(id!) + "/" + operation,
            operation == "list" ? null : new { }, cancellationToken);
        return CliResult.Ok(operation == "list" ? "Proposals listed." : "Proposal resolution recorded.", await ReadJsonAsync(response, cancellationToken));
    }
}
