using Microsoft.Data.Sqlite;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Initialization;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class InitializerTests
{
    [Fact]
    public async Task CreatesBothStoresAndSeedsCallerComputedCredentialAndPassword()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        var request = Request();
        var result = await new StoreInitializer(stores.State, stores.Runner).InitializeAsync(request);
        Assert.True(result.Initialized);
        Assert.Equal(request.CredentialId, result.CredentialId);
        Assert.True(File.Exists(System.IO.Path.Combine(root.Path, "state", "state.db")));
        Assert.True(File.Exists(System.IO.Path.Combine(root.Path, "index", "index.db")));
        Assert.Equal("1", await MigrationTestSql.ScalarAsync(stores.State, "SELECT value FROM meta WHERE key = 'account_epoch';"));
        Assert.Equal("{}", await MigrationTestSql.ScalarAsync(stores.State, "SELECT value FROM meta WHERE key = 'generations_json';"));
        Assert.Equal("1", await MigrationTestSql.ScalarAsync(stores.State, "SELECT value FROM meta WHERE key = 'schema_version';"));
        Assert.Equal("UTC", await MigrationTestSql.ScalarAsync(stores.State, "SELECT value FROM meta WHERE key = 'time_zone';"));
        Assert.True(Ulid.TryParse((string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT value FROM meta WHERE key = 'instance_id';"))!, out _));
        Assert.Equal(request.Password.Hash, await MigrationTestSql.ScalarAsync(stores.State, "SELECT password_hash FROM account WHERE id = 1;"));
        Assert.Equal((long)request.Password.Version, await MigrationTestSql.ScalarAsync(stores.State, "SELECT password_version FROM account WHERE id = 1;"));
        Assert.Equal(Convert.ToBase64String(request.Verifier), await MigrationTestSql.ScalarAsync(stores.State, "SELECT verifier FROM credentials;"));
        Assert.Equal(request.Kid, await MigrationTestSql.ScalarAsync(stores.State, "SELECT kid FROM credentials;"));
        Assert.Equal("read,write,infer,admin", await MigrationTestSql.ScalarAsync(stores.State, "SELECT scopes FROM credentials;"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT generation FROM credentials;"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT account_epoch FROM credentials;"));
        Assert.Equal("api_key", await MigrationTestSql.ScalarAsync(stores.State, "SELECT kind FROM credentials;"));
    }

    [Fact]
    public async Task RepeatedInitializationPreservesExistingIdentityAndPassword()
    {
        using var root = new MigrationTestDataRoot();
        string originalInstance;
        object? originalUpdatedAt;
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await new StoreInitializer(stores.State, stores.Runner).InitializeAsync(Request());
            originalInstance = (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT value FROM meta WHERE key = 'instance_id';"))!;
            originalUpdatedAt = await MigrationTestSql.ScalarAsync(stores.State, "SELECT updated_at FROM account;");
        }

        await using var restarted = new MigrationTestStores(root.Path);
        var altered = Request() with { CredentialId = "new-id", Password = new PasswordHashRecord("replacement-hash", 2), TimeZone = "America/New_York" };
        var result = await new StoreInitializer(restarted.State, restarted.Runner).InitializeAsync(altered);
        Assert.False(result.Initialized);
        Assert.Equal("initial-id", result.CredentialId);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(restarted.State, "SELECT COUNT(*) FROM credentials;"));
        Assert.Equal(Request().Password.Hash, await MigrationTestSql.ScalarAsync(restarted.State, "SELECT password_hash FROM account;"));
        Assert.Equal(originalUpdatedAt, await MigrationTestSql.ScalarAsync(restarted.State, "SELECT updated_at FROM account;"));
        Assert.Equal(originalInstance, await MigrationTestSql.ScalarAsync(restarted.State, "SELECT value FROM meta WHERE key = 'instance_id';"));
        Assert.Equal("UTC", await MigrationTestSql.ScalarAsync(restarted.State, "SELECT value FROM meta WHERE key = 'time_zone';"));
    }

    [Fact]
    public async Task ConcurrentInitializationSeedsExactlyOneAccountAndCredential()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        var initializer = new StoreInitializer(stores.State, stores.Runner);
        var outcomes = await Task.WhenAll(initializer.InitializeAsync(Request()).AsTask(), initializer.InitializeAsync(Request() with { CredentialId = "other-id" }).AsTask());
        Assert.Single(outcomes, result => result.Initialized);
        Assert.Equal(outcomes[0].CredentialId, outcomes[1].CredentialId);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM account;"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM credentials;"));
    }

    [Fact]
    public async Task CredentialInsertFailureRollsBackAllBootstrapStateAndCanBeRetried()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "CREATE TRIGGER reject_bootstrap BEFORE INSERT ON credentials BEGIN SELECT RAISE(ABORT, 'injected constraint failure'); END;");
        var initializer = new StoreInitializer(stores.State, stores.Runner);
        await Assert.ThrowsAsync<SqliteException>(() => initializer.InitializeAsync(Request()).AsTask());
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM account;"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM credentials;"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM meta WHERE key IN ('account_epoch', 'instance_id', 'initial_admin_credential_id');"));
        await MigrationTestSql.WriteAsync(stores.State, "DROP TRIGGER reject_bootstrap;");
        Assert.True((await initializer.InitializeAsync(Request())).Initialized);
    }

    [Fact]
    public async Task BootstrapCannotOverwritePartialExistingCredentialState()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State,
            "INSERT INTO credentials(id,name,kind,verifier,scopes,kid,account_epoch,created_at) VALUES('prior','prior','api_key','verifier','admin','kid',1,'2026-10-08T00:00:00Z');");
        await Assert.ThrowsAsync<InvalidDataException>(() => new StoreInitializer(stores.State, stores.Runner).InitializeAsync(Request()).AsTask());
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM account;"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM credentials;"));
    }

    private static StoreInitializationRequest Request() => new("initial-id", Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(), "initial-kid",
        ["admin", "read", "write", "infer"], new PasswordHashRecord("$argon2id$v=19$caller-computed-parameters$caller-computed-hash", 1));
}
