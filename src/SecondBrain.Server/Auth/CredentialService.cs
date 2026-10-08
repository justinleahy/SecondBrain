using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Security;
using SecondBrain.Infrastructure.Security;

namespace SecondBrain.Server.Auth;

public sealed class CredentialService(IAuthRepository repository, IKeyRing keyRing, CredentialFactory factory,
    IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock)
{
    public const string CookieName = "sb_session";
    private readonly IDataProtector protector = keyRing.DataProtectionProvider.CreateProtector(KeyRingPurposes.Session);
    public async Task<AuthenticatedCredential?> AuthenticateKeyAsync(string bearer, CancellationToken cancellationToken = default)
    {
        CredentialRecord? record = null;
        byte[] bytes = new byte[32];
        var parsed = false;
        if (bearer.Length <= 128 && bearer.StartsWith("sb_", StringComparison.Ordinal))
        {
            var split = bearer.IndexOf('.', 3);
            if (split == 29 && Ulid.TryParse(bearer[3..split], out _))
            {
                try
                {
                    var decoded = WebEncoders.Base64UrlDecode(bearer[(split + 1)..]);
                    if (decoded.Length == 32) { bytes = decoded; parsed = true; }
                }
                catch (FormatException) { }
                record = await repository.FindAsync(bearer[3..split], cancellationToken);
            }
        }
        // Exactly one inexpensive HMAC, including malformed and unknown public ids.
        bool valid;
        try
        {
            var expected = keyRing.Sign(record?.Kid ?? keyRing.ActiveKid, bytes);
            var stored = record is null ? new byte[32] : Convert.FromBase64String(record.Verifier);
            valid = CryptographicOperations.FixedTimeEquals(expected, stored) && parsed && record is not null;
        }
        catch (KeyNotFoundException) { valid = false; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (!valid || record!.Kind != "api_key" ||
            !await repository.TouchAsync(record.Id, record.Generation, record.AccountEpoch, clock.GetUtcNow(), null, cancellationToken)) return null;
        return ToIdentity(record);
    }
    public async Task<AuthenticatedCredential?> AuthenticateSessionAsync(string cookie, bool secureRequest, CancellationToken cancellationToken = default)
    {
        SessionTicket? ticket;
        if (cookie.Length > 4096) return null;
        try { ticket = JsonSerializer.Deserialize<SessionTicket>(protector.Unprotect(cookie)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException) { return null; }
        if (ticket is null || ticket.Secure && !secureRequest || !Ulid.TryParse(ticket.Id, out _)) return null;
        var record = await repository.FindAsync(ticket.Id, cancellationToken);
        if (record is null || record.Kind != "session" || record.Generation != ticket.Generation || record.AccountEpoch != ticket.AccountEpoch) return null;
        byte[] secret;
        try { secret = WebEncoders.Base64UrlDecode(ticket.Secret); }
        catch (FormatException) { return null; }
        try
        {
            if (secret.Length != 32 || !CryptographicOperations.FixedTimeEquals(keyRing.Sign(record.Kid, secret), Convert.FromBase64String(record.Verifier))) return null;
        }
        catch (KeyNotFoundException) { return null; }
        finally { CryptographicOperations.ZeroMemory(secret); }
        if (!await repository.TouchAsync(record.Id, ticket.Generation, ticket.AccountEpoch, clock.GetUtcNow(),
            clock.GetUtcNow().AddHours(options.CurrentValue.Auth.SessionIdleHours), cancellationToken)) return null;
        return ToIdentity(record);
    }
    public async Task<CreatedCredential> IssueSessionAsync(HttpContext context, AuthenticatedCredential? previous = null, bool stepUp = false, long? verifiedEpoch = null)
    {
        var config = options.CurrentValue;
        var epoch = verifiedEpoch ?? await repository.GetEpochAsync(context.RequestAborted);
        var issued = factory.CreateSession(context.Request.Headers.UserAgent.ToString(), IsSecure(context, config), epoch,
            config.Auth.SessionIdleHours, config.Auth.SessionAbsoluteDays, stepUp ? AuthTime.Format(clock.GetUtcNow()) : null);
        if (previous is not null && previous.Kind == "session")
        {
            if (!await repository.RotateAsync(previous.Id, previous.Generation, previous.AccountEpoch, issued.Record, context.RequestAborted))
                throw new AuthorityChangedException();
        }
        else await repository.AddAsync(issued.Record, context.RequestAborted);
        var secure = IsSecure(context, config);
        var ticket = new SessionTicket(issued.Record.Id, issued.Plaintext, issued.Record.Generation, epoch, secure);
        context.Response.Cookies.Append(CookieName, protector.Protect(JsonSerializer.Serialize(ticket)), new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = secure, Path = "/", IsEssential = true,
            Expires = AuthTime.Parse(issued.Record.AbsoluteExpiresAt!),
        });
        return issued;
    }
    public static void ClearCookie(HttpContext context) => context.Response.Cookies.Delete(CookieName,
        new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, Path = "/" });
    public static bool IsSecure(HttpContext context, SecondBrainOptions config) => context.Request.IsHttps ||
        string.Equals(context.Request.Host.Host, config.Server.CloudflareAccess?.PublicHostname, StringComparison.OrdinalIgnoreCase);
    private static AuthenticatedCredential ToIdentity(CredentialRecord record) =>
        new(record.Id, record.Kind, record.Generation, record.AccountEpoch, record.GrantedScopes, record.SteppedUpAt);
}
