using System.Data;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Storage.Auth;

/// <summary>Revalidates an interactive initiator inside the transaction admitting its effect.</summary>
public static class IngestionAuthorityValidator
{
    public static async Task<CredentialRecord> ValidateAsync(IDbConnection connection, IDbTransaction transaction,
        IngestionAuthority authority, Scope requiredScope, DateTimeOffset now, bool sessionOnly = false,
        TimeSpan? stepUpWindow = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(authority);
        if (stepUpWindow is { } window && window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stepUpWindow));
        var row = await connection.QuerySingleOrDefaultAsync<CredentialRecord>(new CommandDefinition(
            """
            SELECT id Id,name Name,kind Kind,scopes Scopes,generation Generation,account_epoch AccountEpoch,
              idle_expires_at IdleExpiresAt,absolute_expires_at AbsoluteExpiresAt,expires_at ExpiresAt,
              stepped_up_at SteppedUpAt,revoked_at RevokedAt
            FROM credentials
            WHERE id=@Id AND account_epoch=(SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch')
            """, new { Id = authority.CredentialId }, transaction, cancellationToken: cancellationToken));
        if (row is null || row.Kind is not ("session" or "api_key") || row.Kind != authority.Kind ||
            row.Generation != authority.CredentialGeneration || row.AccountEpoch != authority.AccountEpoch ||
            row.RevokedAt is not null || AuthTime.Expired(row.ExpiresAt, now) ||
            AuthTime.Expired(row.IdleExpiresAt, now) || AuthTime.Expired(row.AbsoluteExpiresAt, now) ||
            !row.GrantedScopes.Contains(requiredScope) || sessionOnly && row.Kind != "session")
            throw new AuthorityChangedException();
        // A requested step-up always means an interactive session. Keys cannot acquire this
        // property through admin or an invented stepped_up_at value.
        if (stepUpWindow is { } duration && (row.Kind != "session" || row.SteppedUpAt is null ||
            AuthTime.Parse(row.SteppedUpAt) > now || AuthTime.Parse(row.SteppedUpAt).Add(duration) <= now))
            throw new AuthorityChangedException();
        return row;
    }
}
