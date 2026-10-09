namespace SecondBrain.Core.Durability;

/// <summary>Serializes a document's state and index publication without cross-store atomicity (§8).</summary>
public interface IPublicationCoordinator
{
    /// <summary>
    /// Records a new revision, replaces its index snapshot, then marks it published. A document with an active
    /// (prepared or applied) journal mutation is Superseded until that mutation is finalized or conflicted.
    /// </summary>
    ValueTask<PublicationOutcome> PublishAsync(DocumentPublication publication, CancellationToken cancellationToken = default);

    /// <summary>Publishes an existing revision only while both job fences remain current.</summary>
    ValueTask<PublicationOutcome> PublishCurrentAsync(PublicationFence fence, CancellationToken cancellationToken = default);

    /// <summary>Changes only the processing generation and republishes without advancing revision.</summary>
    ValueTask<PublicationOutcome> ReprocessAsync(PublicationGenerationChange change, CancellationToken cancellationToken = default);

    /// <summary>Repeats index replacement and finalization for documents left publishing; returns completed count.</summary>
    ValueTask<int> RecoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>The immutable document revision and processing generation served by a publication job.</summary>
public sealed record PublicationFence(string DocumentId, long Revision, string GenerationJson);

/// <summary>An optimistic generation-only update; the expected fence must still be current.</summary>
public sealed record PublicationGenerationChange(PublicationFence Expected, string GenerationJson);

/// <summary>Outcome of a publication; superseded work never replaces newer projections.</summary>
public enum PublicationOutcome
{
    /// <summary>The snapshot and state completion marker committed.</summary>
    Published,

    /// <summary>The expected revision or processing generation is no longer current.</summary>
    Superseded
}

/// <summary>
/// A new authored document revision independent of the domain aggregate. ExpectedRevision is zero
/// for creation; subsequent publications advance it by one. GenerationJson is a JSON object.
/// </summary>
public sealed record DocumentPublication(
    string DocumentId,
    string SourceId,
    long ExpectedRevision,
    string GenerationJson,
    string Cause,
    string Title,
    string ContentHash,
    string Provenance)
{
    /// <summary>The registered content type.</summary>
    public string Type { get; init; } = "file";
    /// <summary>The authoritative type provenance.</summary>
    public string TypeOrigin { get; init; } = "door";
    /// <summary>An optional classification confidence.</summary>
    public double? TypeConfidence { get; init; }
    /// <summary>The schema that validated effective properties.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Effective properties, already validated by the domain service.</summary>
    public string PropsJson { get; init; } = "{}";
    /// <summary>The original declared properties retained across reclassification.</summary>
    public string PropsOriginalJson { get; init; } = "{}";
    /// <summary>The content media type.</summary>
    public string? Mime { get; init; }
    /// <summary>An optional originating URL.</summary>
    public string? SourceUrl { get; init; }
    /// <summary>An informational, self-asserted client name.</summary>
    public string? ClaimedClient { get; init; }
    /// <summary>The real source occurrence time, if known.</summary>
    public string? OccurredAt { get; init; }
    /// <summary>The source time's original UTC offset.</summary>
    public string? OccurredOffset { get; init; }
    /// <summary>The property that supplied the occurrence time.</summary>
    public string? OccurredBasis { get; init; }
    /// <summary>Date or datetime precision of the source time.</summary>
    public string? OccurredPrecision { get; init; }
    /// <summary>The source-scoped natural key.</summary>
    public string? NaturalKey { get; init; }
    /// <summary>The authoritative file size, if known.</summary>
    public long? SizeBytes { get; init; }
    /// <summary>Publication status. Deleted and purged revisions remove the searchable snapshot.</summary>
    public string Status { get; init; } = "indexed";
    /// <summary>A bounded processing error, if any.</summary>
    public string? Error { get; init; }
    /// <summary>An optional creation timestamp used only for a new document.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}
