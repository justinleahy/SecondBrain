using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Operations;

namespace SecondBrain.Server.Ingestion;

/// <summary>Runs bounded metadata cleanup hourly; document/file expiry has its own durable lifecycle.</summary>
public sealed class MetadataRetentionOrchestrator(IMetadataRetention retention, IDocumentRetention documents, IBackupOperations backups, IOperationCleanup cleanup, TimeProvider clock,
    ILogger<MetadataRetentionOrchestrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromHours(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
                for (var batch = 0; batch < 16; batch++)
                {
                    var result = await retention.RunAsync(cancellationToken: stoppingToken);
                    var expired = await documents.RunAsync(cancellationToken: stoppingToken);
                    var archives = await backups.RotateAsync(token: stoppingToken);
                    var operations = await cleanup.RunAsync(token: stoppingToken);
                    logger.LogInformation("Metadata retention: {Proposals} proposals, {Payloads} orphan payloads, {Traces} traces removed",
                        result.PrunedProposals, result.OrphanPayloads, result.RetrievalTraces);
                    logger.LogInformation("Document retention: {Purged} expiry operations completed, {Deferred} deferred", expired.Purged, expired.Deferred);
                    logger.LogInformation("Backup retention: {Deleted} archives removed, {Deferred} deferred", archives.Deleted, archives.Deferred);
                    logger.LogInformation("Operation cleanup: {Workspaces} workspaces and {DebugLogs} debug logs removed, {Deferred} deferred",
                        operations.Workspaces, operations.DebugLogs, operations.Deferred);
                    delay = result.MoreWork || archives.MoreWork || operations.MoreWork || expired.MoreWork && expired.Deferred == 0 ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1);
                    if (expired.Deferred > 0 || archives.Deferred > 0 || operations.Deferred > 0
                        || !result.MoreWork && !expired.MoreWork && !archives.MoreWork && !operations.MoreWork) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Data.Common.DbException)
            { delay = TimeSpan.FromMinutes(1); logger.LogWarning("Metadata retention remains pending and will retry at its next scheduled run."); }
        }
    }
}
