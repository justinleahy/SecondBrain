using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Initialization;
using SecondBrain.Storage.Migrations;

namespace SecondBrain.Storage;

/// <summary>Shared daemon/CLI composition for the configured data root.</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers stores and ordered migration/journal/publication startup. The host takes its data-root lock first
    /// and must register a mount-aware <see cref="IDiskCapacity"/> for the migration disk preflight.
    /// </summary>
    public static IServiceCollection AddSecondBrainStorage(this IServiceCollection services)
    {
        services.TryAddSingleton(new SqliteStoreOptions());
        services.TryAddSingleton<ICrashPoints, NoOpCrashPoints>();
        services.TryAddSingleton(provider => new SqliteStateStore(
            provider.GetRequiredService<IOptions<SecondBrainOptions>>().Value.DataRoot,
            provider.GetRequiredService<SqliteStoreOptions>()));
        services.TryAddSingleton(provider => new SqliteIndexStore(
            provider.GetRequiredService<IOptions<SecondBrainOptions>>().Value.DataRoot,
            provider.GetRequiredService<SqliteStoreOptions>()));
        services.TryAddSingleton<IStateStore>(provider => provider.GetRequiredService<SqliteStateStore>());
        services.TryAddSingleton<IIndexStore>(provider => provider.GetRequiredService<SqliteIndexStore>());
        services.TryAddSingleton<IStoreSnapshot>(provider => provider.GetRequiredService<SqliteStateStore>());
        services.TryAddSingleton<IMigrationRunner>(provider => new MigrationRunner(
            provider.GetRequiredService<IStateStore>(), provider.GetRequiredService<IIndexStore>(),
            provider.GetRequiredService<IOptions<SecondBrainOptions>>().Value.DataRoot,
            provider.GetRequiredService<IDiskCapacity>(),
            provider.GetRequiredService<ICrashPoints>()));
        services.TryAddSingleton<IStoreInitializer, StoreInitializer>();
        services.TryAddSingleton<IMutationJournal>(provider => new MutationJournal(
            provider.GetRequiredService<IStateStore>(),
            provider.GetRequiredService<IOptions<SecondBrainOptions>>().Value.DataRoot,
            provider.GetRequiredService<ICrashPoints>()));
        services.TryAddSingleton<IPublicationCoordinator, PublicationCoordinator>();
        services.TryAddSingleton<StorageStartupService>();
        services.TryAddSingleton<IStorageStatus>(provider => provider.GetRequiredService<StorageStartupService>());
        services.AddHostedService(provider => provider.GetRequiredService<StorageStartupService>());
        return services;
    }
}

/// <summary>Runs after the host's exclusive lock service and before HTTP begins accepting requests.</summary>
public sealed class StorageStartupService(IServiceProvider services, ILogger<StorageStartupService> logger) : IHostedService, IStorageStatus
{
    private int ready;
    public bool Ready => Volatile.Read(ref ready) == 1;
    public MigrationReport? LastMigration { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolve stores only at startup, after earlier hosted services have acquired the root lock.
        LastMigration = await services.GetRequiredService<IMigrationRunner>().MigrateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var recovery in LastMigration.Recoveries)
            logger.LogWarning("Migration recovered for {Store} version {Version}: {Detail}", recovery.Store, recovery.Version, recovery.Detail);
        var journal = await services.GetRequiredService<IMutationJournal>().RecoverAsync(cancellationToken).ConfigureAwait(false);
        var publications = await services.GetRequiredService<IPublicationCoordinator>().RecoverAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Storage recovered: {Finalized} finalized mutations, {Discarded} discarded preparations, {Publications} completed publications", journal.Finalized, journal.Discarded, publications);
        Volatile.Write(ref ready, 1);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref ready, 0);
        return Task.CompletedTask;
    }
}
