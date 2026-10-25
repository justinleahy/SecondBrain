namespace SecondBrain.Core.Ingestion;

public interface IChunkingWorkRepository
{
    /// <summary>Rebuilds a bounded batch of current projections; durable fences recover interrupted publication.</summary>
    ValueTask<int> ProcessAsync(ChunkingProfile profile, int limit = 16, CancellationToken cancellationToken = default);
}
