namespace SecondBrain.Core.Ingestion;

public sealed record PublicationAdmission(IngestionAuthority? Authority, string JobId, string? IdempotencyKey,
    string RequestHash, long AdmittedBytes, string Kind = "embed", string? PayloadJson = null, string? TrustedSourceId = null, PublicationOccurrence? Occurrence = null,
    string JobStatus = "queued", string? JobError = null);

public sealed record PublicationOccurrence(string SourceId, string RelativePath, DateTimeOffset ModifiedAt, long Length);
