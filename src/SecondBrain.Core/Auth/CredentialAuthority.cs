namespace SecondBrain.Core.Auth;

/// <summary>Reusable authority check for jobs, cursors and future live transports.</summary>
public interface ICredentialAuthority
{
    Task<bool> IsCurrentAsync(string credentialId, long generation, long accountEpoch, CancellationToken cancellationToken = default);
}
public sealed class CredentialAuthority(IAuthRepository repository) : ICredentialAuthority
{
    public Task<bool> IsCurrentAsync(string credentialId, long generation, long accountEpoch, CancellationToken cancellationToken = default) =>
        repository.IsCurrentAsync(credentialId, generation, accountEpoch, cancellationToken);
}
