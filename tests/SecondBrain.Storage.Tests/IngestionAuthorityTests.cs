using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class IngestionAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("UPDATE credentials SET generation=2")]
    [InlineData("UPDATE credentials SET revoked_at='2026-10-09T11:00:00.0000000+00:00'")]
    [InlineData("UPDATE meta SET value='2' WHERE key='account_epoch'")]
    [InlineData("UPDATE credentials SET scopes='read'")]
    [InlineData("UPDATE credentials SET scopes='admin'")]
    [InlineData("UPDATE credentials SET kind='session'")]
    [InlineData("UPDATE credentials SET expires_at='2026-10-09T12:00:00.0000000+00:00'")]
    [InlineData("UPDATE credentials SET idle_expires_at='2026-10-09T12:00:00.0000000+00:00'")]
    [InlineData("UPDATE credentials SET absolute_expires_at='2026-10-09T12:00:00.0000000+00:00'")]
    [InlineData("DELETE FROM credentials")]
    public async Task ChangedAuthorityRejectsTheWholeAdmissionTransaction(string change)
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        await MigrationTestSql.WriteAsync(stores.State, change);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => stores.State.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "INSERT INTO meta(key,value) VALUES('admission-effect','must-rollback')";
            await write.ExecuteNonQueryAsync(token);
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction, Actor(), Scope.Write, Now, cancellationToken: token);
            return true;
        }).AsTask());
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM meta WHERE key='admission-effect'"));
    }

    [Fact]
    public async Task ValidAdmissionUsesAuthoritativeNameAndIndependentScopes()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var record = await stores.State.QueueWriteAsync((connection, transaction, token) =>
            new ValueTask<CredentialRecord>(IngestionAuthorityValidator.ValidateAsync(connection, transaction, Actor(), Scope.Write, Now, cancellationToken: token)));
        Assert.Equal("Actual key name", record.Name);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => stores.State.QueueWriteAsync((connection, transaction, token) =>
            new ValueTask<CredentialRecord>(IngestionAuthorityValidator.ValidateAsync(connection, transaction, Actor(), Scope.Admin, Now, cancellationToken: token))).AsTask());
    }

    [Theory]
    [InlineData("api_key", "2026-10-09T11:59:00.0000000+00:00", false)]
    [InlineData("session", null, false)]
    [InlineData("session", "2026-10-09T11:55:00.0000000+00:00", false)]
    [InlineData("session", "2026-10-09T12:01:00.0000000+00:00", false)]
    [InlineData("session", "2026-10-09T11:59:00.0000000+00:00", true)]
    public async Task PurgeRequiresCurrentInteractiveStepUp(string kind, string? steppedUp, bool accepted)
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores, kind, steppedUp);
        async Task Validate() => _ = await stores.State.QueueWriteAsync((connection, transaction, token) =>
            new ValueTask<CredentialRecord>(IngestionAuthorityValidator.ValidateAsync(connection, transaction, Actor(kind), Scope.Write, Now,
                sessionOnly: true, stepUpWindow: TimeSpan.FromMinutes(5), cancellationToken: token)));
        if (accepted) await Validate();
        else await Assert.ThrowsAsync<AuthorityChangedException>(Validate);
    }

    private static IngestionAuthority Actor(string kind = "api_key") => new("actor", 1, 1, "Untrusted stale name", kind);

    private static async Task SeedAsync(MigrationTestStores stores, string kind = "api_key", string? steppedUp = null)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord
        {
            Id = "actor", Name = "Actual key name", Kind = kind, Generation = 1, AccountEpoch = 1,
            Scopes = "read,write", CreatedAt = AuthTime.Format(Now.AddDays(-1)), Verifier = "fixture", Kid = "fixture",
            SteppedUpAt = steppedUp,
        });
    }
}
