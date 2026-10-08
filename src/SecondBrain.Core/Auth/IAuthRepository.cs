namespace SecondBrain.Core.Auth;

public interface IAuthRepository
{
    Task<CredentialRecord?> FindAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CredentialRecord>> ListAsync(string kind, CancellationToken cancellationToken = default);
    Task<long> GetEpochAsync(CancellationToken cancellationToken = default);
    Task<AccountRecord?> GetAccountAsync(CancellationToken cancellationToken = default);
    Task SaveAccountAsync(AccountRecord account, bool invalidate = false, CancellationToken cancellationToken = default);
    Task<bool> RehashAsync(AccountRecord expected, AccountRecord replacement, long epoch, CancellationToken cancellationToken = default);
    Task AddAsync(CredentialRecord credential, CancellationToken cancellationToken = default);
    Task<bool> RotateAsync(string previousId, long generation, long epoch, CredentialRecord replacement, CancellationToken cancellationToken = default);
    Task<bool> TouchAsync(string id, long generation, long epoch, DateTimeOffset now, DateTimeOffset? idleExpiry, CancellationToken cancellationToken = default);
    Task<bool> IsCurrentAsync(string id, long generation, long epoch, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(string id, string? kind = null, CancellationToken cancellationToken = default);
    Task<long> BumpEpochAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LoginAttempt>> LoginAttemptsAsync(string source, DateTimeOffset since, CancellationToken cancellationToken = default);
    Task RecordLoginAsync(string source, DateTimeOffset at, bool success, CancellationToken cancellationToken = default);
}
