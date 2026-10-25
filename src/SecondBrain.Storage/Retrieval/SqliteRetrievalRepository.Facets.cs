using SecondBrain.Core.Retrieval;

namespace SecondBrain.Storage.Retrieval;

public sealed partial class SqliteRetrievalRepository
{
    public async ValueTask<IReadOnlyList<TypeCount>> TypeCountsAsync(FacetRequest request, CancellationToken cancellationToken = default)
    {
        RetrievalFilterValidation.Validate(request.Filters);
        if (request.Query?.Length > 8192) throw new ArgumentException("The query is too long.");
        using var gate = await VectorSnapshotRegistry.For(index).EnterAsync(cancellationToken);
        await using var lease = await index.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);
        var parameters = new List<(string Name, object? Value)>();
        // Count every matching document, not just top-ranked passages. Leaving the type
        // facet open lets users see the alternatives while retaining every other filter.
        var where = BuildFilters((request.Filters ?? new()) with { Type = null }, parameters);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var query = SanitizeFts(request.Query);
            if (query.Length == 0) where += " AND 0";
            else
            {
                parameters.Add(("$query", query));
                where += " AND d.document_id IN (SELECT c.document_id FROM chunks c JOIN chunks_fts ON chunks_fts.rowid=c.rowid WHERE chunks_fts MATCH $query)";
            }
        }
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var command = Command(lease.Connection, transaction,
            "SELECT d.type,COUNT(*) FROM indexed_documents d WHERE " + where + " GROUP BY d.type", parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) counts[reader.GetString(0)] = reader.GetInt64(1);
        return new[] { "note", "article", "transcript", "calendar_event", "file" }.Select(type => new TypeCount(type, counts.GetValueOrDefault(type))).ToArray();
    }
}
