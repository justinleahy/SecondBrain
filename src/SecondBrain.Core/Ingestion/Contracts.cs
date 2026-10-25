using System.Text.Json;
using System.Text.Json.Serialization;
using SecondBrain.Core.Durability;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Core.Ingestion;

public sealed record CaptureRequest(string Content, string? Title = null, string? Type = null,
    JsonElement? Props = null, string[]? Tags = null, [property: JsonPropertyName("source_url")] string? SourceUrl = null,
    string[]? Aliases = null);
public sealed record IngestionAuthority([property: JsonPropertyName("credential_id")] string CredentialId, [property: JsonPropertyName("credential_generation")] long CredentialGeneration, [property: JsonPropertyName("account_epoch")] long AccountEpoch, [property: JsonPropertyName("credential_name")] string CredentialName, string Kind, string Door = "api");
public sealed record DocumentRecord(string Id, long Revision, string Type, string Title, string Status,
    [property: JsonPropertyName("content_hash")] string ContentHash, [property: JsonPropertyName("props_json")] string PropsJson, string Provenance, [property: JsonPropertyName("updated_at")] string UpdatedAt, string? Path = null,
    string? Text = null, IReadOnlyList<TextChunk>? Chunks = null, string[]? Tags = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DocumentEnrichment? Enrichment { get; init; }
    [JsonPropertyName("occurred_at")] public string? OccurredAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExtractionSegment>? Segments { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("instance_id")] public string? InstanceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("recurrence_id")] public string? RecurrenceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("starts_at")] public string? StartsAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("ends_at")] public string? EndsAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("document_id")] public string? DocumentId => InstanceId is null ? null : Id;
}
public sealed record ProcessingJob(string Id, [property: JsonPropertyName("document_id")] string DocumentId, long Revision, string Status, int Attempts,
    string? Error = null, string? Stage = null);
public sealed record CaptureResult(DocumentRecord Document, ProcessingJob? Job, string Keyword = "live", string Vector = "pending");
public sealed record TranscriptLocator(int From, int To, string? Speaker,
    [property: JsonPropertyName("start_ms")] long StartMs, [property: JsonPropertyName("end_ms")] long EndMs);
public sealed record TextChunk(string Id, int Ordinal, string Context, string Text, [property: JsonPropertyName("start_offset")] int StartOffset,
    [property: JsonPropertyName("end_offset")] int EndOffset, [property: JsonPropertyName("token_count")] int TokenCount, [property: JsonPropertyName("input_hash")] string InputHash, int? Page = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TranscriptLocator? Transcript = null);
public sealed record TextProjection([property: JsonPropertyName("lossless_text")] string LosslessText, [property: JsonPropertyName("search_text")] string SearchText, [property: JsonPropertyName("offset_map")] int[] OffsetMap,
    IReadOnlyList<TextChunk> Chunks, string[] Tags, string? Path = null, IReadOnlyList<string>? Links = null, IReadOnlyList<string>? Aliases = null, string? SourceCreatedAt = null, string? SourceUpdatedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ExtractionSegment>? Segments = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalendarInstance>? CalendarInstances = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CalendarGeneration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalendarPerson>? People = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DocumentEnrichment? Enrichment { get; init; }
}
public interface IDocumentRepository
{
    ValueTask<int> ScheduleCalendarMaterializationAsync(CalendarWindow window, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    ValueTask<ExtractionResult?> CachedExtractionAsync(string documentId, CancellationToken cancellationToken = default);
    ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, IngestionAuthority authority, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    ValueTask<CaptureResult> ExecuteApprovedAsync(SecondBrain.Core.Chat.ApprovedDocumentWrite write, IngestionAuthority authority, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Approved writes require the durable document repository.");
    ValueTask<CaptureResult> AdmitFileAsync(byte[] bytes, CaptureRequest metadata, string fileName, IngestionAuthority authority, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    ValueTask<CaptureResult> CompleteFileAsync(string documentId, long revision, ExtractionResult extraction, CancellationToken cancellationToken = default);
    ValueTask<CaptureResult> CompleteClassifiedFileAsync(string documentId, long revision, ExtractionResult extraction, ContentClassification? classification, CancellationToken cancellationToken = default)
        => CompleteFileAsync(documentId, revision, extraction, cancellationToken);
    ValueTask<IReadOnlyList<FileProcessingWork>> PendingClaimCleanupAsync(int limit = 16, CancellationToken cancellationToken = default);
    ValueTask FinalizeClaimAsync(string jobId, CancellationToken cancellationToken = default);
    ValueTask<ProcessingJob?> JobAsync(string id, CancellationToken cancellationToken = default);
    ValueTask<ProcessingJob> RetryJobAsync(string id, IngestionAuthority authority, CancellationToken cancellationToken = default);
    ValueTask FailFileAsync(string jobId, string error, bool deterministic, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<FileProcessingWork>> PendingFilesAsync(int limit = 16, CancellationToken cancellationToken = default);
    /// <summary>Releases extraction leases left by the preceding exclusive daemon instance.</summary>
    ValueTask RecoverFileLeasesAsync(CancellationToken cancellationToken = default);
    ValueTask<CaptureResult> CaptureExtractedAsync(CaptureRequest metadata, ExtractionResult extraction, string fileName, IngestionAuthority authority, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    ValueTask<CaptureResult> MutateAsync(string id, long revision, string operation, JsonElement payload, IngestionAuthority authority, CancellationToken cancellationToken = default);
    ValueTask<DocumentRecord?> GetAsync(string id, bool includeText = false, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ProcessingJob>> JobsAsync(string? status = null, CancellationToken cancellationToken = default);
    ValueTask<string> ProposeAsync(string id, long revision, string operation, JsonElement payload, IngestionAuthority authority, CancellationToken cancellationToken = default);
    ValueTask<int> RebuildAsync(CancellationToken cancellationToken = default);
}
public sealed class IngestionConflictException(string code) : Exception(code) { public string Code { get; } = code; }
