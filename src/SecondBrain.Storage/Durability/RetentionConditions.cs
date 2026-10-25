using System.Data.Common;
using Dapper;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Storage.Durability;

internal static class RetentionConditions
{
    internal static async ValueTask<bool> MatchesAsync(DbConnection connection, DbTransaction transaction, string id, RetentionExpiry expiry, CancellationToken token)
    {
        expiry.Validate();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COUNT(*) FROM documents d LEFT JOIN retention_removed_at r ON r.document_id=d.id
            WHERE d.id=@id AND d.status=@status AND julianday(CASE WHEN @status='deleted' THEN d.deleted_at ELSE r.since END)<=julianday(@before)
              AND (@status='deleted' OR NOT EXISTS(SELECT 1 FROM suppressions s WHERE s.scope='document' AND s.key=d.id))
            """, new { id, status = expiry.Status, before = expiry.Before.ToString("O") }, transaction, cancellationToken: token)) != 0;
    }

    internal static Task RecordAsync(DbConnection connection, DbTransaction transaction, string id, RetentionExpiry expiry, CancellationToken token) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO retention_purges(document_id,operation_id,reason,cutoff) VALUES(@id,@operation,@status,@before)
            """, new { id, operation = expiry.OperationId, status = expiry.Status, before = expiry.Before.ToString("O") }, transaction, cancellationToken: token));
}
