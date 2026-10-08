using System.Globalization;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Migrations;

namespace SecondBrain.Storage.Initialization;

/// <summary>Bootstrap persistence, independent of credential generation and password hashing.</summary>
public sealed class StoreInitializer(IStateStore state, IMigrationRunner migrations) : IStoreInitializer
{
    /// <inheritdoc />
    public async ValueTask<StoreInitializationResult> InitializeAsync(StoreInitializationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CredentialId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kid);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CredentialName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TimeZone);
        ArgumentNullException.ThrowIfNull(request.Password);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Password.Hash);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Password.Version, 1);
        ArgumentNullException.ThrowIfNull(request.Verifier);
        if (request.Verifier.Length != 32)
        {
            throw new ArgumentException("The admin verifier must be an already computed 32-byte HMAC-SHA-256 value.", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Scopes);
        var allowedScopes = new[] { "read", "write", "infer", "admin" };
        if (!request.Scopes.Contains("admin", StringComparer.Ordinal)
            || request.Scopes.Any(scope => !allowedScopes.Contains(scope, StringComparer.Ordinal)))
        {
            throw new ArgumentException("The first credential requires admin scope and only declared M0 scopes.", nameof(request));
        }

        var verifier = Convert.ToBase64String(request.Verifier);
        var scopes = string.Join(',', allowedScopes.Where(scope => request.Scopes.Contains(scope, StringComparer.Ordinal)));
        await migrations.MigrateAsync(cancellationToken).ConfigureAwait(false);
        return await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var accountExists = Convert.ToInt64(await MigrationSql.ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM account;", token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
            if (accountExists)
            {
                var initialId = await MigrationSql.ScalarAsync(connection, transaction,
                    "SELECT value FROM meta WHERE key = 'initial_admin_credential_id';", token).ConfigureAwait(false)
                    ?? await MigrationSql.ScalarAsync(connection, transaction,
                        "SELECT id FROM credentials WHERE kind = 'api_key' AND instr(',' || scopes || ',', ',admin,') > 0 ORDER BY created_at, id LIMIT 1;", token).ConfigureAwait(false);
                if (initialId is not string existingId)
                {
                    throw new InvalidDataException("The account exists without an initial admin credential; bootstrap will not overwrite existing state.");
                }

                return new StoreInitializationResult(false, existingId);
            }

            var credentialsExist = Convert.ToInt64(await MigrationSql.ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM credentials;", token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
            if (credentialsExist)
            {
                throw new InvalidDataException("Credentials exist without an account; bootstrap will not overwrite existing state.");
            }

            var at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await MigrationSql.ExecuteAsync(connection, transaction,
                "INSERT INTO meta(key, value) VALUES ('account_epoch', '1'), ('generations_json', '{}'), ('instance_id', $instance), ('time_zone', $zone), ('initial_admin_credential_id', $id) ON CONFLICT(key) DO NOTHING;", token,
                ("$instance", Ulid.NewUlid().ToString()), ("$zone", request.TimeZone), ("$id", request.CredentialId)).ConfigureAwait(false);
            var epoch = await MigrationSql.ScalarAsync(connection, transaction,
                "SELECT value FROM meta WHERE key = 'account_epoch';", token).ConfigureAwait(false);
            if (!string.Equals(Convert.ToString(epoch, CultureInfo.InvariantCulture), "1", StringComparison.Ordinal))
            {
                throw new InvalidDataException("An uninitialized account must start at account epoch 1.");
            }

            await MigrationSql.ExecuteAsync(connection, transaction,
                "INSERT INTO account(id, password_hash, password_version, updated_at) VALUES (1, $hash, $version, $at);", token,
                ("$hash", request.Password.Hash), ("$version", request.Password.Version), ("$at", at)).ConfigureAwait(false);
            await MigrationSql.ExecuteAsync(connection, transaction,
                "INSERT INTO credentials(id, name, kind, verifier, scopes, generation, kid, account_epoch, created_at) VALUES ($id, $name, 'api_key', $verifier, $scopes, 1, $kid, 1, $at);", token,
                ("$id", request.CredentialId), ("$name", request.CredentialName), ("$verifier", verifier), ("$scopes", scopes), ("$kid", request.Kid), ("$at", at)).ConfigureAwait(false);
            return new StoreInitializationResult(true, request.CredentialId);
        }, cancellationToken).ConfigureAwait(false);
    }
}
