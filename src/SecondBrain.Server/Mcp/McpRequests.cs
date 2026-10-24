using System.Text.Json;
using System.Text.Json.Serialization;
using SecondBrain.Core.Retrieval;

namespace SecondBrain.Server.Mcp;

public sealed record McpSearchRequest(string Query, string Mode = "hybrid", string? Type = null, string[]? Tags = null,
    PersonFilter[]? People = null, [property: JsonPropertyName("occurred_after")] string? OccurredAfter = null,
    [property: JsonPropertyName("occurred_before")] string? OccurredBefore = null,
    [property: JsonPropertyName("path_prefix")] string? PathPrefix = null, int Limit = 10, string? Cursor = null);
public sealed record McpAskRequest(string Question, bool Deep = false, string? Type = null, string[]? Tags = null,
    [property: JsonPropertyName("occurred_after")] string? OccurredAfter = null,
    [property: JsonPropertyName("occurred_before")] string? OccurredBefore = null);
public sealed record McpGetRequest(string Id, int Offset = 0, int Length = 32_768);
public sealed record McpRememberRequest(string Content, string? Title = null, string Type = "note", JsonElement? Props = null,
    string[]? Tags = null, [property: JsonPropertyName("source_url")] string? SourceUrl = null,
    [property: JsonPropertyName("related_to")] string[]? RelatedTo = null);
public sealed record McpUpdateRequest(string Id, long Revision, string Content, bool Append = false);
public sealed record McpRecentRequest(string? Type = null, string? Tag = null, int Limit = 50, string? Cursor = null);
public sealed record McpTagsRequest(int Limit = 100, string? Cursor = null);
