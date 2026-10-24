using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace SecondBrain.Cli.Commands;

public sealed partial class HttpDaemonCommands : IMcpBridge
{
    private const int MaximumMcpResponseCharacters = 8_000_000;
    public async Task<CliResult> RunMcpAsync(string? credential, TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        if (credential is not null)
        {
            if (string.IsNullOrWhiteSpace(credential) || credential.Length > 128) throw new CliUsageException("Invalid bridge credential name.");
            credentialName = credential;
        }
        using var client = await OpenAsync(apiKey: true, cancellationToken);
        client.Timeout = TimeSpan.FromMinutes(6);
        if (client.DefaultRequestHeaders.Authorization?.Parameter?.StartsWith("sbcli_", StringComparison.Ordinal) == true)
            throw new CliPreconditionException("The MCP bridge requires a dedicated API key, never an interactive session.");
        using (var identityResponse = await SendAsync(client, HttpMethod.Get, "/v1/auth/credential", null, cancellationToken))
        {
            var identity = await ReadJsonAsync(identityResponse, cancellationToken);
            if (identity.GetProperty("kind").GetString() != "api_key") throw new CliPreconditionException("MCP requires an API key.");
            var scopes = identity.GetProperty("scopes").EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()!.ToLowerInvariant() : value.GetInt32() switch { 0 => "read", 1 => "write", 2 => "infer", 3 => "admin", _ => "unknown" }).ToHashSet();
            if (scopes.Contains("admin")) throw new CliPreconditionException("Do not use an administrator key for MCP. Create a dedicated client key.");
            if (credential is null && (scopes.Count != 1 || !scopes.Contains("read")))
                throw new CliPreconditionException("The default MCP key must be read-only. Select a separately named write/infer key with --credential.");
        }
        var version = "2026-07-28";
        while (await ReadBoundedLineAsync(input, 2_000_000, cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                throw new CliUsageException("The MCP bridge accepts one JSON-RPC request or notification per line.");
            if (root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
            {
                if (method.GetString() == "initialize" && parameters.TryGetProperty("protocolVersion", out var protocol)) version = protocol.GetString() ?? version;
                if (parameters.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                    meta.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var protocolMeta)) version = protocolMeta.GetString() ?? version;
            }
            if (version.Length > 32 || version.Any(char.IsControl)) throw new CliUsageException("Invalid MCP protocol version.");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(line, Encoding.UTF8, "application/json") };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("MCP-Protocol-Version", version);
            if (string.CompareOrdinal(version, "2026-07-28") >= 0)
            {
                request.Headers.Add("Mcp-Method", McpHeaderEncoder.EncodeValue(method.GetString()!));
                if (root.TryGetProperty("params", out var namedParams))
                {
                    var member = method.GetString() switch { "tools/call" or "prompts/get" => "name", "resources/read" => "uri", _ => null };
                    if (member is not null && namedParams.TryGetProperty(member, out var name))
                        request.Headers.Add("Mcp-Name", McpHeaderEncoder.EncodeValue(name.GetString() ?? ""));
                }
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // A notification may produce no JSON-RPC response. Never print CLI status text on stdout.
            if (response.StatusCode is System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.NoContent) continue;
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                var data = new StringBuilder();
                while (await ReadBoundedLineAsync(reader, MaximumMcpResponseCharacters, cancellationToken) is { } eventLine)
                {
                    if (eventLine.Length == 0 && data.Length != 0) { await ForwardAsync(data.ToString()); data.Clear(); }
                    else if (eventLine.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length + eventLine.Length > MaximumMcpResponseCharacters) throw new CliPreconditionException("The MCP response exceeds its size limit.");
                        if (data.Length > 0) data.Append('\n');
                        data.Append(eventLine.AsSpan(5).TrimStart());
                    }
                }
                if (data.Length != 0) await ForwardAsync(data.ToString());
            }
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                var body = new StringBuilder();
                var buffer = new char[8192];
                int count;
                while ((count = await reader.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (body.Length + count > MaximumMcpResponseCharacters) throw new CliPreconditionException("The MCP response exceeds its size limit.");
                    body.Append(buffer, 0, count);
                }
                if (body.Length != 0) await ForwardAsync(body.ToString());
                else if (!response.IsSuccessStatusCode) ThrowResponseFailure(response);
            }

            async Task ForwardAsync(string payload)
            {
                using var message = JsonDocument.Parse(payload);
                if (!message.RootElement.TryGetProperty("jsonrpc", out var rpc) || rpc.GetString() != "2.0")
                    throw new CliPreconditionException("The daemon returned an invalid MCP response.");
                await output.WriteLineAsync(JsonSerializer.Serialize(message.RootElement));
                await output.FlushAsync(cancellationToken);
            }
        }
        return CliResult.Ok("MCP bridge stopped.");
    }
}
