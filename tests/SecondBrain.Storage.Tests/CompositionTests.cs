using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Storage;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class CompositionTests
{
    [Fact]
    public async Task ConfiguredRootServicesStartOnlyAfterRegistrationAndRecoverOnRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "secondbrain-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var provider = Provider(root))
            {
                var startup = Assert.Single(provider.GetServices<IHostedService>());
                var status = provider.GetRequiredService<IStorageStatus>();
                Assert.False(status.Ready);
                Assert.False(Directory.Exists(root));
                await startup.StartAsync(default);
                Assert.True(status.Ready);
                Assert.Equal(1, status.LastMigration!.StateVersion);
                Assert.Equal(Path.Combine(root, "state", "state.db"), provider.GetRequiredService<SqliteStateStore>().DatabasePath);
                Assert.Equal(Path.Combine(root, "index", "index.db"), provider.GetRequiredService<SqliteIndexStore>().DatabasePath);
                Assert.Same(provider.GetRequiredService<IStateStore>(), provider.GetRequiredService<IStoreSnapshot>());
                Assert.NotNull(provider.GetRequiredService<IStoreInitializer>());
                Assert.NotNull(provider.GetRequiredService<IPublicationCoordinator>());
                var destination = Path.Combine(root, "note.md");
                await provider.GetRequiredService<IMutationJournal>().PrepareAsync(new MutationWriteRequest
                {
                    MutationId = "restart", Kind = "import", OperationId = "one", Destination = destination, Content = "durable"u8.ToArray()
                });
                await provider.GetRequiredService<IMutationJournal>().ApplyAsync("restart");
                await startup.StopAsync(default);
                Assert.False(status.Ready);
            }
            await using (var provider = Provider(root))
            {
                await Assert.Single(provider.GetServices<IHostedService>()).StartAsync(default);
                Assert.True(provider.GetRequiredService<IStorageStatus>().Ready);
                var state = provider.GetRequiredService<IStateStore>();
                await using var lease = await state.OpenReadConnectionAsync();
                using var lookup = lease.Connection.CreateCommand();
                lookup.CommandText = "SELECT status FROM mutations WHERE id='restart';";
                Assert.Equal("finalized", await lookup.ExecuteScalarAsync());
                Assert.Equal(new MutationRecoveryReport(0, 0, 0, 0), await provider.GetRequiredService<IMutationJournal>().RecoverAsync());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ServiceProvider Provider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<SecondBrainOptions>>(Options.Create(new SecondBrainOptions { DataRoot = root }));
        services.AddSecondBrainStorage();
        services.AddSecondBrainStorage();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
