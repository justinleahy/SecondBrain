using SecondBrain.Core.Providers;

namespace SecondBrain.Core.Ingestion;

public sealed record EnrichmentBatchItem(EnrichmentWork Work, BatchChatRequest Request, long ReservedTokens, long UsageId = 0);
public sealed record EnrichmentBatchState(string Id, string Provider, string Model, string BindingIdentity, string Fingerprint,
    string Phase, DateTimeOffset NextAttempt, int Version = 0, string? StagedFileId = null,
    BatchReceipt? Receipt = null, BatchSnapshot? Snapshot = null, bool Invalidated = false, string? Error = null, int[]? CompletedItems = null,
    DateTimeOffset CreatedAt = default);
public sealed record EnrichmentBatch(EnrichmentBatchState State, EnrichmentBatchItem[]? Items, BatchChatResult[]? Results = null);

public interface IEnrichmentBatchRepository
{
    Task<EnrichmentBatch?> ReadPendingAsync(CancellationToken token = default);
    Task<EnrichmentBatch?> PrepareAsync(EnrichmentBatchState state, EnrichmentBatchItem[] items, CancellationToken token = default);
    Task<EnrichmentBatch> SaveAsync(EnrichmentBatch batch, CancellationToken token = default);
    Task CompleteItemAsync(EnrichmentBatchItem item, ModelUsageTotals? totals, CancellationToken token = default);
    Task FinishAsync(EnrichmentBatch batch, CancellationToken token = default);
}
