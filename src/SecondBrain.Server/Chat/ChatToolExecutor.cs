using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;

namespace SecondBrain.Server.Chat;

/// <summary>All arguments are model-controlled. Read authority and cumulative exposure are enforced outside the prompt.</summary>
internal sealed partial class ChatToolExecutor
{
    private readonly IChatRepository repository;
    private readonly IRetrievalRepository retrieval;
    private readonly IDocumentRepository documents;
    private readonly EmbeddingProcessor embeddings;
    private readonly ChatTurn turn;
    private readonly AuthenticatedCredential actor;
    private readonly HashSet<string> surfaced = new(StringComparer.Ordinal);

    public ChatToolExecutor(IChatRepository repository, IRetrievalRepository retrieval, IDocumentRepository documents,
        EmbeddingProcessor embeddings, ChatTurn turn, AuthenticatedCredential actor)
    {
        this.repository = repository; this.retrieval = retrieval; this.documents = documents; this.embeddings = embeddings; this.turn = turn; this.actor = actor;
        foreach (Match match in DocumentId().Matches(turn.Prompt)) surfaced.Add(match.Value);
        Tools =
        [
            AIFunctionFactory.Create(SearchAsync, "search", "Search the vault and return verified passages with citation numbers. Use a concise query; limit is 1 through 8."),
            AIFunctionFactory.Create(ReadAsync, "read_document", "Read a document surfaced by a search in this turn or explicitly named by the user."),
            AIFunctionFactory.Create(RecentAsync, "list_recent", "List recent document metadata. Use read_document to read a returned document."),
            AIFunctionFactory.Create(QueryAsync, "query_records", "Find structured records using validated query filters; returned metadata is not a substitute for reading a document.")
        ];
    }

    public IReadOnlyList<AIFunction> Tools { get; }

    public async Task<IReadOnlyList<ChatSource>> SearchAsync(string query, int limit = 4, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2048 || limit is < 1 or > 8) throw new ArgumentException("Invalid search arguments.");
        await repository.CountToolAsync(turn.Id, limit, actor, cancellationToken);
        float[]? vector = null; string? space = null;
        var callId = Guid.NewGuid().ToString("N");
        await repository.ReserveTokensAsync(turn.Id, callId, System.Text.Encoding.UTF8.GetByteCount(query) + 64, actor, cancellationToken);
        try { (vector, space) = await embeddings.QueryAsync(query, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException && error is not AuthorityChangedException)
        {
            await repository.AppendAsync(turn.Id, "warning", JsonSerializer.SerializeToElement(new { code = "keyword_fallback" }), [], actor, cancellationToken);
        }
        finally { await repository.SettleTokensAsync(turn.Id, callId, null, CancellationToken.None); }
        await repository.CheckActiveAsync(turn.Id, actor, cancellationToken);
        var response = await retrieval.SearchAsync(new(query, "hybrid", Limit: limit), vector, space, cancellationToken);
        foreach (var hit in response.Results) surfaced.Add(hit.DocumentId);
        var sources = await repository.ExposeAsync(turn.Id, response.Results.Select(hit => new ChatSourceRequest(hit.DocumentId, hit.Revision, hit.ChunkId)).ToArray(), actor, cancellationToken);
        await repository.AppendAsync(turn.Id, "tool", JsonSerializer.SerializeToElement(new { name = "search", sources, degraded = response.Degraded }), sources.Select(source => source.DocumentId).Distinct().ToArray(), actor, cancellationToken);
        return sources;
    }

    private async Task<IReadOnlyList<ChatSource>> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!surfaced.Contains(id)) throw new ChatConflictException("Read only a document surfaced in this turn or named by the user.");
        await repository.CountToolAsync(turn.Id, 0, actor, cancellationToken);
        var document = await documents.GetAsync(id, true, cancellationToken) ?? throw new ChatNotFoundException();
        var sources = await repository.ExposeAsync(turn.Id, (document.Chunks ?? []).Select(chunk => new ChatSourceRequest(id, document.Revision, chunk.Id)).ToArray(), actor, cancellationToken);
        await repository.AppendAsync(turn.Id, "tool", JsonSerializer.SerializeToElement(new { name = "read_document", sources }), [id], actor, cancellationToken);
        return sources;
    }

    private Task<object> RecentAsync(int limit = 10, CancellationToken cancellationToken = default) => QueryAsync(new QueryRequest(Limit: limit), cancellationToken);

    private async Task<object> QueryAsync(QueryRequest query, CancellationToken cancellationToken = default)
    {
        if (query.Limit is < 1 or > 50) throw new ArgumentException("Query limit must be 1 through 50.");
        await repository.CountToolAsync(turn.Id, query.Limit, actor, cancellationToken);
        var authority = new IngestionAuthority(actor.Id, actor.Generation, actor.AccountEpoch, "", actor.Kind);
        var page = await retrieval.QueryAsync(query, authority, cancellationToken);
        foreach (var record in page.Records) surfaced.Add(record.Id);
        var result = new { records = page.Records.Select(record => new { record.Id, record.Title, record.Type, record.Revision, record.UpdatedAt }).ToArray(), page.NextCursor, page.Truncated, page.Coverage };
        await repository.AppendAsync(turn.Id, "tool", JsonSerializer.SerializeToElement(new { name = "query_records", result }), page.Records.Select(record => record.Id).ToArray(), actor, cancellationToken);
        return result;
    }

    [GeneratedRegex(@"\b[0-9A-HJKMNP-TV-Z]{26}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentId();
}
