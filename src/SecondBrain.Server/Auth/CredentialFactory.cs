using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using SecondBrain.Core.Authorization;
using SecondBrain.Infrastructure.Security;

namespace SecondBrain.Server.Auth;

/// <summary>Creation result: Plaintext is returned once and must never be logged.</summary>
public sealed class CreatedCredential(CredentialRecord record, string plaintext)
{
    public CredentialRecord Record { get; } = record;
    public string Plaintext { get; } = plaintext;
    public override string ToString() => $"CreatedCredential({Record.Id}, redacted)";
}

public sealed record InitializationRecords(CreatedCredential AdminCredential, AccountRecord Account);

/// <summary>Lane A/C init integration: produces Appendix A rows without creating stores.</summary>
public interface IAdminCredentialFactory
{
    InitializationRecords CreateInitializationRecords(string password, long accountEpoch = 1);
}

public sealed class CredentialFactory(IKeyRing keyRing, IPasswordHasher passwordHasher, TimeProvider clock) : IAdminCredentialFactory
{
    public InitializationRecords CreateInitializationRecords(string password, long accountEpoch = 1) =>
        new(CreateApiKey("admin", Enum.GetValues<Scope>().ToHashSet(), accountEpoch), CreateAccount(password));
    public AccountRecord CreateAccount(string password) => new()
    {
        PasswordHash = passwordHasher.Hash(password), PasswordVersion = passwordHasher.Parameters.Version,
        UpdatedAt = AuthTime.Format(clock.GetUtcNow()),
    };
    public CreatedCredential CreateApiKey(string name, IReadOnlySet<Scope> scopes, long epoch, DateTimeOffset? expiresAt = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || scopes.Count == 0 || scopes.Any(s => !Enum.IsDefined(s)))
            throw new ArgumentException("A bounded name and at least one recognized scope are required.");
        if (expiresAt <= clock.GetUtcNow()) throw new ArgumentException("Expiry must be in the future.");
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var record = NewRecord(name, "api_key", secret, scopes, epoch);
            record.ExpiresAt = expiresAt is null ? null : AuthTime.Format(expiresAt.Value);
            return new(record, $"sb_{record.Id}.{WebEncoders.Base64UrlEncode(secret)}");
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    internal CreatedCredential CreateSession(string device, bool secure, long epoch, int idleHours, int absoluteDays, string? steppedUpAt = null)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var record = NewRecord("browser", "session", secret, Enum.GetValues<Scope>().ToHashSet(), epoch);
            record.Device = System.Text.Json.JsonSerializer.Serialize(new SessionDevice(device.Length > 256 ? device[..256] : device, secure));
            record.IdleExpiresAt = AuthTime.Format(clock.GetUtcNow().AddHours(idleHours));
            record.AbsoluteExpiresAt = AuthTime.Format(clock.GetUtcNow().AddDays(absoluteDays));
            record.SteppedUpAt = steppedUpAt;
            return new(record, WebEncoders.Base64UrlEncode(secret));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    private CredentialRecord NewRecord(string name, string kind, byte[] secret, IReadOnlySet<Scope> scopes, long epoch)
    {
        var kid = keyRing.ActiveKid;
        return new()
        {
            Id = Ulid.NewUlid().ToString(), Name = name, Kind = kind,
            Scopes = string.Join(',', scopes.Order().Select(s => s.ToString().ToLowerInvariant())),
            Kid = kid, Verifier = Convert.ToBase64String(keyRing.Sign(kid, secret)),
            AccountEpoch = epoch, Generation = 1, CreatedAt = AuthTime.Format(clock.GetUtcNow()),
        };
    }
}
