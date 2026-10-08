using SecondBrain.Core.Authorization;

namespace SecondBrain.Server.Auth;

/// <summary>Appendix A credential row; verifiers never appear in HTTP responses.</summary>
public sealed class CredentialRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "api_key";
    public string Verifier { get; set; } = "";
    public string Scopes { get; set; } = "";
    public long Generation { get; set; } = 1;
    public string Kid { get; set; } = "";
    public long AccountEpoch { get; set; }
    public string? Device { get; set; }
    public string? IdleExpiresAt { get; set; }
    public string? AbsoluteExpiresAt { get; set; }
    public string? SteppedUpAt { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? LastUsedAt { get; set; }
    public string? ExpiresAt { get; set; }
    public string? RevokedAt { get; set; }
    public IReadOnlySet<Scope> GrantedScopes => Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => Enum.Parse<Scope>(value, true)).ToHashSet();
}

public sealed class AccountRecord
{
    public int Id { get; set; } = 1;
    public string PasswordHash { get; set; } = "";
    public int PasswordVersion { get; set; }
    public string UpdatedAt { get; set; } = "";
}

public sealed class LoginAttempt
{
    public string Source { get; set; } = "";
    public string At { get; set; } = "";
    public bool Success { get; set; }
}

public sealed record AuthenticatedCredential(string Id, string Kind, long Generation, long AccountEpoch, IReadOnlySet<Scope> Scopes, string? SteppedUpAt);
public sealed record SessionTicket(string Id, string Secret, long Generation, long AccountEpoch, bool Secure);
public sealed record SessionDevice(string Summary, bool Secure);
public sealed record CredentialSummary(string Id, string Name, string Kind, string Scopes, long Generation, string? Device,
    string CreatedAt, string? LastUsedAt, string? ExpiresAt, string? IdleExpiresAt, string? AbsoluteExpiresAt, string? RevokedAt)
{
    public static CredentialSummary From(CredentialRecord value) => new(value.Id, value.Name, value.Kind, value.Scopes,
        value.Generation, value.Device, value.CreatedAt, value.LastUsedAt, value.ExpiresAt, value.IdleExpiresAt, value.AbsoluteExpiresAt, value.RevokedAt);
}

internal static class AuthTime
{
    public static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    public static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    public static bool Expired(string? value, DateTimeOffset now) => value is not null && Parse(value) <= now;
}

public sealed class AuthorityChangedException() : InvalidOperationException("Credential authority changed while the action was being authorized.");
