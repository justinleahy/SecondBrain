using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Server.Http;

/// <summary>A dedicated control-plane transport; tests may replace it with a local JWKS transport.</summary>
public interface IAccessJwksTransport
{
    ValueTask<string> FetchAsync(Uri certificateEndpoint, CancellationToken cancellationToken);
}

public sealed class AccessJwksTransport(IOptionsMonitor<SecondBrainOptions> options) : IAccessJwksTransport, IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(5) };

    public async ValueTask<string> FetchAsync(Uri certificateEndpoint, CancellationToken cancellationToken)
    {
        if (certificateEndpoint.Scheme != Uri.UriSchemeHttps ||
            !certificateEndpoint.Host.EndsWith(".cloudflareaccess.com", StringComparison.OrdinalIgnoreCase) ||
            certificateEndpoint.AbsolutePath != "/cdn-cgi/access/certs")
            throw new InvalidOperationException("Invalid Access certificate endpoint.");
        if (!options.CurrentValue.Privacy.ControlPlaneEgress.Contains(certificateEndpoint.AbsoluteUri, StringComparer.Ordinal))
            throw new InvalidOperationException("Access certificate endpoint is not allowed for control-plane egress.");
        using var response = await client.GetAsync(certificateEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 262144) throw new InvalidOperationException("Access JWKS exceeds its limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > 262144) throw new InvalidOperationException("Access JWKS exceeds its limit.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    public void Dispose() => client.Dispose();
}

/// <summary>Daily refresh plus a single serialized, rate-limited unknown-kid refresh; failures retain the last good keys.</summary>
public sealed class AccessJwksCache(IAccessJwksTransport transport, IOptionsMonitor<SecondBrainOptions> options,
    TimeProvider clock, ILogger<AccessJwksCache> logger) : IDisposable
{
    private readonly SemaphoreSlim refresh = new(1, 1);
    private SecurityKey[] keys = [];
    private DateTimeOffset lastSuccess = DateTimeOffset.MinValue;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset lastUnknownAttempt = DateTimeOffset.MinValue;
    private string? teamDomain;
    private bool? lastRefreshSucceeded;
    public IReadOnlyList<SecurityKey> CurrentKeys => Volatile.Read(ref keys);
    public DateTimeOffset? LastSuccessfulRefresh => lastSuccess == DateTimeOffset.MinValue ? null : lastSuccess;
    public DateTimeOffset? LastRefreshAttempt => lastAttempt == DateTimeOffset.MinValue ? null : lastAttempt;
    public bool? LastRefreshSucceeded => lastRefreshSucceeded;
    public static TimeSpan UnknownKidRefreshInterval => TimeSpan.FromMinutes(1);

    public async Task EnsureKeysAsync(string? kid, CancellationToken cancellationToken)
    {
        await refresh.WaitAsync(cancellationToken);
        try
        {
            var access = options.CurrentValue.Server.CloudflareAccess;
            if (access is null) return;
            var issuer = Issuer(access);
            if (teamDomain != issuer)
            {
                teamDomain = issuer;
                Volatile.Write(ref keys, []);
                lastSuccess = lastAttempt = lastUnknownAttempt = DateTimeOffset.MinValue;
                lastRefreshSucceeded = null;
            }
            var now = clock.GetUtcNow();
            var due = keys.Length == 0 || now - lastSuccess >= TimeSpan.FromDays(1);
            if (due && (lastAttempt == DateTimeOffset.MinValue || now - lastAttempt >= UnknownKidRefreshInterval))
            {
                await RefreshAsync(issuer, now, cancellationToken);
                return;
            }
            if (kid is not null && !keys.Any(key => string.Equals(key.KeyId, kid, StringComparison.Ordinal)) &&
                (lastUnknownAttempt == DateTimeOffset.MinValue || now - lastUnknownAttempt >= UnknownKidRefreshInterval))
            {
                lastUnknownAttempt = now;
                await RefreshAsync(issuer, now, cancellationToken);
            }
        }
        finally { refresh.Release(); }
    }

    private async Task RefreshAsync(string issuer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lastAttempt = now;
        lastRefreshSucceeded = false;
        try
        {
            var document = await transport.FetchAsync(new Uri(issuer + "/cdn-cgi/access/certs"), cancellationToken);
            if (Encoding.UTF8.GetByteCount(document) > 262144) throw new InvalidOperationException("Access JWKS exceeds its limit.");
            var jwks = new JsonWebKeySet(document);
            var valid = jwks.Keys.Where(key => key.Kty == "RSA" && !string.IsNullOrEmpty(key.N) && !string.IsNullOrEmpty(key.E) &&
                !string.IsNullOrEmpty(key.Kid) && key.Kid.Length <= 256 && (string.IsNullOrEmpty(key.Use) || key.Use == "sig") &&
                (string.IsNullOrEmpty(key.Alg) || key.Alg == SecurityAlgorithms.RsaSha256)).Cast<SecurityKey>().ToArray();
            if (valid.Length is 0 or > 16 || valid.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != valid.Length)
                throw new InvalidOperationException("Access JWKS contains no usable bounded key set.");
            Volatile.Write(ref keys, valid);
            lastSuccess = now;
            lastRefreshSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            lastUnknownAttempt = now;
            logger.LogWarning(exception, "Access signing-key refresh failed; retaining the last good set");
        }
    }

    public static string Issuer(CloudflareAccessOptions access)
    {
        if (!Uri.TryCreate("https://" + access.TeamDomain, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != "https" || !issuer.Host.EndsWith(".cloudflareaccess.com", StringComparison.OrdinalIgnoreCase) ||
            issuer.AbsolutePath != "/" || !issuer.IsDefaultPort || !string.IsNullOrEmpty(issuer.UserInfo) ||
            !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment))
            throw new InvalidOperationException("Access team domain must be a Cloudflare Access team hostname.");
        return issuer.GetLeftPart(UriPartial.Authority);
    }

    public void Dispose() => refresh.Dispose();
}

public sealed class AccessJwksRefreshService(AccessJwksCache cache, IOptionsMonitor<SecondBrainOptions> options,
    TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            if (options.CurrentValue.Server.CloudflareAccess is not null)
                await cache.EnsureKeysAsync(null, stoppingToken);
    }
}
