using System.Text.Json;
using System.Text.Json.Serialization;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Core.Retrieval;

public sealed record TimeWindow(string? From = null, string? To = null);
public sealed record PropertyPredicate(string Field, string Op, JsonElement Value);
public sealed record PersonFilter(string Value, string? Role = null);
public sealed record RetrievalFilters(string? Type = null, string[]? Tags = null, string? Source = null,
    [property: JsonPropertyName("path_prefix")] string? PathPrefix = null, string? Mime = null, TimeWindow? Occurred = null, TimeWindow? Updated = null,
    PropertyPredicate[]? Predicates = null, PersonFilter[]? People = null);
public sealed record SearchRequest(string Query, string Mode = "hybrid", RetrievalFilters? Filters = null,
    int Limit = 8, string Rerank = "none", [property: JsonPropertyName("recency_boost")] bool RecencyBoost = false);
public sealed record HighlightRange(int Start, int End);
public sealed record SearchHit([property: JsonPropertyName("chunk_id")] string ChunkId, [property: JsonPropertyName("document_id")] string DocumentId, long Revision, string Type, string Title,
    string? Path, string Context, string Snippet, [property: JsonPropertyName("highlight_ranges")] IReadOnlyList<HighlightRange> HighlightRanges,
    double Score, [property: JsonPropertyName("occurred_at")] string? OccurredAt, [property: JsonPropertyName("updated_at")] string UpdatedAt, string[] Tags, int? Page = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TranscriptLocator? Transcript = null);
public sealed record TraceCandidate(string ChunkId, string DocumentId);
public sealed record RetrievalPhaseTimings(double GateAndSnapshotMilliseconds,
    double LexicalRankingMilliseconds, double LexicalMaterializationMilliseconds,
    double VectorAcquisitionMilliseconds, double SemanticFilteringMilliseconds,
    double SemanticScoringMilliseconds, double SemanticMaterializationMilliseconds,
    double FusionMilliseconds);
public sealed record RetrievalRuntimeMetadata(int ProcessorCount, bool SimdHardwareAccelerated,
    int SimdFloatWidth, int ScoringPartitions, int ExactChunksScored, int LexicalRankingPasses,
    int LexicalCandidatesMaterialized, int SemanticCandidatesMaterialized)
{
    public bool LexicalContributionHit { get; init; }
    public long LexicalDerivedBytes { get; init; }
    public long LexicalPeakBytes { get; init; }
    public int LexicalPrimitiveLoads { get; init; }
}
public sealed record RetrievalTrace(IReadOnlyList<TraceCandidate> Lexical, IReadOnlyList<TraceCandidate> Semantic,
    IReadOnlyList<TraceCandidate> Fused, double ElapsedMilliseconds)
{
    public RetrievalPhaseTimings? Phases { get; init; }
    public RetrievalRuntimeMetadata? Runtime { get; init; }
}
public sealed record SearchResponse(IReadOnlyList<SearchHit> Results, IReadOnlyList<string> Degraded)
{
    [JsonIgnore] public RetrievalTrace? Trace { get; init; }
}
public sealed record ContextPassage(string ChunkId, string DocumentId, long Revision, int Ordinal, string Context, string Text, int? Page, int Tokens,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TranscriptLocator? Transcript = null);
public sealed record QueryRequest(string? Type = null, PropertyPredicate[]? Predicates = null,
    PersonFilter[]? People = null, TimeWindow? Occurred = null, TimeWindow? Updated = null,
    string[]? Tags = null, string Sort = "updated_at", string Direction = "desc", int Limit = 50,
    string? Cursor = null, string? Source = null, [property: JsonPropertyName("path_prefix")] string? PathPrefix = null, string? Mime = null,
    [property: JsonPropertyName("include_cancelled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IncludeCancelled = false);
public sealed record CalendarCoverageSource(string ContainerId, string SourceId, string From, string To, long Generation);
public sealed record CalendarSubscriptionFreshness([property: JsonPropertyName("source_id")] string SourceId, string State, bool Stale,
    [property: JsonPropertyName("last_success_at")] DateTimeOffset? LastSuccessAt,
    [property: JsonPropertyName("last_attempt_at")] DateTimeOffset? LastAttemptAt, string? Error);
public sealed record CalendarCoverage([property: JsonPropertyName("calendar")] string Status, IReadOnlyList<CalendarCoverageSource> Sources)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<CalendarSubscriptionFreshness>? Subscriptions { get; init; }
    [JsonPropertyName("covered_from")] public string? CoveredFrom => Sources.Count == 0 ? null : Sources.Max(item => item.From);
    [JsonPropertyName("covered_to")] public string? CoveredTo => Sources.Count == 0 ? null : Sources.Min(item => item.To);
    public long Generation => Sources.Count == 0 ? 0 : Sources.Max(item => item.Generation);
}
public sealed record QueryPage(IReadOnlyList<DocumentRecord> Records, [property: JsonPropertyName("next_cursor")] string? NextCursor, bool Truncated,
    object? Coverage = null);
public sealed record TagCount(string Tag, long Count);
public sealed record TypeCount(string Type, long Count);
public sealed record FacetRequest(string? Query = null, RetrievalFilters? Filters = null);
public sealed record TagPage(IReadOnlyList<TagCount> Tags, [property: JsonPropertyName("next_cursor")] string? NextCursor, bool Truncated);
public interface IRetrievalRepository
{
    ValueTask<IReadOnlyList<TypeCount>> TypeCountsAsync(FacetRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Type counts are unavailable.");
    ValueTask<TagPage> TagsAsync(IngestionAuthority authority, int limit = 100, string? cursor = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Tag vocabulary is unavailable.");
    ValueTask<SearchResponse> SearchAsync(SearchRequest request, float[]? queryVector = null, string? spaceId = null, CancellationToken cancellationToken = default);
    ValueTask<QueryPage> QueryAsync(QueryRequest request, IngestionAuthority authority, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DocumentRecord>> RelatedAsync(string documentId, IngestionAuthority authority, int limit = 8, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Related-document lookup is unavailable.");
    ValueTask<IReadOnlyList<ContextPassage>> ExpandAsync(IReadOnlyList<SearchHit> selected, int tokenBudget, CancellationToken cancellationToken = default);
    ValueTask RecordTraceAsync(SearchRequest request, SearchResponse response, CancellationToken cancellationToken = default);
}
public interface IPayloadProtection
{
    string Protect(string purpose, string plaintext);
    string Unprotect(string purpose, string protectedText);
}
