using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Storage;

namespace SecondBrain.Storage.Auth;

/// <summary>The SQL behind brain init's existing-account lookup and brain init --reset-password.</summary>
public sealed class SqliteAccountRecoveryStore(IStateStore state) : IAccountRecoveryStore
{
    public async Task<string?> FindInitialAdminCredentialIdAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        var existingId = await lease.Connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT value FROM meta WHERE key='initial_admin_credential_id'", cancellationToken: cancellationToken));
        existingId ??= await lease.Connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT id FROM credentials WHERE kind='api_key' AND instr(',' || scopes || ',', ',admin,')>0 ORDER BY created_at,id LIMIT 1", cancellationToken: cancellationToken));
        return existingId;
    }

    public async Task<long> ResetPasswordAsync(AccountRecord account, string passwordParametersJson, CancellationToken cancellationToken = default) =>
        // Password, deployment policy and epoch are one transaction: no stale credential survives recovery.
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE account SET password_hash=@PasswordHash,password_version=@PasswordVersion,updated_at=@UpdatedAt WHERE id=1", account, transaction, cancellationToken: token));
            if (changed != 1) throw new AccountNotInitializedException();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO meta(key,value) VALUES('password_parameters',@policy) ON CONFLICT(key) DO UPDATE SET value=excluded.value; UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='account_epoch'",
                new { policy = passwordParametersJson }, transaction, cancellationToken: token));
            return await connection.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'", transaction: transaction, cancellationToken: token));
        }, cancellationToken);
}
