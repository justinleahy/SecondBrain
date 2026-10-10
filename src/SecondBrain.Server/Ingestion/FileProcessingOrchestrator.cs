using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;

namespace SecondBrain.Server.Ingestion;

/// <summary>Processes durable admitted originals independently of the upload request.</summary>
public sealed class FileProcessingOrchestrator(IDocumentRepository documents, IManagedDocumentFiles files,
    IExtractorClient extractor, IInboxClaims claims, EmbeddingOrchestrator embeddings, IPersistentAdmissionAccounting accounting, ILogger<FileProcessingOrchestrator> logger) : BackgroundService
{
    private readonly SemaphoreSlim wake = new(0, 4);

    public void Notify()
    {
        try { wake.Release(); } catch (SemaphoreFullException) { }
    }
    public async Task<CaptureResult> WaitAsync(CaptureResult admitted, int seconds, CancellationToken token)
    {
        Notify();
        if (seconds == 0) return admitted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var result = admitted;
        try
        {
            while (true)
            {
                var doc = await documents.GetAsync(admitted.Document.Id, true, deadline.Token);
                if (doc is not null) result = result with { Document = doc, Keyword = admitted.Keyword, Vector = doc.Status == "indexed" ? "live" : "pending" };
                var job = admitted.Job is null ? null : await documents.JobAsync(admitted.Job.Id, deadline.Token);
                if (job is not null) result = result with { Job = job };
                if (result.Document.Status is not ("queued" or "processing" or "pending")) return result;
                await Task.Delay(50, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return result; }
    }

    private async Task CleanupClaimsAsync(CancellationToken token)
    {
        foreach (var work in await documents.PendingClaimCleanupAsync(16, token))
        {
            if (work.ClaimRoot is null || work.ClaimPath is null) continue;
            await claims.CompleteAsync(work.ClaimRoot, new ClaimedInput(work.ClaimPath, work.ClaimOriginalName ?? work.FileName), token);
            await documents.FinalizeClaimAsync(work.JobId, token);
        }
    }

    private async Task ProcessAsync(FileProcessingWork work, CancellationToken token)
    {
        try
        {
            SecondBrain.Extraction.Contracts.ExtractionResult extraction;
            try
            {
                var cached = await documents.CachedExtractionAsync(work.DocumentId, token);
                if (cached is not null) extraction = cached;
                else
                {
                    await using var original = await files.OpenReadAsync(work.Path, token);
                    extraction = await extractor.ExtractAsync(original, Path.GetExtension(work.FileName), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                await documents.FailFileAsync(work.JobId, "extraction-unavailable", false, token);
                return;
            }
            if (extraction.Status == "failed" && extraction.Error == "extractor_protocol_failed")
            {
                await documents.FailFileAsync(work.JobId, extraction.Error, false, token);
                return;
            }
            var completed = await documents.CompleteFileAsync(work.DocumentId, work.Revision, extraction, token);
            await embeddings.TryCompleteAsync(completed, token);
            await accounting.RefreshAsync(token);
            await CleanupClaimsAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or IngestionConflictException)
        { logger.LogWarning("File processing remains pending; admission or extraction service unavailable."); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The daemon holds the exclusive data-root lock before hosted services run.
        // No worker from the preceding instance can still own these durable leases.
        await documents.RecoverFileLeasesAsync(stoppingToken);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunWorkerAsync(stoppingToken)));
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupClaimsAsync(stoppingToken);
                var pending = await documents.PendingFilesAsync(1, stoppingToken);
                if (pending.Count != 0)
                {
                    await ProcessAsync(pending[0], stoppingToken);
                    continue; // Drain admitted work without an artificial polling delay.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or IngestionConflictException)
            { logger.LogWarning("File processing remains pending; admission or extraction service unavailable."); }
            await wake.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
