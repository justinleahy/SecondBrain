namespace SecondBrain.Core.Auth;

public interface IAuthRepository
{
    Task<CredentialRecord?> FindAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>One keyset page in (created_at, id) order; <paramref name="activeOnly"/> drops revoked, stale-epoch and expired rows before the limit.</summary>
    Task<CredentialPage> ListPageAsync(string kind, bool activeOnly, CredentialCursor? after, int limit, CancellationToken cancellationToken = default);
    Task<long> GetEpochAsync(CancellationToken cancellationToken = default);
    /// <summary>The persisted <c>meta.instance_id</c> written by initialization; a store without one is not initialized.</summary>
    Task<string> GetInstanceIdAsync(CancellationToken cancellationToken = default);
    Task<AccountRecord?> GetAccountAsync(CancellationToken cancellationToken = default);
    Task SaveAccountAsync(AccountRecord account, bool invalidate = false, CancellationToken cancellationToken = default);
    Task<bool> RehashAsync(AccountRecord expected, AccountRecord replacement, long epoch, CancellationToken cancellationToken = default);
    /// <summary>Credential management checks the admitted actor again inside the mutation transaction.</summary>
    Task AddAuthorizedAsync(CredentialRecord credential, AuthenticatedCredential actor, TimeSpan stepUpWindow, CancellationToken cancellationToken = default);
    Task<bool> RevokeAuthorizedAsync(string id, string kind, AuthenticatedCredential actor, TimeSpan stepUpWindow, CancellationToken cancellationToken = default);
    Task<long> BumpEpochAuthorizedAsync(AuthenticatedCredential actor, TimeSpan stepUpWindow, CancellationToken cancellationToken = default);
    /// <summary>Password-verified issuance or explicit local maintenance; HTTP credential management uses actor-aware methods.</summary>
    Task AddAsync(CredentialRecord credential, CancellationToken cancellationToken = default);
    Task<bool> RotateAsync(string previousId, long generation, long epoch, CredentialRecord replacement, CancellationToken cancellationToken = default);
    Task<bool> TouchAsync(string id, long generation, long epoch, DateTimeOffset now, DateTimeOffset? idleExpiry, CancellationToken cancellationToken = default);
    Task<bool> IsCurrentAsync(string id, long generation, long epoch, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(string id, string? kind = null, CancellationToken cancellationToken = default);
    Task<long> BumpEpochAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// A source's attempts from the later of <paramref name="since"/> and its latest success (inclusive, ties broken by insertion order),
    /// as at most the newest <paramref name="limit"/> rows in chronological order.
    /// </summary>
    Task<IReadOnlyList<LoginAttempt>> LoginAttemptsAsync(string source, DateTimeOffset since, int limit, CancellationToken cancellationToken = default);
    Task RecordLoginAsync(string source, DateTimeOffset at, bool success, CancellationToken cancellationToken = default);
}
