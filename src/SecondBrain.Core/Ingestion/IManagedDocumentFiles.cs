namespace SecondBrain.Core.Ingestion;

public interface IManagedDocumentFiles
{
    ValueTask<int> CleanupCalendarSnapshotsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    /// <summary>Removes owned calendar files in bounded journaled batches. Caller must durably stop source writes first.</summary>
    ValueTask<int> PurgeOwnedDocumentFilesAsync(string documentId, string operationId, CancellationToken cancellationToken = default) =>
        PurgeOwnedCalendarFilesAsync(documentId, operationId, cancellationToken);
    ValueTask<int> PurgeOwnedCalendarFilesAsync(string containerId, string operationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Calendar source purge is unavailable.");
    ValueTask<string> WriteCalendarSnapshotAsync(string containerId, string snapshotId, byte[] bytes, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Calendar snapshot storage is unavailable.");
    string GetLifecyclePath(string documentId, string extension, string operation);
    ValueTask<string> ApplyLifecycleAsync(string documentId, string? managedPath, string extension, string operation, string? expectedHash, byte[] fallbackContent, SecondBrain.Core.Durability.MutationFinalization finalization, CancellationToken cancellationToken = default);
    string GetManagedPath(string documentId, string extension);
    string GetAssistantNotePath(string documentId) => GetManagedPath(documentId, ".md");
    ValueTask<string> WriteRevisionAsync(string documentId, byte[] bytes, string extension, string? expectedHash, SecondBrain.Core.Durability.MutationFinalization finalization, CancellationToken cancellationToken = default);
    ValueTask<Stream> OpenReadAsync(string managedPath, CancellationToken cancellationToken = default);
    ValueTask<string> WriteAsync(string documentId, byte[] bytes, string extension, string? expectedHash = null, CancellationToken cancellationToken = default);
    /// <summary>Removes a freshly written original whose document admission was rejected, with its journal entry.</summary>
    ValueTask DiscardUnpublishedAsync(string managedPath, CancellationToken cancellationToken = default);
}
