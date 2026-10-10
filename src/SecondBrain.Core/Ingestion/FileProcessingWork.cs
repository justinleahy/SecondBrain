namespace SecondBrain.Core.Ingestion;

public sealed record FileProcessingWork(string DocumentId, long Revision, string GenerationJson, string JobId,
    string Path, string FileName, string? ClaimRoot = null, string? ClaimPath = null, string? ClaimOriginalName = null);
