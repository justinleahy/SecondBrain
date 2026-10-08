using Dapper;
using SecondBrain.Core.Storage;

namespace SecondBrain.Server.Auth;

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

/// <summary>Database-only callbacks, through the store's bounded writer; no transaction ownership escapes.</summary>
public sealed class AuthRepository(IStateStore store, TimeProvider clock) : IAuthRepository
{
    private const string Columns = "id Id, name Name, kind Kind, verifier Verifier, scopes Scopes, generation Generation, kid Kid, account_epoch AccountEpoch, device Device, idle_expires_at IdleExpiresAt, absolute_expires_at AbsoluteExpiresAt, stepped_up_at SteppedUpAt, created_at CreatedAt, last_used_at LastUsedAt, expires_at ExpiresAt, revoked_at RevokedAt";
    private const string Insert = "INSERT INTO credentials (id,name,kind,verifier,scopes,generation,kid,account_epoch,device,idle_expires_at,absolute_expires_at,stepped_up_at,created_at,last_used_at,expires_at,revoked_at) VALUES (@Id,@Name,@Kind,@Verifier,@Scopes,@Generation,@Kid,@AccountEpoch,@Device,@IdleExpiresAt,@AbsoluteExpiresAt,@SteppedUpAt,@CreatedAt,@LastUsedAt,@ExpiresAt,@RevokedAt)";
    public async Task<CredentialRecord?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken);
        return await lease.Connection.QuerySingleOrDefaultAsync<CredentialRecord>(new CommandDefinition($"SELECT {Columns} FROM credentials WHERE id=@id", new { id }, cancellationToken: cancellationToken));
    }
    public async Task<IReadOnlyList<CredentialRecord>> ListAsync(string kind, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken);
        return (await lease.Connection.QueryAsync<CredentialRecord>(new CommandDefinition($"SELECT {Columns} FROM credentials WHERE kind=@kind ORDER BY created_at,id LIMIT 1000", new { kind }, cancellationToken: cancellationToken))).AsList();
    }
    public async Task<long> GetEpochAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken);
        return await lease.Connection.QuerySingleAsync<long>(new CommandDefinition("SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'", cancellationToken: cancellationToken));
    }
    public async Task<AccountRecord?> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken);
        return await lease.Connection.QuerySingleOrDefaultAsync<AccountRecord>(new CommandDefinition("SELECT id Id, password_hash PasswordHash, password_version PasswordVersion, updated_at UpdatedAt FROM account WHERE id=1", cancellationToken: cancellationToken));
    }
    public async Task SaveAccountAsync(AccountRecord account, bool invalidate = false, CancellationToken cancellationToken = default) =>
        _ = await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO account (id,password_hash,password_version,updated_at) VALUES (1,@PasswordHash,@PasswordVersion,@UpdatedAt) ON CONFLICT(id) DO UPDATE SET password_hash=excluded.password_hash,password_version=excluded.password_version,updated_at=excluded.updated_at", account, transaction, cancellationToken: token));
            if (invalidate) await connection.ExecuteAsync(new CommandDefinition("UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='account_epoch'", transaction: transaction, cancellationToken: token));
            return true;
        }, cancellationToken);
    public async Task<bool> RehashAsync(AccountRecord expected, AccountRecord replacement, long epoch, CancellationToken cancellationToken = default) =>
        await store.QueueWriteAsync(async (connection, transaction, token) =>
            await connection.ExecuteAsync(new CommandDefinition("UPDATE account SET password_hash=@PasswordHash,password_version=@PasswordVersion,updated_at=@UpdatedAt WHERE id=1 AND password_hash=@oldHash AND password_version=@oldVersion AND @epoch=(SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch')", new { replacement.PasswordHash, replacement.PasswordVersion, replacement.UpdatedAt, oldHash = expected.PasswordHash, oldVersion = expected.PasswordVersion, epoch }, transaction, cancellationToken: token)) == 1, cancellationToken);
    public async Task AddAsync(CredentialRecord credential, CancellationToken cancellationToken = default) =>
        _ = await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            // Prevent issuance against an epoch that changed while password verification was running.
            var epoch = await connection.QuerySingleAsync<long>(new CommandDefinition("SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'", transaction: transaction, cancellationToken: token));
            if (epoch != credential.AccountEpoch) throw new AuthorityChangedException();
            return await connection.ExecuteAsync(new CommandDefinition(Insert, credential, transaction, cancellationToken: token));
        }, cancellationToken);
    public async Task<bool> RotateAsync(string previousId, long generation, long epoch, CredentialRecord replacement, CancellationToken cancellationToken = default) =>
        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var now = AuthTime.Format(clock.GetUtcNow());
            var changed = await connection.ExecuteAsync(new CommandDefinition("UPDATE credentials SET generation=generation+1,revoked_at=@now WHERE id=@previousId AND generation=@generation AND account_epoch=@epoch AND revoked_at IS NULL AND account_epoch=(SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch') AND (idle_expires_at IS NULL OR idle_expires_at>@now) AND (absolute_expires_at IS NULL OR absolute_expires_at>@now)", new { now, previousId, generation, epoch }, transaction, cancellationToken: token));
            if (changed == 0) return false;
            await connection.ExecuteAsync(new CommandDefinition(Insert, replacement, transaction, cancellationToken: token));
            return true;
        }, cancellationToken);
    public async Task<bool> TouchAsync(string id, long generation, long epoch, DateTimeOffset now, DateTimeOffset? idleExpiry, CancellationToken cancellationToken = default) =>
        await store.QueueWriteAsync(async (connection, transaction, token) =>
            await connection.ExecuteAsync(new CommandDefinition("UPDATE credentials SET last_used_at=@at, idle_expires_at=CASE WHEN @idle IS NULL THEN idle_expires_at ELSE @idle END WHERE id=@id AND generation=@generation AND account_epoch=@epoch AND revoked_at IS NULL AND account_epoch=(SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch') AND (expires_at IS NULL OR expires_at>@at) AND (idle_expires_at IS NULL OR idle_expires_at>@at) AND (absolute_expires_at IS NULL OR absolute_expires_at>@at)", new { id, generation, epoch, at = AuthTime.Format(now), idle = idleExpiry is null ? null : AuthTime.Format(idleExpiry.Value) }, transaction, cancellationToken: token)) == 1, cancellationToken);
    public async Task<bool> IsCurrentAsync(string id, long generation, long epoch, CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(id, cancellationToken);
        var now = clock.GetUtcNow();
        return row is not null && row.Generation == generation && row.AccountEpoch == epoch && row.RevokedAt is null &&
            !AuthTime.Expired(row.ExpiresAt, now) && !AuthTime.Expired(row.IdleExpiresAt, now) && !AuthTime.Expired(row.AbsoluteExpiresAt, now) &&
            await GetEpochAsync(cancellationToken) == epoch;
    }
    public async Task<bool> RevokeAsync(string id, string? kind = null, CancellationToken cancellationToken = default) =>
        await store.QueueWriteAsync(async (connection, transaction, token) =>
            await connection.ExecuteAsync(new CommandDefinition("UPDATE credentials SET generation=generation+1, revoked_at=@now WHERE id=@id AND (@kind IS NULL OR kind=@kind) AND revoked_at IS NULL", new { id, kind, now = AuthTime.Format(clock.GetUtcNow()) }, transaction, cancellationToken: token)) == 1, cancellationToken);
    public async Task<long> BumpEpochAsync(CancellationToken cancellationToken = default) =>
        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='account_epoch'", transaction: transaction, cancellationToken: token));
            return await connection.QuerySingleAsync<long>(new CommandDefinition("SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'", transaction: transaction, cancellationToken: token));
        }, cancellationToken);
    public async Task<IReadOnlyList<LoginAttempt>> LoginAttemptsAsync(string source, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken);
        return (await lease.Connection.QueryAsync<LoginAttempt>(new CommandDefinition("SELECT source Source, at At, success Success FROM login_attempts WHERE source=@source AND at>=@since ORDER BY at LIMIT 1000", new { source, since = AuthTime.Format(since) }, cancellationToken: cancellationToken))).AsList();
    }
    public async Task RecordLoginAsync(string source, DateTimeOffset at, bool success, CancellationToken cancellationToken = default) =>
        _ = await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM login_attempts WHERE at<@old", new { old = AuthTime.Format(at.AddDays(-1)) }, transaction, cancellationToken: token));
            return await connection.ExecuteAsync(new CommandDefinition("INSERT INTO login_attempts(source,at,success) VALUES(@source,@at,@success)", new { source, at = AuthTime.Format(at), success }, transaction, cancellationToken: token));
        }, cancellationToken);
}
