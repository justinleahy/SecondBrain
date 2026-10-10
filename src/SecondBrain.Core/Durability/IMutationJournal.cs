namespace SecondBrain.Core.Durability;

/// <summary>Crash-safe managed-file writes. All finalization data is persisted at prepare time.</summary>
public interface IMutationJournal
{
    ValueTask<MutationRecord> PrepareAsync(MutationWriteRequest request, CancellationToken cancellationToken = default);
    ValueTask<MutationRecord> ApplyAsync(string mutationId, CancellationToken cancellationToken = default);
    ValueTask<MutationRecord> FinalizeAsync(string mutationId, CancellationToken cancellationToken = default);
    ValueTask<MutationRecord> ExecuteAsync(MutationWriteRequest request, CancellationToken cancellationToken = default);
    ValueTask<MutationRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Undoes a finalized import whose document was never published (admission rejected after the write):
    /// removes the destination only while it still holds the imported bytes, then forgets the journal entry.
    /// Returns false when no such unpublished import exists.
    /// </summary>
    ValueTask<bool> DiscardUnpublishedImportAsync(string destination, CancellationToken cancellationToken = default);
}

/// <summary>The caller authorizes a write before prepare; the journal does not grant write authority.</summary>
public sealed record MutationWriteRequest
{
    public required string MutationId { get; init; }
    public required string Kind { get; init; }
    public required string OperationId { get; init; }
    /// <summary>An absolute managed-file path beneath the configured data root.</summary>
    public required string Destination { get; init; }
    public required byte[] Content { get; init; }
    /// <summary>Lower/upper-case SHA-256 hex, or null to require a previously absent destination.</summary>
    public string? ExpectedHash { get; init; }
    public IReadOnlyList<MutationRemovalTarget> RemovalTargets { get; init; } = [];
    public MutationFinalization Finalization { get; init; } = new();
    public IReadOnlyList<string> DocumentReferences { get; init; } = [];
    /// <summary>Optional immutable replay result; stored once and referenced by execution/idempotency.</summary>
    public byte[]? ReplayResult { get; init; }
}

/// <summary>Durable metadata applied atomically with the mutation's finalized transition.</summary>
public sealed record MutationFinalization
{
    public bool PurgeDocumentLineage { get; init; }
    public bool SuppressDocument { get; init; }
    public bool ClearSuppression { get; init; }
    public MutationDocumentMetadata? DocumentMetadata { get; init; }
    public string? DocumentId { get; init; }
    public long? RevisionBefore { get; init; }
    public long? RevisionAfter { get; init; }
    public string RevisionCause { get; init; } = "edit";
    /// <summary>Optional processing-generation object; an existing document fence is retained when omitted.</summary>
    public string? GenerationJson { get; init; }
    public string? PendingOperationId { get; init; }
    public string? ToolExecutionId { get; init; }
    public MutationIdempotency? Idempotency { get; init; }
}

public sealed record MutationRemovalTarget(string Path, string ExpectedHash);

public sealed record MutationDocumentMetadata(
    string Title, string Type, string TypeOrigin, string PropsJson, string PropsOriginalJson,
    string Status, string ProjectionPayloadId, string? Mime = null, string? SourceUrl = null, IReadOnlyDictionary<string, string>? Overrides = null, bool UpdateOccurred = false,
    string? OccurredAt = null, string? OccurredOffset = null, string? OccurredBasis = null, string? OccurredPrecision = null);

public sealed record MutationIdempotency(string Key, string CredentialId, string PayloadHash, string ResponseJson);

public sealed record MutationRecord(
    string Id, string Kind, string OperationId, string Destination, string? ExpectedHash,
    string? ResultingHash, string Status, long? RevisionBefore, long? RevisionAfter,
    string? ResultPayloadId, string? ConflictPath = null);

public sealed record MutationRecoveryReport(int Finalized, int Discarded, int Conflicts, int ReconciledRenames);

/// <summary>Additional filesystem crash boundaries without modifying the frozen shared names.</summary>
public static class MutationCrashPointNames
{
    public const string AfterTempFlush = "after_temp_flush";
    public const string BeforeAtomicRename = "before_atomic_rename";
    public const string AfterAtomicExchange = "after_atomic_exchange";
    public const string BeforeSourceQuarantine = "before_source_quarantine";
    public const string AfterSourceQuarantine = "after_source_quarantine";
    public const string BeforeSourceRemoval = "before_source_removal";
    public const string AfterSourceRemoval = "after_source_removal";
    public const string AfterRename = "after_rename";
}

/// <summary>A mutation id was reused with a different immutable operation.</summary>
public sealed class MutationReuseException(string mutationId) : InvalidOperationException($"Mutation '{mutationId}' was reused with different inputs.");

/// <summary>Finalization requires an authoritative document revision that has changed.</summary>
public sealed class MutationRevisionException(string documentId) : InvalidOperationException($"Document '{documentId}' changed before mutation finalization.");
