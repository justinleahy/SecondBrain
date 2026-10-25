using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Server.Ingestion;

/// <summary>Converges existing projections on the configured profile, including after a restart.</summary>
public sealed class ChunkingOrchestrator(IChunkingWorkRepository work, IOptionsMonitor<SecondBrainOptions> options,
    ILogger<ChunkingOrchestrator> logger) : BackgroundService
{
    private readonly SemaphoreSlim changed = new(0, 1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = options.OnChange((_, _) => { try { changed.Release(); } catch (SemaphoreFullException) { } });
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await work.ProcessAsync(ChunkingProfile.FromOptions(options.CurrentValue.Chunking), cancellationToken: stoppingToken) > 0) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
            { logger.LogWarning("Chunking changes remain pending; processing will retry."); }
            // Wake immediately for settings edits. Occasional reconciliation also catches
            // work admitted under an earlier profile that finished after the last batch.
            await changed.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
