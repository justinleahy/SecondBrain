namespace SecondBrain.Core.Domain;

public enum TypeOrigin
{
    Override,
    Door,
    Frontmatter,
    Format,
    SourceDefault,
    Llm,
    FormatDefault,
}

public enum DocumentStatus
{
    Pending,
    Indexed,
    IndexedPartial,
    Failed,
    Skipped,
    Missing,
    RemovedFromSource,
    Deleted,
    Purged,
}

public enum PublishState { Publishing, Published }
public enum OccurredPrecision { DateTime, Date }
public enum RevisionCause { Ingest, Edit, Patch, TypeChange, Enrichment, SchemaMigration, Delete, Restore, Purge }

/// <summary>The byte location; identity is never inferred from content hashes or frontmatter (§5.3).</summary>
public sealed class DocumentOccurrence
{
    public string SourceId { get; }
    public string Path { get; }
    public Ulid? ContainerDocumentId { get; }
    public string? MemberKey { get; }
    public DateTimeOffset? FileModifiedAt { get; }
    public long? SizeBytes { get; }

    public DocumentOccurrence(string sourceId, string path, Ulid? containerDocumentId = null, string? memberKey = null,
        DateTimeOffset? fileModifiedAt = null, long? sizeBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (containerDocumentId.HasValue != !string.IsNullOrWhiteSpace(memberKey))
        {
            throw new ArgumentException("Container occurrences require both a container identity and a member key.", nameof(memberKey));
        }
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }
        SourceId = sourceId;
        Path = path;
        ContainerDocumentId = containerDocumentId;
        MemberKey = memberKey;
        FileModifiedAt = fileModifiedAt?.ToUniversalTime();
        SizeBytes = sizeBytes;
    }
}

/// <summary>A real source time, normalized to UTC while preserving its known original offset (§5.5).</summary>
public sealed class OccurredTime
{
    public DateTimeOffset At { get; }
    public string Basis { get; }
    public OccurredPrecision Precision { get; }
    public TimeSpan? OriginalOffset { get; }

    public OccurredTime(DateTimeOffset at, string basis, OccurredPrecision precision, bool offsetKnown = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basis);
        ArgumentOutOfRangeException.ThrowIfEqual(Enum.IsDefined(precision), false, nameof(precision));
        At = at.ToUniversalTime();
        Basis = basis;
        Precision = precision;
        OriginalOffset = offsetKnown ? at.Offset : null;
    }

    public static OccurredTime FromDate(DateOnly date, string basis, TimeZoneInfo instanceZone)
    {
        ArgumentNullException.ThrowIfNull(instanceZone);
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, instanceZone);
        return new OccurredTime(new DateTimeOffset(utc), basis, OccurredPrecision.Date, offsetKnown: false);
    }
}

public sealed record DocumentRevision(Ulid DocumentId, long Revision, RevisionCause Cause, string ContentHash, DateTimeOffset CreatedAt);
public sealed record DocumentSuppression(Ulid DocumentId, string Reason, DateTimeOffset CreatedAt);
public sealed record DocumentPersonReference(string Role, string Identifier, string DisplayName);

/// <summary>A stale revision precondition; HTTP maps it to revision-mismatch with status 412.</summary>
public sealed class RevisionMismatchException : InvalidOperationException
{
    public long ExpectedRevision { get; }
    public long CurrentRevision { get; }

    public RevisionMismatchException(long expectedRevision, long currentRevision)
        : base($"Expected revision {expectedRevision}; the current revision is {currentRevision}.")
    {
        ExpectedRevision = expectedRevision;
        CurrentRevision = currentRevision;
    }
}
