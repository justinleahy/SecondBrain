using SecondBrain.Core.Ingestion;

namespace SecondBrain.Core.Sources;

public sealed record FolderImportCandidate(SourceFileEntry Entry, string? DocumentId, long Revision);
public sealed record FolderScanCandidate(FolderImportCandidate Candidate, int HashOccurrences);
public interface IFolderDocumentRepository
{
    ValueTask PersistPlanAsync(SourceRecord source, IReadOnlyList<FolderScanCandidate> candidates, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<FolderScanCandidate>> PendingPlanAsync(SourceRecord source, int limit = 256, CancellationToken cancellationToken = default);
    /// <summary>Expires unmatched prior absences after every persisted candidate has been admitted or acknowledged.</summary>
    ValueTask CompleteScanAsync(string sourceId, CancellationToken cancellationToken = default);
    ValueTask CompleteCandidateAsync(string sourceId, string relativePath, CancellationToken cancellationToken = default);
    /// <summary>Stops a source and durably purges its retained data; external source files are never removed.</summary>
    ValueTask PurgeAsync(string sourceId, IngestionAuthority authority, CancellationToken cancellationToken = default) => throw new NotSupportedException("Folder purge unavailable.");
    ValueTask<int> RecoverPurgesAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    ValueTask SetStatusAsync(string sourceId, string status, IngestionAuthority authority, CancellationToken cancellationToken = default);
    ValueTask<SourceRecord> EnsureInboxAsync(string inboxPath, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<FolderImportCandidate>> PlanScanAsync(SourceRecord source, IReadOnlyList<SourceFileEntry> entries, bool deep, CancellationToken cancellationToken = default);
    /// <summary>Durably records a rejected entry as a failed stage job, with a metadata-only document when it has no readable one; idempotent while unchanged.</summary>
    ValueTask<bool> RecordRejectionAsync(SourceRecord source, SourceEntryRejection rejection, CancellationToken cancellationToken = default);
    ValueTask<CaptureResult?> AdmitSourceAsync(SourceRecord source, FolderImportCandidate candidate, byte[] bytes, string sha256, int hashOccurrences = 1, CancellationToken cancellationToken = default);
}
