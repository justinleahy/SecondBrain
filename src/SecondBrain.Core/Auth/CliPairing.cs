namespace SecondBrain.Core.Auth;

public sealed record CliPairingRequest(string Id, string Code, string Name, string PollVerifier, string Source,
    bool Secure, string CreatedAt, string ExpiresAt);
public sealed record CliPairingView(string Code, string Name, string ExpiresAt, string Status);
public interface ICliPairingRepository
{
    Task CreatePairingAsync(CliPairingRequest request, CancellationToken token = default);
    Task<CliPairingView?> FindPairingAsync(string code, CancellationToken token = default);
    Task ApprovePairingAsync(string code, AuthenticatedCredential actor, long passwordVerifiedEpoch, CancellationToken token = default);
    Task<bool> ClaimPairingAsync(string id, string pollVerifier, CredentialRecord credential, bool secureRequest, CancellationToken token = default);
}
public sealed class CliPairingException(string message) : InvalidOperationException(message);
