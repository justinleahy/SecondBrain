using SecondBrain.Storage.Auth;
using Xunit;

namespace SecondBrain.Storage.Tests;

/// <summary>Characterizes the password-policy read relocated from the daemon into Storage.</summary>
public sealed class SqlitePasswordPolicyStoreTests
{
    [Fact]
    public async Task MissingKeyReturnsNull()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();

        Assert.Null(await new SqlitePasswordPolicyStore(stores.State).ReadPasswordParametersJsonAsync());
    }

    [Fact]
    public async Task StoredJsonComesBackVerbatim()
    {
        const string json = "{ \"Version\":2,  \"MemoryKiB\":2048,\"Iterations\":4,\"Lanes\":2 , \"Extra\":\"kept\" }";
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, $"INSERT INTO meta(key, value) VALUES ('password_parameters', '{json}');");

        Assert.Equal(json, await new SqlitePasswordPolicyStore(stores.State).ReadPasswordParametersJsonAsync());
    }
}
