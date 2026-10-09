using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Limits;
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

    [Fact]
    public async Task StoreHandlesAreSingleUndecoratedInstances()
    {
        // PublicationCoordinator and MutationJournal key static locks by store instance.
        var root = Path.Combine(Path.GetTempPath(), "secondbrain-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = Provider(root);
            var state = provider.GetRequiredService<IStateStore>();
            Assert.Same(state, provider.GetRequiredService<IStateStore>());
            Assert.Same(provider.GetRequiredService<SqliteStateStore>(), state);
            var index = provider.GetRequiredService<IIndexStore>();
            Assert.Same(index, provider.GetRequiredService<IIndexStore>());
            Assert.Same(provider.GetRequiredService<SqliteIndexStore>(), index);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MigrationStartupUsesTheRegisteredDataRootDiskProbe()
    {
        var root = Path.Combine(Path.GetTempPath(), "secondbrain-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var disk = new MigrationTestDisk(0);
            await using (var provider = Provider(root, disk))
            {
                var error = await Assert.ThrowsAsync<IOException>(() => Assert.Single(provider.GetServices<IHostedService>()).StartAsync(default));
                Assert.Contains("disk preflight", error.Message, StringComparison.Ordinal);
                Assert.False(provider.GetRequiredService<IStorageStatus>().Ready);
            }
            Assert.Equal(new[] { Path.GetFullPath(root) }, disk.Queried);

            // Composition without a probe cannot silently measure some other filesystem.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<SecondBrainOptions>>(Options.Create(new SecondBrainOptions { DataRoot = root }));
            services.AddSecondBrainStorage();
            await using var unprobed = services.BuildServiceProvider();
            Assert.Throws<InvalidOperationException>(() => unprobed.GetRequiredService<IMigrationRunner>());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ServiceProvider Provider(string root, IDiskCapacity? disk = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<SecondBrainOptions>>(Options.Create(new SecondBrainOptions { DataRoot = root }));
        services.AddSingleton(disk ?? new MigrationTestDisk(1L << 40));
        services.AddSecondBrainStorage();
        services.AddSecondBrainStorage();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
