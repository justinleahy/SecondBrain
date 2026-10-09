using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SecondBrain.Cli.Credentials;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Cli.Commands;

/// <summary>Daemon administration uses stored API keys; session operations use password-authenticated browser sessions.</summary>
public sealed class HttpDaemonCommands(
    Func<ICredentialStore> credentialStore,
    Func<string, CancellationToken, Task<string>> readPassword,
    Func<HttpClient>? createClient = null) : ICredentialCommands, IProviderCommands, ISessionCommands, ILoginCommands, IDoctorExtension
{
    /// <summary>Listings traverse every page up to this bound; exceeding it fails instead of truncating.</summary>
    public const int MaxListPages = 1000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Func<SecondBrainOptions>? loadOptions;
    private string? configuredOrigin;
    private string credentialName = Environment.MachineName;

    public void Configure(Func<SecondBrainOptions> options, string? origin, string name)
    {
        loadOptions = options;
        configuredOrigin = origin;
        credentialName = name;
    }

    public async Task<CliResult> CreateAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        using var client = await OpenAsync(apiKey: true, cancellationToken);
        using var response = await SendAsync(client, HttpMethod.Post, "/keys", new { name = credentialName, scopes }, cancellationToken);
        var issued = await ReadCreatedKeyAsync(response, cancellationToken);
        return CliResult.Ok("API key created. Save this key; it is shown once.", issued);
    }

    async Task<CliResult> ICredentialCommands.ListAsync(CancellationToken cancellationToken)
    {
        using var client = await OpenAsync(apiKey: true, cancellationToken);
        return CliResult.Ok("API keys listed.", await GetAllPagesAsync(client, "/keys", cancellationToken));
    }
    Task<CliResult> ICredentialCommands.RevokeAsync(string id, CancellationToken cancellationToken) =>
        DeleteAsync("/keys/" + EscapeId(id), "API key revoked.", cancellationToken);
    Task<CliResult> IProviderCommands.ListAsync(CancellationToken cancellationToken) => GetAsync("/providers", "Providers and models listed.", cancellationToken);

    public async Task<CliResult> TestAsync(string? name, CancellationToken cancellationToken)
    {
        using var client = await OpenAsync(apiKey: true, cancellationToken);
        using var response = await SendAsync(client, HttpMethod.Post, "/providers/test", new { name }, cancellationToken);
        var data = await ReadJsonAsync(response, cancellationToken);
        // Provider health failures remain a failed precondition even when the diagnostic endpoint returned 200.
        var failed = ContainsFailedHealth(data);
        return new(failed ? CliExitCode.PreconditionFailed : CliExitCode.Ok, failed ? "provider-unavailable" : "ok",
            failed ? "A configured provider failed its reachability check." : "Provider checks passed.", data);
    }

    public async Task<LoginCredential> AcquireAsync(Uri origin, string name, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        ValidateOrigin(origin);
        using var client = NewClient(origin);
        var password = await readPassword("login", cancellationToken);
        await LoginAsync(client, password, cancellationToken);
        try
        {
            await StepUpAsync(client, password, cancellationToken);
            using var response = await SendAsync(client, HttpMethod.Post, "/keys", new { name, scopes }, cancellationToken);
            var created = await ReadCreatedKeyAsync(response, cancellationToken);
            return new LoginCredential(created.Key, created.Id);
        }
        finally { await LogoutAsync(client, cancellationToken); }
    }

    async Task<CliResult> ISessionCommands.ListAsync(CancellationToken cancellationToken)
    {
        using var client = await OpenAsync(apiKey: false, cancellationToken);
        var currentId = await LoginAsync(client, await readPassword("sessions", cancellationToken), cancellationToken);
        try
        {
            var data = await GetAllPagesAsync(client, "/auth/sessions", cancellationToken);
            // The short-lived CLI transport session is closed below and is not useful in the returned active-session list.
            var sessions = data.Where(item => item.GetProperty("id").GetString() != currentId).ToArray();
            return CliResult.Ok("Browser sessions listed.", sessions);
        }
        finally { await LogoutAsync(client, cancellationToken); }
    }

    async Task<CliResult> ISessionCommands.RevokeAsync(string id, CancellationToken cancellationToken)
    {
        var path = "/auth/sessions/" + EscapeId(id);
        using var client = await OpenAsync(apiKey: false, cancellationToken);
        var password = await readPassword("sessions", cancellationToken);
        await LoginAsync(client, password, cancellationToken);
        try
        {
            // Revoking another session needs a fresh step-up (SEC-10); the same password re-authenticates without a second prompt.
            await StepUpAsync(client, password, cancellationToken);
            using (await SendAsync(client, HttpMethod.Delete, path, null, cancellationToken)) { }
            return CliResult.Ok("Browser session revoked.", new { id });
        }
        finally { await LogoutAsync(client, cancellationToken); }
    }

    public async Task<CliResult> RevokeAllAsync(CancellationToken cancellationToken)
    {
        using var client = await OpenAsync(apiKey: false, cancellationToken);
        var password = await readPassword("sessions", cancellationToken);
        await LoginAsync(client, password, cancellationToken);
        try
        {
            await StepUpAsync(client, password, cancellationToken);
            using (await SendAsync(client, HttpMethod.Post, "/auth/logout-all", new { }, cancellationToken)) { }
            return CliResult.Ok("All sessions and API keys invalidated by the account epoch change.");
        }
        finally { await LogoutAsync(client, cancellationToken); }
    }

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(SecondBrainOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await OpenAsync(apiKey: true, cancellationToken, options);
            using var response = await SendAsync(client, HttpMethod.Get, "/diagnostics", null, cancellationToken);
            var data = await ReadJsonAsync(response, cancellationToken);
            var checks = data.GetProperty("checks").Deserialize<DoctorCheck[]>(Json) ?? throw new JsonException();
            return checks;
        }
        catch (DaemonCommandException ex) { return [new("daemon.diagnostics", false, ex.Result.Message)]; }
        catch (Exception ex) when (ex is HttpRequestException or DaemonUnreachableException)
        { return [new("daemon.unreachable", false, "The daemon diagnostics endpoint could not be reached.")]; }
        catch (CliPreconditionException ex) { return [new("daemon.diagnostics", false, ex.Message)]; }
    }

    /// <summary>
    /// Follows <c>Link: rel="next"</c> only to the same origin and path, so credentials never reach another destination.
    /// A repeated link, an invalid link or more than <see cref="MaxListPages"/> pages fails without a partial result.
    /// </summary>
    private static async Task<JsonElement[]> GetAllPagesAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { path };
        string? next = path;
        for (var pages = 0; next is not null; pages++)
        {
            if (pages == MaxListPages)
                throw ListingFailure("listing-incomplete", $"The daemon listing has more than {MaxListPages} pages; no partial result is returned.");
            using var response = await SendAsync(client, HttpMethod.Get, next, null, cancellationToken);
            var data = await ReadJsonAsync(response, cancellationToken);
            if (data.ValueKind != JsonValueKind.Array) throw ListingFailure("listing-invalid", "The daemon returned a listing page that is not an array.");
            items.AddRange(data.EnumerateArray().Select(item => item.Clone()));
            next = NextPage(response, client.BaseAddress!, path);
            if (next is not null && !seen.Add(next)) throw ListingFailure("listing-incomplete", "The daemon repeated a listing page link; no partial result is returned.");
        }
        return items.ToArray();
    }
    private static string? NextPage(HttpResponseMessage response, Uri origin, string path)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        // RFC 8288 link-values: "<target>; rel=\"next\"", comma separated; the daemon's targets contain no commas.
        var targets = values.SelectMany(value => value.Split(',')).Select(link => link.Split(';'))
            .Where(parts => parts.Skip(1).Select(part => part.Split('=', 2)).Any(parameter => parameter.Length == 2 &&
                parameter[0].Trim().Equals("rel", StringComparison.OrdinalIgnoreCase) &&
                parameter[1].Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("next", StringComparer.OrdinalIgnoreCase)))
            .Select(parts => parts[0].Trim()).ToArray();
        if (targets.Length == 0) return null;
        if (targets.Length > 1 || targets[0] is not ['<', .., '>'] || !Uri.TryCreate(origin, targets[0][1..^1], out var target) ||
            Uri.Compare(target, origin, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) != 0 ||
            !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Fragment) || target.AbsolutePath != path)
            throw ListingFailure("listing-link-rejected", "The daemon returned a next-page link outside the listing path; it was not followed.");
        return target.PathAndQuery;
    }
    private static DaemonCommandException ListingFailure(string code, string message) => new(new(CliExitCode.Error, code, message));
    private static async Task StepUpAsync(HttpClient client, string password, CancellationToken cancellationToken)
    {
        // Step-up rotates the session, so the antiforgery token is refreshed for the following state change.
        await SetAntiforgeryAsync(client, cancellationToken);
        using (await SendAsync(client, HttpMethod.Post, "/auth/step-up", new { password }, cancellationToken)) { }
        await SetAntiforgeryAsync(client, cancellationToken);
    }

    private async Task<CliResult> GetAsync(string path, string message, CancellationToken cancellationToken)
    {
        using var client = await OpenAsync(apiKey: true, cancellationToken);
        using var response = await SendAsync(client, HttpMethod.Get, path, null, cancellationToken);
        return CliResult.Ok(message, await ReadJsonAsync(response, cancellationToken));
    }
    private async Task<CliResult> DeleteAsync(string path, string message, CancellationToken cancellationToken)
    {
        using var client = await OpenAsync(apiKey: true, cancellationToken);
        using (await SendAsync(client, HttpMethod.Delete, path, null, cancellationToken)) { }
        return CliResult.Ok(message);
    }

    private async Task<HttpClient> OpenAsync(bool apiKey, CancellationToken cancellationToken, SecondBrainOptions? options = null)
    {
        var origin = configuredOrigin is not null ? ParseOrigin(configuredOrigin) : ResolveOrigin(options ?? loadOptions?.Invoke()
            ?? throw new CliPreconditionException("Select the daemon origin with --url or SECONDBRAIN_URL, or supply --config."));
        var client = NewClient(origin);
        try
        {
            if (apiKey)
            {
                var key = Environment.GetEnvironmentVariable("SECONDBRAIN_API_KEY")
                    ?? await credentialStore().ReadAsync(origin, credentialName, cancellationToken);
                if (string.IsNullOrWhiteSpace(key)) throw new CliPreconditionException("No API key is stored for this daemon and credential name; run brain login with --scopes admin for administration.");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private HttpClient NewClient(Uri origin)
    {
        var client = createClient?.Invoke() ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = true, CookieContainer = new CookieContainer(),
        });
        client.BaseAddress = origin;
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SecondBrain-brain/0.1");
        if (Environment.GetEnvironmentVariable("SECONDBRAIN_ACCESS_ASSERTION") is { Length: > 0 } assertion)
            client.DefaultRequestHeaders.Add("Cf-Access-Jwt-Assertion", assertion);
        return client;
    }

    private static Uri ResolveOrigin(SecondBrainOptions options)
    {
        var configured = options.Server.Origins.FirstOrDefault(origin =>
            !string.Equals(new Uri(origin).Host, options.Server.CloudflareAccess?.PublicHostname, StringComparison.OrdinalIgnoreCase));
        if (configured is not null) return ParseOrigin(configured);
        var listener = options.Server.Listeners.FirstOrDefault() ?? throw new CliPreconditionException("The configuration has no daemon origin or listener.");
        return ParseOrigin(new UriBuilder(listener.Scheme, listener.Bind, listener.Port).Uri.ToString());
    }
    private static Uri ParseOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin)) throw new CliUsageException("The daemon URL must be an absolute HTTP(S) origin.");
        ValidateOrigin(origin);
        return origin;
    }
    private static void ValidateOrigin(Uri origin)
    {
        if (origin.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new CliUsageException("The daemon URL must be an HTTP(S) origin without credentials, a path, query or fragment.");
    }
    private static string EscapeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new CliUsageException("A credential or session id is required.");
        return Uri.EscapeDataString(id);
    }

    private static async Task<string?> LoginAsync(HttpClient client, string password, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/auth/login", new { password }, cancellationToken);
        return (await ReadJsonAsync(response, cancellationToken)).GetProperty("id").GetString();
    }
    private static async Task SetAntiforgeryAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/auth/antiforgery", null, cancellationToken);
        var data = await ReadJsonAsync(response, cancellationToken);
        var header = data.GetProperty("headerName").GetString() ?? throw new JsonException();
        client.DefaultRequestHeaders.Remove(header);
        client.DefaultRequestHeaders.Add(header, data.GetProperty("token").GetString());
    }
    private static async Task LogoutAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await SetAntiforgeryAsync(client, cancellationToken);
            using (await SendAsync(client, HttpMethod.Post, "/auth/logout", new { }, cancellationToken)) { }
        }
        catch (Exception ex) when (ex is DaemonCommandException or HttpRequestException or OperationCanceledException or DaemonUnreachableException) { }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new DaemonUnreachableException("The daemon request timed out."); }
        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode;
        var exit = status == 400 ? CliExitCode.Usage : status is >= 400 and < 500 || status == 503 ? CliExitCode.PreconditionFailed : CliExitCode.Error;
        response.Dispose();
        throw new DaemonCommandException(new(exit, "daemon-rejected", $"The daemon rejected the request (HTTP {status})."));
    }
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
    private static async Task<CreatedApiKeyResult> ReadCreatedKeyAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<CreatedApiKeyResult>(Json, cancellationToken)
            ?? throw new JsonException("The daemon returned no credential.");

    private static bool ContainsFailedHealth(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(ContainsFailedHealth);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if ((property.Name.Equals("reachable", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("healthy", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("isHealthy", StringComparison.OrdinalIgnoreCase) ||
                 property.Name.Equals("ready", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("passed", StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.False) return true;
            if (ContainsFailedHealth(property.Value)) return true;
        }
        return false;
    }
}

public sealed record CreatedApiKeyResult(string Id, string Key, string Scopes, string? ExpiresAt) : IOneTimeSecret
{
    [JsonIgnore] public string Secret => Key;
    public override string ToString() => $"CreatedApiKeyResult({Id}, redacted)";
}
