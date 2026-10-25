using System.Text.Json.Serialization;

namespace SecondBrain.Core.Ingestion;

public sealed record CatalogPage<T>(IReadOnlyList<T> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);
public sealed record TrashedDocument(string Id, long Revision, string Title, string Type,
    [property: JsonPropertyName("deleted_at")] string? DeletedAt);
public sealed record CatalogSource(string Id, string Name, string Status);
public sealed record SourceDocumentCounts(string Id, long Total, long Available, long Pending, long Failed, long Missing);
public sealed record CatalogJob(string Id, string DocumentId, long Revision, string Title, string? FileName,
    string SourceId, string SourceName, string Status, int Attempts, string? Error, string Stage, bool CanRetry, string? RetryRejection);
public sealed record CatalogCompletion(string Label, string Value, string Detail);
public sealed record DocumentTag(string Tag, string Origin);
public sealed record DocumentLink(string Target, string? DocumentId, string? Title);
public sealed record DocumentRevisionEntry(long Revision, string Cause, string CreatedAt);
public sealed record DocumentPage(int Number, int Start, int Length);
public sealed record PreservedConflictFile(string Name, string Path);
public sealed record CalendarDocumentInfo(string ContainerId, string From, string To, long Generation, int DuplicateCalendars,
    IReadOnlyList<SecondBrain.Extraction.Contracts.CalendarInstance> Occurrences, bool Truncated);
public sealed record DocumentDetails(long Revision, string SourceId, string SourceName, string TypeOrigin, string? Mime,
    string? SourceUrl, string CreatedAt, string UpdatedAt, string? OccurredAt, string? OccurredBasis, string? OccurredPrecision,
    string PropsOriginalJson, IReadOnlyList<string> Aliases, IReadOnlyList<DocumentTag> Tags, IReadOnlyList<DocumentLink> Links)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TypeConfidence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentEnrichment? Enrichment { get; init; }
    public IReadOnlyList<DocumentPage> Pages { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CalendarDocumentInfo? Calendar { get; init; }
}
public sealed record SuppressionRecord(string Id, string Scope, string? Source, string Key, string Reason,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("document_status")] string? DocumentStatus);

/// <summary>Metadata-only lifecycle listings; retained text never crosses this boundary.</summary>
public interface IDocumentCatalog
{
    ValueTask<PreservedConflictFile?> ConflictFileAsync(string documentId, string mutationId, IngestionAuthority authority, CancellationToken cancellationToken = default);
    ValueTask<CatalogPage<CatalogJob>> JobsAsync(IngestionAuthority authority, string? status = null, string? source = null,
        int limit = 50, string? cursor = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<SourceDocumentCounts>> SourceCountsAsync(IngestionAuthority authority, CancellationToken cancellationToken = default);
    ValueTask<DocumentDetails> DetailsAsync(string id, IngestionAuthority authority, long? revision = null, CancellationToken cancellationToken = default);
    ValueTask<CatalogPage<DocumentRevisionEntry>> RevisionsAsync(string id, IngestionAuthority authority, int limit = 50,
        string? cursor = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<CatalogCompletion>> CompleteAsync(string kind, string prefix, IngestionAuthority authority,
        int limit = 20, CancellationToken cancellationToken = default);
    ValueTask<CatalogPage<CatalogSource>> SourcesAsync(IngestionAuthority authority, int limit = 200,
        string? cursor = null, CancellationToken cancellationToken = default);
    ValueTask<CatalogPage<TrashedDocument>> TrashAsync(IngestionAuthority authority, int limit = 50,
        string? cursor = null, CancellationToken cancellationToken = default);
    ValueTask<CatalogPage<SuppressionRecord>> SuppressionsAsync(IngestionAuthority authority, int limit = 50,
        string? cursor = null, CancellationToken cancellationToken = default);
    ValueTask<bool> LiftSuppressionAsync(string id, IngestionAuthority authority, CancellationToken cancellationToken = default);
}
