using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using SecondBrain.Core.Retrieval;

namespace SecondBrain.Server.Mcp;

internal static class McpToolCatalog
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
    internal static IReadOnlyList<Tool> Tools { get; } =
    [
        Tool<McpSearchRequest>("brain_search", "Search personal notes and files by topic. Returns ranked excerpts and explicit degradation. Use brain_query for who/when/type questions.", true, true),
        Tool<QueryRequest>("brain_query", "Query records by type, people, dates, tags and indexed properties. Returns continuation and coverage; disclose partial coverage.", true, true),
        Tool<McpAskRequest>("brain_ask", "Answer a question from retrieved knowledge with verified citations, using read-only tools. Deep allows up to five searches. Disclose empty or degraded retrieval.", true, false),
        Tool<McpGetRequest>("brain_get", "Read lossless document text by ID or vault-relative note path, with revision and explicit text offsets.", true, true),
        Tool<McpRememberRequest>("brain_remember", "Save user-provided knowledge as a new document. Include context so it makes sense later. Returns identity, revision and live projections.", false, false),
        Tool<McpUpdateRequest>("brain_update", "Propose replacing or appending a document at the revision last read. Returns a proposal; an interactive session must approve before content changes.", false, false),
        Tool<McpRecentRequest>("brain_list_recent", "List recently updated documents, optionally by type and tag, with a continuation cursor.", true, true),
        Tool<McpTagsRequest>("brain_tags", "List the tag vocabulary and document counts. Use existing tags when saving knowledge. Continue using the returned cursor.", true, true),
    ];
    private static Tool Tool<T>(string name, string description, bool readOnly, bool idempotent)
    {
        var schema = Json.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        // Protocol roots are always objects. Optional constructor values remain optional.
        return new Tool
        {
            Name = name, Description = description, InputSchema = JsonSerializer.SerializeToElement(schema),
            Annotations = new() { ReadOnlyHint = readOnly, IdempotentHint = idempotent, DestructiveHint = false, OpenWorldHint = false }
        };
    }
}
