using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;

namespace SecondBrain.Server.Ingestion;

/// <summary>Serializes index inference and resumes persistent pending work after restart.</summary>
public sealed class EmbeddingOrchestrator(EmbeddingProcessor processor, IDocumentRepository documents, IPersistentAdmissionAccounting accounting, IOptionsMonitor<SecondBrainOptions> options, ILogger<EmbeddingOrchestrator> logger) : BackgroundService
{
    private readonly SemaphoreSlim processing = new(1, 1);

    public async Task<CaptureResult> TryCompleteAsync(CaptureResult capture, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(options.CurrentValue.Ingest.SyncEmbedDeadlineMs));
        try
        {
            await processing.WaitAsync(deadline.Token);
            try { await processor.ProcessAsync(deadline.Token); }
            finally { processing.Release(); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (IsProviderFailure(ex)) { }
        await accounting.RefreshAsync(token);
        var document = await documents.GetAsync(capture.Document.Id, true, token) ?? capture.Document;
        var job = capture.Job is null ? null : await documents.JobAsync(capture.Job.Id, token) ?? capture.Job;
        return capture with { Document = document, Job = job, Vector = document.Status == "indexed" ? "live" : "pending" };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processing.WaitAsync(stoppingToken);
                try { await processor.ProcessAsync(stoppingToken); await accounting.RefreshAsync(stoppingToken); }
                finally { processing.Release(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (IsProviderFailure(ex)) { logger.LogWarning("Embedding work remains pending; provider unavailable or response invalid."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
    public static bool IsProviderFailure(Exception exception) => exception is HttpRequestException or PrivacyPolicyException or InvalidOperationException or ArgumentException;
}
