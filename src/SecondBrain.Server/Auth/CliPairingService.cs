using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Server.Auth;

public sealed class CliPairingService(ICliPairingRepository pairings, IAuthRepository auth, CredentialFactory factory,
    TimeProvider clock)
{
    public async Task<CliPairingStarted> BeginAsync(string name, string source, bool secure, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) throw new ArgumentException("Invalid device name.");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        var id = Ulid.NewUlid().ToString();
        var expires = AuthTime.Format(clock.GetUtcNow().AddMinutes(5));
        await pairings.CreatePairingAsync(new(id, code, name, Verifier(secret), source, secure, AuthTime.Format(clock.GetUtcNow()), expires), token);
        return new(id, code, secret, expires);
    }

    public async Task<CliPairingClaim> ClaimAsync(string id, string secret, bool secureRequest, CancellationToken token)
    {
        if (id is not { Length: 26 } || !Ulid.TryParse(id, out _) || secret is not { Length: 64 }) throw new ArgumentException("Invalid pairing request.");
        var created = factory.CreateSession("Paired CLI", false, await auth.GetEpochAsync(token), 1, 1);
        created.Record.Name = "cli";
        created.Record.AbsoluteExpiresAt = AuthTime.Format(clock.GetUtcNow().AddHours(8));
        if (!await pairings.ClaimPairingAsync(id, Verifier(secret), created.Record, secureRequest, token)) return new("pending");
        return new("approved", "sbcli_" + created.Record.Id + "." + created.Plaintext, created.Record.Id, created.Record.AbsoluteExpiresAt);
    }
    private static string Verifier(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

public sealed record CliPairingStarted(string Id, string Code, string PollSecret, string ExpiresAt)
{
    public override string ToString() => $"CliPairingStarted({Id}, redacted)";
}
public sealed record CliPairingClaim(string Status, string? Credential = null, string? CredentialId = null, string? ExpiresAt = null)
{
    public override string ToString() => $"CliPairingClaim({Status}, redacted)";
}
