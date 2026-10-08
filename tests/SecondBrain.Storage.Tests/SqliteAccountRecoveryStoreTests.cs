using SecondBrain.Core.Auth;
using SecondBrain.Storage.Auth;
using Xunit;

namespace SecondBrain.Storage.Tests;

/// <summary>Characterizes the brain init lookup and reset-password SQL relocated from the CLI into Storage.</summary>
public sealed class SqliteAccountRecoveryStoreTests
{
    private const string PolicyJson = "{\"Version\":1,\"MemoryKiB\":65536,\"Iterations\":3,\"Lanes\":1}";

    [Fact]
    public async Task LookupReturnsNullWhenNeitherTheMetaKeyNorAnAdminKeyExists()
    {
        await using var store = await RecoveryStore.CreateAsync();
        await store.Repository.AddAsync(Credential("read-key", "read", "2026-10-08T00:00:00Z"));
        await store.Repository.AddAsync(Credential("admin-session", "admin,read", "2026-10-08T00:00:00Z", kind: "session"));

        Assert.Null(await store.Recovery.FindInitialAdminCredentialIdAsync());
    }

    [Fact]
    public async Task LookupFallsBackToTheOldestAdminApiKey()
    {
        await using var store = await RecoveryStore.CreateAsync();
        await store.Repository.AddAsync(Credential("newer-admin", "admin", "2026-10-08T00:00:02Z"));
        await store.Repository.AddAsync(Credential("b-admin", "read,admin,write", "2026-10-08T00:00:01Z"));
        await store.Repository.AddAsync(Credential("a-admin", "admin,read", "2026-10-08T00:00:01Z"));
        await store.Repository.AddAsync(Credential("oldest-read", "read,administrator", "2026-10-08T00:00:00Z"));

        Assert.Equal("a-admin", await store.Recovery.FindInitialAdminCredentialIdAsync());
    }

    [Fact]
    public async Task LookupPrefersTheRecordedInitialAdminCredentialId()
    {
        await using var store = await RecoveryStore.CreateAsync();
        await store.Repository.AddAsync(Credential("oldest-admin", "admin", "2026-10-08T00:00:00Z"));
        await MigrationTestSql.WriteAsync(store.State, "INSERT INTO meta(key, value) VALUES ('initial_admin_credential_id', 'recorded-id');");

        Assert.Equal("recorded-id", await store.Recovery.FindInitialAdminCredentialIdAsync());
    }

    [Fact]
    public async Task ResetUpdatesTheAccountUpsertsThePolicyAndAdvancesTheEpoch()
    {
        await using var store = await RecoveryStore.CreateAsync();
        await store.Repository.SaveAccountAsync(new AccountRecord { PasswordHash = "old-hash", PasswordVersion = 1, UpdatedAt = "2026-10-08T00:00:00Z" });
        await MigrationTestSql.WriteAsync(store.State, "INSERT INTO meta(key, value) VALUES ('password_parameters', 'old-policy');");

        var epoch = await store.Recovery.ResetPasswordAsync(
            new AccountRecord { PasswordHash = "new-hash", PasswordVersion = 2, UpdatedAt = "2026-10-08T01:00:00Z" }, PolicyJson);

        Assert.Equal(2, epoch);
        Assert.Equal("2", await MigrationTestSql.ScalarAsync(store.State, "SELECT value FROM meta WHERE key='account_epoch';"));
        Assert.Equal(PolicyJson, await MigrationTestSql.ScalarAsync(store.State, "SELECT value FROM meta WHERE key='password_parameters';"));
        var account = await store.Repository.GetAccountAsync();
        Assert.NotNull(account);
        Assert.Equal((1, "new-hash", 2, "2026-10-08T01:00:00Z"), (account.Id, account.PasswordHash, account.PasswordVersion, account.UpdatedAt));

        Assert.Equal(3, await store.Recovery.ResetPasswordAsync(
            new AccountRecord { PasswordHash = "third-hash", PasswordVersion = 3, UpdatedAt = "2026-10-08T02:00:00Z" }, "{}"));
        Assert.Equal("{}", await MigrationTestSql.ScalarAsync(store.State, "SELECT value FROM meta WHERE key='password_parameters';"));
    }

    [Fact]
    public async Task ResetWithoutAnAccountThrowsAndLeavesMetaUnchanged()
    {
        await using var store = await RecoveryStore.CreateAsync();
        var before = await MigrationTestSql.FirstColumnAsync(store.State, "SELECT key || '=' || value FROM meta ORDER BY key;");

        var error = await Assert.ThrowsAsync<AccountNotInitializedException>(() => store.Recovery.ResetPasswordAsync(
            new AccountRecord { PasswordHash = "new-hash", PasswordVersion = 2, UpdatedAt = "2026-10-08T01:00:00Z" }, PolicyJson));

        Assert.Equal("The account is not initialized.", error.Message);
        Assert.Equal(before, await MigrationTestSql.FirstColumnAsync(store.State, "SELECT key || '=' || value FROM meta ORDER BY key;"));
        Assert.Null(await MigrationTestSql.ScalarAsync(store.State, "SELECT value FROM meta WHERE key='password_parameters';"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(store.State, "SELECT COUNT(*) FROM account;"));
    }

    private static CredentialRecord Credential(string id, string scopes, string createdAt, string kind = "api_key") => new()
    {
        Id = id, Name = id, Kind = kind, Verifier = "verifier-" + id, Scopes = scopes, Kid = "kid-1", AccountEpoch = 1, CreatedAt = createdAt,
    };

    private sealed class RecoveryStore : IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root;
        private readonly MigrationTestStores stores;

        private RecoveryStore(MigrationTestDataRoot root, MigrationTestStores stores)
        {
            this.root = root;
            this.stores = stores;
            Recovery = new SqliteAccountRecoveryStore(stores.State);
            Repository = new AuthRepository(stores.State, TimeProvider.System);
        }

        public SqliteAccountRecoveryStore Recovery { get; }
        public AuthRepository Repository { get; }
        public SqliteStateStore State => stores.State;

        public static async Task<RecoveryStore> CreateAsync()
        {
            var root = new MigrationTestDataRoot();
            var stores = new MigrationTestStores(root.Path);
            await stores.Runner.MigrateAsync();
            await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key, value) VALUES ('account_epoch', '1') ON CONFLICT(key) DO NOTHING;");
            return new RecoveryStore(root, stores);
        }

        public async ValueTask DisposeAsync()
        {
            await stores.DisposeAsync();
            root.Dispose();
        }
    }
}
