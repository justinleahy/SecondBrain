namespace SecondBrain.Cli.Commands;

public sealed partial class HttpDaemonCommands
{
    public async Task<CliResult> EnrichAsync(string id, CancellationToken token)
    {
        using var client = await OpenAsync(apiKey: true, token);
        using var response = await SendAsync(client, HttpMethod.Post, "/v1/documents/" + EscapeId(id) + "/enrich", new { }, token);
        return CliResult.Ok("Enrichment requested.", await ReadJsonAsync(response, token));
    }
    public Task<CliResult> ReadUsageAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken token)
    {
        var values = new List<string>();
        if (from is { } start) values.Add("from=" + Uri.EscapeDataString(start.ToString("O")));
        if (to is { } end) values.Add("to=" + Uri.EscapeDataString(end.ToString("O")));
        return GetAsync("/v1/usage" + (values.Count == 0 ? "" : "?" + string.Join("&", values)), "Model usage reported.", token);
    }
    public async Task<CliResult> RunRetentionAsync(CancellationToken token)
    {
        using var client = await OpenAsync(apiKey: false, token);
        var password = await readPassword("maintenance", token);
        await LoginAsync(client, password, token);
        try
        {
            await StepUpAsync(client, password, token);
            using var response = await SendAsync(client, HttpMethod.Post, "/v1/maintenance/retention", new { }, token);
            return CliResult.Ok("Retention cleanup completed; review the counts for deferred work.", await ReadJsonAsync(response, token));
        }
        finally { await LogoutAsync(client, token); }
    }
}
