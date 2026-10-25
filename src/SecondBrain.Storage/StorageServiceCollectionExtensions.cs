using SecondBrain.Core.Providers;
using SecondBrain.Storage.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Operations;
using SecondBrain.Storage.Backup;
using SecondBrain.Core.Retrieval;
using SecondBrain.Core.Sources;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Chat;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Initialization;
using SecondBrain.Storage.Migrations;
using SecondBrain.Storage.Retrieval;
using SecondBrain.Storage.Sources;

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
        services.TryAddSingleton(TimeProvider.System);
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
        services.TryAddSingleton<IBackupOperations>(provider => new BackupOperations(provider.GetRequiredService<IStateStore>(),
            provider.GetRequiredService<IIndexStore>(),provider.GetRequiredService<IStoreSnapshot>(),provider.GetRequiredService<IMutationJournal>(),
            provider.GetRequiredService<IBackupKeyProvider>(),provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>(),
            provider.GetRequiredService<IDiskCapacity>(),provider.GetRequiredService<TimeProvider>(),provider.GetService<RuntimeLocations>()));
        services.TryAddSingleton<IExportOperations>(provider => (IExportOperations)provider.GetRequiredService<IBackupOperations>());
        services.TryAddSingleton<IModelUsage>(provider => new SqliteModelUsage(provider.GetRequiredService<IStateStore>(),
            provider.GetRequiredService<IOptions<SecondBrainOptions>>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IOptionsMonitor<SecondBrainOptions>>()));
        services.TryAddSingleton<IMigrationBackup>(provider => (IMigrationBackup)provider.GetRequiredService<IBackupOperations>());
        services.TryAddSingleton<IOperationCleanup>(provider => new OperationCleanup(provider.GetRequiredService<IStateStore>(),
            provider.GetRequiredService<IOptions<SecondBrainOptions>>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IOptionsMonitor<SecondBrainOptions>>()));
        services.TryAddSingleton<IMigrationRunner>(provider => new MigrationRunner(
            provider.GetRequiredService<IStateStore>(), provider.GetRequiredService<IIndexStore>(),
            provider.GetRequiredService<IOptions<SecondBrainOptions>>().Value.DataRoot,
            provider.GetRequiredService<IDiskCapacity>(),
            provider.GetRequiredService<ICrashPoints>(), migrationBackup: () => provider.GetRequiredService<IMigrationBackup>()));
        services.TryAddSingleton<IStoreInitializer, StoreInitializer>();
        services.TryAddSingleton<IMutationJournal>(provider => new MutationJournal(
            provider.GetRequiredService<IStateStore>(),
            provider.GetRequiredService<IOptions<SecondBrainOptions>>().Value.DataRoot,
            provider.GetRequiredService<ICrashPoints>(), provider.GetService<IOptionsMonitor<SecondBrainOptions>>(), provider.GetService<TimeProvider>()));
        services.TryAddSingleton<IPublicationCoordinator, PublicationCoordinator>();
        services.TryAddSingleton<IPersistentAdmissionAccounting, PersistentAdmissionAccounting>();
        services.TryAddSingleton<IFolderDocumentRepository, SqliteFolderDocumentRepository>();
        services.TryAddSingleton<SqliteCalendarSubscriptionRepository>();
        services.TryAddSingleton<ICalendarSubscriptionRepository>(provider => provider.GetRequiredService<SqliteCalendarSubscriptionRepository>());
        services.TryAddSingleton<ICalendarFetchAuthority>(provider => provider.GetRequiredService<SqliteCalendarSubscriptionRepository>());
        services.TryAddSingleton<IEmbeddingWorkRepository, SqliteEmbeddingWorkRepository>();
        services.TryAddSingleton<IClassificationWorkRepository, SqliteClassificationWorkRepository>();
        services.TryAddSingleton<SqliteEnrichmentRepository>();
        services.TryAddSingleton<IEnrichmentRepository>(p => p.GetRequiredService<SqliteEnrichmentRepository>());
        services.TryAddSingleton<IEnrichmentBatchRepository>(p => p.GetRequiredService<SqliteEnrichmentRepository>());
        services.TryAddSingleton<IDocumentRepository, SqliteDocumentRepository>();
        services.TryAddSingleton<IChunkingWorkRepository, SqliteChunkingWorkRepository>();
        services.TryAddSingleton<IMetadataRetention, SqliteMetadataRetention>();
        // Managed files are supplied by the host; storage-only composition can still validate before they are registered.
        services.TryAddSingleton<IDocumentRetention>(provider => new SqliteDocumentRetention(
            provider.GetRequiredService<IStateStore>(), provider.GetRequiredService<IIndexStore>(),
            provider.GetRequiredService<IPublicationCoordinator>(), provider.GetRequiredService<IMutationJournal>(),
            provider.GetRequiredService<IManagedDocumentFiles>(), provider.GetRequiredService<ICalendarSubscriptionRepository>(),
            provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IDocumentCatalog>(provider => new SqliteDocumentCatalog(
            provider.GetRequiredService<IStateStore>(), provider.GetRequiredService<IPayloadProtection>(), provider.GetRequiredService<IIndexStore>()));
        services.TryAddSingleton<SqliteChatRepository>();
        services.TryAddSingleton<IChatRepository>(provider => provider.GetRequiredService<SqliteChatRepository>());
        services.TryAddSingleton<IChatProposalRepository>(provider => provider.GetRequiredService<SqliteChatRepository>());
        services.TryAddSingleton<IChatExecutionRepository>(provider => provider.GetRequiredService<SqliteChatRepository>());
        // A factory, because IPayloadProtection comes from the host's key-ring composition, not from Storage.
        services.TryAddSingleton<IRetrievalRepository>(provider => new SqliteRetrievalRepository(
            provider.GetRequiredService<IIndexStore>(), provider.GetRequiredService<IPayloadProtection>(),
            provider.GetService<IOptionsMonitor<SecondBrainOptions>>(), provider.GetRequiredService<IStateStore>(), provider.GetRequiredService<TimeProvider>()));
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
        while (await services.GetRequiredService<IOperationCleanup>().RunAsync(token: cancellationToken).ConfigureAwait(false) is { MoreWork: true }) { }
        // Resolve stores only at startup, after earlier hosted services have acquired the root lock.
        LastMigration = await services.GetRequiredService<IMigrationRunner>().MigrateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var recovery in LastMigration.Recoveries)
            logger.LogWarning("Migration recovered for {Store} version {Version}: {Detail}", recovery.Store, recovery.Version, recovery.Detail);
        await services.GetRequiredService<IStateStore>().QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var recovery = SqliteDocumentRepository.Command(connection, transaction,
                """
                UPDATE jobs SET status='queued',lease_until=NULL WHERE status='running' AND json_extract(payload_json,'$.batchId') IS NULL;
                UPDATE usage SET settled=1 WHERE settled=0 AND NOT EXISTS(
                  SELECT 1 FROM jobs j JOIN batch_jobs b ON b.id=json_extract(j.payload_json,'$.batchId')
                  WHERE j.id=usage.job_id AND b.status NOT IN ('done','failed','cancelled'));
                """);
            return await recovery.ExecuteNonQueryAsync(token);
        }, cancellationToken).ConfigureAwait(false);
        var journal = await services.GetRequiredService<IMutationJournal>().RecoverAsync(cancellationToken).ConfigureAwait(false);
        var publications = await services.GetRequiredService<IPublicationCoordinator>().RecoverAsync(cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<IChatRepository>().RecoverInterruptedAsync(cancellationToken).ConfigureAwait(false);
        var purges = await PurgeCleanup.RecoverAsync(services.GetRequiredService<IStateStore>(), services.GetRequiredService<IIndexStore>(), cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<ICalendarSubscriptionRepository>().RecoverPurgesAsync(cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<IFolderDocumentRepository>().RecoverPurgesAsync(cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<IChatProposalRepository>().RecoverApprovalsAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Storage recovered: {Finalized} finalized mutations, {Discarded} discarded preparations, {Publications} completed publications, {Purges} completed purge cleanups", journal.Finalized, journal.Discarded, publications, purges);
        if (services.GetService<IStagingRecovery>() is { } staging)
        {
            try { logger.LogInformation("Removed {Count} stale staging files", await staging.CleanStaleAsync(cancellationToken).ConfigureAwait(false)); }
            catch (IOException) { logger.LogWarning("Stale staging files could not be inspected; they will be retried at the next start."); }
        }
        await services.GetRequiredService<IPersistentAdmissionAccounting>().RefreshAsync(cancellationToken).ConfigureAwait(false);
        var activeSpace = await services.GetRequiredService<IEmbeddingWorkRepository>().ActiveAsync(cancellationToken).ConfigureAwait(false);
        if (activeSpace is not null)
        {
            var index = services.GetRequiredService<IIndexStore>();
            var vectors = VectorSnapshotRegistry.For(index);
            using var gate = await vectors.EnterAsync(cancellationToken).ConfigureAwait(false);
            await using var lease = index is SqliteStore sqlite
                ? await sqlite.OpenStartupReadConnectionAsync(cancellationToken).ConfigureAwait(false)
                : await index.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var vectorLoadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            using var loaded = await vectors.AcquireAsync(lease.Connection, transaction, activeSpace.Id, activeSpace.Dimensions, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Loaded {VectorCount} committed vectors before readiness: dimensions={Dimensions} elapsed_ms={ElapsedMilliseconds}",
                loaded.Count, activeSpace.Dimensions, System.Diagnostics.Stopwatch.GetElapsedTime(vectorLoadStarted).TotalMilliseconds);
        }
        Volatile.Write(ref ready, 1);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref ready, 0);
        return Task.CompletedTask;
    }
}
