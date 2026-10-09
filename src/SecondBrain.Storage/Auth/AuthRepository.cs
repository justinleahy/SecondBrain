using Dapper;
using System.Data;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Storage;

namespace SecondBrain.Storage.Auth;

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
    public async Task<CredentialPage> ListPageAsync(string kind, bool activeOnly, CredentialCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        // Activity is filtered before the limit, against the epoch read in the same statement; AuthTime's fixed-width UTC text orders correctly.
        const string Active = "revoked_at IS NULL AND account_epoch=(SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch') AND (expires_at IS NULL OR expires_at>@now) AND (idle_expires_at IS NULL OR idle_expires_at>@now) AND (absolute_expires_at IS NULL OR absolute_expires_at>@now)";
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken);
        var rows = (await lease.Connection.QueryAsync<CredentialRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM credentials WHERE kind=@kind AND (@afterCreated IS NULL OR created_at>@afterCreated OR (created_at=@afterCreated AND id>@afterId)){(activeOnly ? " AND " + Active : "")} ORDER BY created_at,id LIMIT @take",
            new { kind, afterCreated = after?.CreatedAt, afterId = after?.Id, now = AuthTime.Format(clock.GetUtcNow()), take = limit + 1 },
            cancellationToken: cancellationToken))).AsList();
        if (rows.Count <= limit) return new(rows, null);
        rows.RemoveAt(limit);
        return new(rows, new(rows[^1].CreatedAt, rows[^1].Id));
    }
    public async Task<string> GetInstanceIdAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken);
        return await lease.Connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition("SELECT value FROM meta WHERE key='instance_id'", cancellationToken: cancellationToken))
            ?? throw new InvalidDataException("The state store has no instance id; it has not been initialized.");
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
    private async Task ValidateActorAsync(IDbConnection connection, IDbTransaction transaction, AuthenticatedCredential actor,
        bool sessionOnly, bool admin, bool stepUp, TimeSpan stepUpWindow, CancellationToken token)
    {
        var row = await connection.QuerySingleOrDefaultAsync<CredentialRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM credentials WHERE id=@Id AND account_epoch=(SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch')",
            new { actor.Id }, transaction, cancellationToken: token));
        var now = clock.GetUtcNow();
        if (row is null || row.Generation != actor.Generation || row.AccountEpoch != actor.AccountEpoch || row.Kind != actor.Kind ||
            row.RevokedAt is not null || AuthTime.Expired(row.ExpiresAt, now) || AuthTime.Expired(row.IdleExpiresAt, now) || AuthTime.Expired(row.AbsoluteExpiresAt, now) ||
            row.Kind is not ("session" or "api_key") || sessionOnly && row.Kind != "session" || admin && !row.GrantedScopes.Contains(Scope.Admin) ||
            stepUp && row.Kind == "session" && (row.SteppedUpAt is null || AuthTime.Parse(row.SteppedUpAt).Add(stepUpWindow) <= now))
            throw new AuthorityChangedException();
    }
    public async Task AddAuthorizedAsync(CredentialRecord credential, AuthenticatedCredential actor, TimeSpan stepUpWindow, CancellationToken cancellationToken = default) =>
        _ = await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await ValidateActorAsync(connection, transaction, actor, false, true, true, stepUpWindow, token);
            if (credential.Kind != "api_key" || credential.AccountEpoch != actor.AccountEpoch) throw new AuthorityChangedException();
            return await connection.ExecuteAsync(new CommandDefinition(Insert, credential, transaction, cancellationToken: token));
        }, cancellationToken);
    public async Task<bool> RevokeAuthorizedAsync(string id, string kind, AuthenticatedCredential actor, TimeSpan stepUpWindow, CancellationToken cancellationToken = default) =>
        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            if (kind is not ("session" or "api_key")) throw new AuthorityChangedException();
            await ValidateActorAsync(connection, transaction, actor, kind == "session", kind == "api_key", kind == "api_key" || id != actor.Id, stepUpWindow, token);
            return await connection.ExecuteAsync(new CommandDefinition("UPDATE credentials SET generation=generation+1,revoked_at=@now WHERE id=@id AND kind=@kind AND revoked_at IS NULL",
                new { id, kind, now = AuthTime.Format(clock.GetUtcNow()) }, transaction, cancellationToken: token)) == 1;
        }, cancellationToken);
    public async Task<long> BumpEpochAuthorizedAsync(AuthenticatedCredential actor, TimeSpan stepUpWindow, CancellationToken cancellationToken = default) =>
        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await ValidateActorAsync(connection, transaction, actor, true, false, true, stepUpWindow, token);
            await connection.ExecuteAsync(new CommandDefinition("UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='account_epoch'", transaction: transaction, cancellationToken: token));
            return actor.AccountEpoch + 1;
        }, cancellationToken);
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
            if (replacement.AccountEpoch != epoch || replacement.Kind != "session") return false;
            var now = AuthTime.Format(clock.GetUtcNow());
            var changed = await connection.ExecuteAsync(new CommandDefinition("UPDATE credentials SET generation=generation+1,revoked_at=@now WHERE id=@previousId AND kind='session' AND (expires_at IS NULL OR expires_at>@now) AND generation=@generation AND account_epoch=@epoch AND revoked_at IS NULL AND account_epoch=(SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch') AND (idle_expires_at IS NULL OR idle_expires_at>@now) AND (absolute_expires_at IS NULL OR absolute_expires_at>@now)", new { now, previousId, generation, epoch }, transaction, cancellationToken: token));
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
    public async Task<IReadOnlyList<LoginAttempt>> LoginAttemptsAsync(string source, DateTimeOffset since, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        // (at, rowid) orders attempts with identical timestamps by insertion, so a burst of equal-time successes cannot hide later failures.
        const string Sql = """
            WITH latest AS (SELECT at, rowid seq FROM login_attempts WHERE source=@source AND at>=@since AND success=1 ORDER BY at DESC, rowid DESC LIMIT 1)
            SELECT source Source, at At, success Success FROM (
              SELECT source, at, success, rowid seq FROM login_attempts
              WHERE source=@source AND at>=@since AND (NOT EXISTS (SELECT 1 FROM latest)
                OR at>(SELECT at FROM latest) OR (at=(SELECT at FROM latest) AND rowid>=(SELECT seq FROM latest)))
              ORDER BY at DESC, rowid DESC LIMIT @limit)
            ORDER BY at, seq
            """;
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken);
        return (await lease.Connection.QueryAsync<LoginAttempt>(new CommandDefinition(Sql, new { source, since = AuthTime.Format(since), limit }, cancellationToken: cancellationToken))).AsList();
    }
    public async Task RecordLoginAsync(string source, DateTimeOffset at, bool success, CancellationToken cancellationToken = default) =>
        _ = await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM login_attempts WHERE at<@old", new { old = AuthTime.Format(at.AddDays(-1)) }, transaction, cancellationToken: token));
            return await connection.ExecuteAsync(new CommandDefinition("INSERT INTO login_attempts(source,at,success) VALUES(@source,@at,@success)", new { source, at = AuthTime.Format(at), success }, transaction, cancellationToken: token));
        }, cancellationToken);
}
