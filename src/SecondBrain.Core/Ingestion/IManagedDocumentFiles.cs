namespace SecondBrain.Core.Ingestion;

public interface IManagedDocumentFiles
{
    string GetLifecyclePath(string documentId, string extension, string operation);
    ValueTask<string> ApplyLifecycleAsync(string documentId, string? managedPath, string extension, string operation, string? expectedHash, byte[] fallbackContent, SecondBrain.Core.Durability.MutationFinalization finalization, CancellationToken cancellationToken = default);
    string GetManagedPath(string documentId, string extension);
    ValueTask<string> WriteRevisionAsync(string documentId, byte[] bytes, string extension, string? expectedHash, SecondBrain.Core.Durability.MutationFinalization finalization, CancellationToken cancellationToken = default);
    ValueTask<Stream> OpenReadAsync(string managedPath, CancellationToken cancellationToken = default);
    ValueTask<string> WriteAsync(string documentId, byte[] bytes, string extension, string? expectedHash = null, CancellationToken cancellationToken = default);
    /// <summary>Removes a freshly written original whose document admission was rejected, with its journal entry.</summary>
    ValueTask DiscardUnpublishedAsync(string managedPath, CancellationToken cancellationToken = default);
}
