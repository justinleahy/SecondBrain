namespace SecondBrain.Storage.Ingestion;

/// <summary>
/// Derived index cleanup for a logical purge (SEC-26). Purge finalization records a durable marker in the same
/// state transaction; the marker is removed only after the index cleanup commits, so a crash in between is
/// completed by startup recovery. Every step is idempotent.
/// </summary>
public static class PurgeCleanup
{
    internal const string MarkerPrefix = "purge_cleanup:";
    private const string BackfillKey = "purge_cleanup_backfill";

    /// <summary>Marks a document for derived cleanup, capturing its content hash before purge scrubs it.</summary>
    internal const string MarkSql = "INSERT INTO meta(key,value) SELECT '" + MarkerPrefix + "'||id,content_hash FROM documents WHERE id=@id ON CONFLICT(key) DO NOTHING;";

    /// <summary>Completes every pending cleanup, first marking documents purged before markers existed.</summary>
    public static async ValueTask<int> RecoverAsync(IStateStore state, IIndexStore index, CancellationToken cancellationToken = default)
    {
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var backfill = SqliteDocumentRepository.Command(connection, transaction, $"""
                INSERT INTO meta(key,value) SELECT '{MarkerPrefix}'||id,'' FROM documents WHERE status='purged' AND NOT EXISTS(SELECT 1 FROM meta WHERE key='{BackfillKey}') ON CONFLICT(key) DO NOTHING;
                DELETE FROM overrides WHERE document_id IN (SELECT id FROM documents WHERE status='purged') AND NOT EXISTS(SELECT 1 FROM meta WHERE key='{BackfillKey}');
                INSERT OR IGNORE INTO meta(key,value) VALUES('{BackfillKey}','1');
                """);
            return await backfill.ExecuteNonQueryAsync(token);
        }, cancellationToken);
        var pending = new List<string>();
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var read = SqliteDocumentRepository.Command(lease.Connection, null,
            "SELECT substr(key,$offset) FROM meta WHERE key>=$prefix AND key<$end ORDER BY key", ("$offset", MarkerPrefix.Length + 1), ("$prefix", MarkerPrefix), ("$end", MarkerPrefix[..^1] + ";")))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) pending.Add(reader.GetString(0));
        foreach (var documentId in pending) await CompleteAsync(state, index, documentId, cancellationToken);
        return pending.Count;
    }

    /// <summary>Removes the purged document's caches and traces, then its marker; a no-op without a marker.</summary>
    internal static async ValueTask CompleteAsync(IStateStore state, IIndexStore index, string documentId, CancellationToken cancellationToken)
    {
        var key = MarkerPrefix + documentId;
        string? contentHash;
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var read = SqliteDocumentRepository.Command(lease.Connection, null, "SELECT value FROM meta WHERE key=$key", ("$key", key)))
            contentHash = await read.ExecuteScalarAsync(cancellationToken) as string;
        if (contentHash is null) return;
        // Extraction results are keyed by content hash; every revision's hash is lineage of the purged document.
        var contentHashes = new HashSet<string>(StringComparer.Ordinal);
        if (contentHash.Length != 0) contentHashes.Add(contentHash);
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var revisions = SqliteDocumentRepository.Command(lease.Connection, null, "SELECT DISTINCT content_hash FROM revisions WHERE document_id=$id AND content_hash<>''", ("$id", documentId)))
        await using (var reader = await revisions.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) contentHashes.Add(reader.GetString(0));
        await index.QueueWriteAsync(async (connection, transaction, token) =>
        {
            // Cached vectors from every revision the document embedded, unless another document still uses the input.
            await using var cache = SqliteDocumentRepository.Command(connection, transaction, """
                DELETE FROM embedding_cache WHERE input_hash IN (SELECT input_hash FROM embedding_cache_owners WHERE document_id=$id UNION SELECT input_hash FROM chunks WHERE document_id=$id)
                  AND NOT EXISTS(SELECT 1 FROM chunks c WHERE c.input_hash=embedding_cache.input_hash AND c.document_id<>$id)
                  AND NOT EXISTS(SELECT 1 FROM embedding_cache_owners o WHERE o.input_hash=embedding_cache.input_hash AND o.document_id<>$id);
                DELETE FROM embedding_cache_owners WHERE document_id=$id;
                """, ("$id", documentId));
            await cache.ExecuteNonQueryAsync(token);
            foreach (var hash in contentHashes)
            {
                await using var extraction = SqliteDocumentRepository.Command(connection, transaction, "DELETE FROM extraction_cache WHERE content_hash=$hash", ("$hash", hash));
                await extraction.ExecuteNonQueryAsync(token);
            }
            await using var traces = SqliteDocumentRepository.Command(connection, transaction, "DELETE FROM retrieval_traces WHERE json_valid(trace_json) AND EXISTS(SELECT 1 FROM json_tree(trace_json) WHERE value=$id)", ("$id", documentId));
            return await traces.ExecuteNonQueryAsync(token);
        }, cancellationToken);
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var clear = SqliteDocumentRepository.Command(connection, transaction, "DELETE FROM meta WHERE key=$key", ("$key", key));
            return await clear.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }
}
