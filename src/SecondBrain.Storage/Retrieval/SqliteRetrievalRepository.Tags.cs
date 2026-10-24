using System.Text.Json;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;

namespace SecondBrain.Storage.Retrieval;

public sealed partial class SqliteRetrievalRepository
{
    public async ValueTask<TagPage> TagsAsync(IngestionAuthority authority, int limit = 100, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentException("Invalid tag limit.");
        var registry = VectorSnapshotRegistry.For(index);
        using var consistent = await registry.EnterAsync(cancellationToken);
        await using var lease = await index.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);
        var generation = await registry.CorpusGenerationAsync(lease.Connection, transaction, cancellationToken);
        var instance = await InstanceAsync(cancellationToken);
        var after = "";
        if (cursor is not null)
        {
            if (cursor.Length > 4096) throw new ArgumentException("Invalid cursor.");
            Cursor saved;
            try { saved = JsonSerializer.Deserialize<Cursor>(protection.Unprotect(CursorPurpose, cursor))!; }
            catch (Exception error) when (error is not OperationCanceledException) { throw new ArgumentException("Invalid cursor.", error); }
            if (saved is null || saved.Instance != instance || saved.Operation != "tags" || saved.CredentialId != authority.CredentialId ||
                saved.CredentialGeneration != authority.CredentialGeneration || saved.AccountEpoch != authority.AccountEpoch || saved.Generation != generation ||
                saved.ExpiresAt <= DateTimeOffset.UtcNow || saved.IssuedAt > DateTimeOffset.UtcNow) throw new ArgumentException("Invalid cursor.");
            after = saved.Position;
        }
        var tags = new List<TagCount>();
        await using (var query = Command(lease.Connection, transaction, """
            SELECT t.tag,COUNT(*) FROM effective_tags t JOIN indexed_documents d ON d.document_id=t.document_id
            WHERE d.status IN ('indexed','indexed_partial') AND t.tag>$after
            GROUP BY t.tag ORDER BY t.tag LIMIT $limit
            """, ("$after", after), ("$limit", limit + 1)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) tags.Add(new(reader.GetString(0), reader.GetInt64(1)));
        var truncated = tags.Count > limit;
        if (truncated) tags.RemoveAt(tags.Count - 1);
        var now = DateTimeOffset.UtcNow;
        var next = truncated ? protection.Protect(CursorPurpose, JsonSerializer.Serialize(new Cursor(instance, "tags", "tags", authority.CredentialId,
            authority.CredentialGeneration, authority.AccountEpoch, generation, tags[^1].Tag, "", now, now.AddMinutes(15)))) : null;
        return new(tags, next, truncated);
    }
}
