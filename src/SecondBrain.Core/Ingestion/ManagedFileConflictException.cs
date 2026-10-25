namespace SecondBrain.Core.Ingestion;

/// <summary>The journal preserved the draft instead of overwriting unseen filesystem changes.</summary>
public sealed class ManagedFileConflictException(string mutationId, string? conflictPath) : IOException("managed-file-conflict")
{
    public string MutationId { get; } = mutationId;
    public string? ConflictPath { get; } = conflictPath;
}
