namespace SecondBrain.Cli.Commands;

public sealed partial class HttpDaemonCommands : ISourceCommands
{
    public async Task<CliResult> AddCalendarAsync(string name, string url, int interval, bool enabled, CancellationToken token)
    {
        using var client = await OpenAsync(apiKey: true, token, interactive: true);
        using var response = await SendAsync(client, HttpMethod.Post, "/v1/sources",
            new { kind = "calendar_subscription", name, url, poll_interval_s = interval, enabled }, token);
        return CliResult.Ok(enabled ? "Calendar registered; its first refresh is queued." : "Calendar registered and paused.", await ReadJsonAsync(response, token));
    }
    public async Task<CliResult> ListSourcesAsync(CancellationToken token)
    {
        using var client = await OpenAsync(apiKey: true, token, interactive: true);
        using var response = await SendAsync(client, HttpMethod.Get, "/v1/sources", null, token);
        return CliResult.Ok("Sources listed.", await ReadJsonAsync(response, token));
    }
    public async Task<CliResult> ScanSourceAsync(string id, bool deep, CancellationToken token)
    {
        using var client = await OpenAsync(apiKey: true, token, interactive: true);
        using var response = await SendAsync(client, HttpMethod.Post, "/v1/sources/" + EscapeId(id) + "/scan" + (deep ? "?deep=1" : ""), new { }, token);
        return CliResult.Ok("Scan requested; calendars refresh asynchronously.", await ReadJsonAsync(response, token));
    }
    public Task<CliResult> SetSourceEnabledAsync(string id, bool enabled, CancellationToken token) =>
        MutateSourceAsync(id, HttpMethod.Patch, new { status = enabled ? "active" : "disabled" }, enabled ? "Source resumed." : "Source paused.", token);
    public Task<CliResult> RemoveSourceAsync(string id, CancellationToken token, bool purge = false) =>
        MutateSourceAsync(id, HttpMethod.Delete, null, purge ? "Source and retained data purged; external originals remain." : "Source removed; imported documents are hidden.", token, purge);
    private async Task<CliResult> MutateSourceAsync(string id, HttpMethod method, object? body, string message, CancellationToken token, bool purge = false)
    {
        // Source lifecycle changes retain the browser's recent-password requirement.
        using var client = await OpenAsync(apiKey: false, token);
        var password = await readPassword("sources", token);
        await LoginAsync(client, password, token);
        try
        {
            await StepUpAsync(client, password, token);
            using (await SendAsync(client, method, "/v1/sources/" + EscapeId(id) + (purge ? "?purge=1" : ""), body, token)) { }
            return CliResult.Ok(message, new { id });
        }
        finally { await LogoutAsync(client, token); }
    }
}
