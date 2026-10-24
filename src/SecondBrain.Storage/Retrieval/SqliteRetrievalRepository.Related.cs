using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Ingestion;

namespace SecondBrain.Storage.Retrieval;

public sealed partial class SqliteRetrievalRepository
{
    public async ValueTask<IReadOnlyList<DocumentRecord>> RelatedAsync(string documentId, IngestionAuthority authority, int limit = 8, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId) || documentId.Length > 128 || limit is < 1 or > 50) throw new ArgumentException("Invalid related-document request.");
        if (state is null) throw new InvalidOperationException("Related-document lookup requires the state authority.");
        await using (var authLease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var authTransaction = await authLease.Connection.BeginTransactionAsync(cancellationToken))
            await IngestionAuthorityValidator.ValidateAsync(authLease.Connection, authTransaction, authority, Scope.Read, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
        await using var lease = await index.OpenReadConnectionAsync(cancellationToken);
        await using var command = SqliteDocumentRepository.Command(lease.Connection, null, """
            SELECT d.document_id,d.revision,d.type,d.title,d.status,d.props_json,d.updated_at
            FROM indexed_documents d WHERE d.document_id!=@id AND d.status NOT IN ('deleted','purged','missing','failed')
              AND d.document_id IN (
                SELECT to_document_id FROM links WHERE from_document_id=@id AND to_document_id IS NOT NULL
                UNION SELECT from_document_id FROM links WHERE to_document_id=@id
              ) ORDER BY d.updated_at DESC,d.document_id LIMIT @limit
            """, ("@id", documentId), ("@limit", limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<DocumentRecord> result = [];
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), "", reader.GetString(5), "", reader.GetString(6)));
        return result;
    }
}
