using System.Text.Json;

namespace SecondBrain.Core.Domain;

/// <summary>
/// Persistence boundary for authoritative document state. Rehydrate preserves stored props even if a custom type
/// has been removed; processing uses TypeRegistry.GetProcessingType and migrations are explicit (§5.4).
/// </summary>
public sealed record DocumentSnapshot
{
    public required Ulid Id { get; init; }
    public required DocumentOccurrence Occurrence { get; init; }
    public required long Revision { get; init; }
    public required string Type { get; init; }
    public required TypeOrigin TypeOrigin { get; init; }
    public double? TypeConfidence { get; init; }
    public required int SchemaVersion { get; init; }
    public required JsonElement Props { get; init; }
    public required JsonElement PropsOriginal { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public DateTimeOffset? SourceCreatedAt { get; init; }
    public required string Title { get; init; }
    public string? Mime { get; init; }
    public required string ContentHash { get; init; }
    public OccurredTime? OccurredAt { get; init; }
    public string? NaturalKey { get; init; }
    public required IReadOnlyList<DocumentPersonReference> People { get; init; }
    public required IReadOnlyDictionary<string, JsonElement> IndexedProperties { get; init; }
    public required DocumentStatus Status { get; init; }
    public required PublishState PublishState { get; init; }
    public long? IndexedRevision { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public DocumentSuppression? Suppression { get; init; }
    public required IReadOnlyList<DocumentRevision> Revisions { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Occurrence);
        ArgumentException.ThrowIfNullOrWhiteSpace(Type);
        ArgumentNullException.ThrowIfNull(Title);
        ArgumentNullException.ThrowIfNull(ContentHash);
        ArgumentNullException.ThrowIfNull(Tags);
        ArgumentNullException.ThrowIfNull(People);
        ArgumentNullException.ThrowIfNull(IndexedProperties);
        ArgumentNullException.ThrowIfNull(Revisions);
        if (Id == default || Revision < 1 || SchemaVersion < 1 || UpdatedAt < CreatedAt ||
            !Enum.IsDefined(TypeOrigin) || !Enum.IsDefined(Status) || !Enum.IsDefined(PublishState) ||
            TypeConfidence is { } confidence && (double.IsNaN(confidence) || confidence is < 0 or > 1) ||
            Props.ValueKind != JsonValueKind.Object || PropsOriginal.ValueKind != JsonValueKind.Object ||
            IndexedRevision is { } indexed && (indexed < 1 || indexed > Revision) ||
            PublishState == PublishState.Published && IndexedRevision != Revision ||
            Suppression is { } suppression && suppression.DocumentId != Id)
        {
            throw new ArgumentException("The stored document snapshot has inconsistent identity, revision, or metadata.");
        }

        if (Revisions.Count != Revision || Revisions[0].Cause != RevisionCause.Ingest || Revisions[0].CreatedAt != CreatedAt)
        {
            throw new ArgumentException("Stored revision history must begin with ingestion and reach the current revision.");
        }
        var previousTime = CreatedAt;
        for (var index = 0; index < Revisions.Count; index++)
        {
            var revision = Revisions[index];
            if (revision.DocumentId != Id || revision.Revision != index + 1L || !Enum.IsDefined(revision.Cause) ||
                revision.CreatedAt < previousTime || revision.CreatedAt > UpdatedAt)
            {
                throw new ArgumentException("Stored revisions must be contiguous, chronological, and belong to the document.");
            }
            previousTime = revision.CreatedAt;
        }
        if (Revisions[^1].CreatedAt != UpdatedAt || Revisions[^1].ContentHash != ContentHash)
        {
            throw new ArgumentException("Stored revision history must describe the document's current content and mutation time.");
        }
        if (Status is DocumentStatus.Deleted or DocumentStatus.Purged && (Suppression is null || DeletedAt is null))
        {
            throw new ArgumentException("Deleted and purged snapshots must retain their suppression tombstone.");
        }
        if (Status == DocumentStatus.Purged && (Title != "[redacted]" || ContentHash.Length != 0 || Mime is not null ||
            SourceCreatedAt is not null || Props.EnumerateObject().Any() || PropsOriginal.EnumerateObject().Any() ||
            Tags.Count != 0 || People.Count != 0 || IndexedProperties.Count != 0 || OccurredAt is not null || NaturalKey is not null))
        {
            throw new ArgumentException("A purged document snapshot cannot contain retained content metadata.");
        }
    }
}
