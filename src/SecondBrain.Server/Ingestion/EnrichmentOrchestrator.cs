using SecondBrain.Core.Ingestion;

namespace SecondBrain.Server.Ingestion;

public sealed class EnrichmentOrchestrator(EnrichmentProcessor processor, EnrichmentBatchProcessor batches, SecondBrain.Core.Limits.IPersistentAdmissionAccounting accounting,
    ILogger<EnrichmentOrchestrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (await batches.ProcessAsync(stoppingToken) || await processor.ProcessAsync(stoppingToken)) { await accounting.RefreshAsync(stoppingToken); continue; } }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or IngestionConflictException)
            { logger.LogWarning("Enrichment remains pending; processing will retry."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
