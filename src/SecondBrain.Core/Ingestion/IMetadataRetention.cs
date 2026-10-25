namespace SecondBrain.Core.Ingestion;

public sealed record MetadataRetentionResult(int ExpiredProposals, int PrunedProposals, int IdempotencyRecords,
    int DetachedReferences, int OrphanPayloads, int RetrievalTraces, bool MoreWork);

/// <summary>Bounded cleanup of temporary records. A null actor is reserved for the daemon's scheduled policy.</summary>
public interface IMetadataRetention
{
    ValueTask<MetadataRetentionResult> RunAsync(IngestionAuthority? actor = null, int limit = 256, CancellationToken cancellationToken = default);
}
