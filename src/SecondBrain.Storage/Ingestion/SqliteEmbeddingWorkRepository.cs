using System.Data.Common;
using System.Globalization;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Ingestion;

public sealed class SqliteEmbeddingWorkRepository(IStateStore state, IIndexStore index) : IEmbeddingWorkRepository
{
    public async ValueTask<EmbeddingSpace> EnsureSpaceAsync(string fingerprint, string provider, string model, int dimensions, CancellationToken cancellationToken = default)
    {
        return await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var insert = Command(connection, transaction,
                "INSERT OR IGNORE INTO embedding_spaces(id,fingerprint,provider,model,dimensions,chunk_generation,status,created_at) VALUES($id,$fingerprint,$provider,$model,$dimensions,1,'building',$now)",
                ("$id", Ulid.NewUlid().ToString()), ("$fingerprint", fingerprint), ("$provider", provider), ("$model", model), ("$dimensions", dimensions), ("$now", Now()));
            await insert.ExecuteNonQueryAsync(token);
            await using var revive = Command(connection, transaction, "UPDATE embedding_spaces SET status='building',retired_at=NULL WHERE fingerprint=$fingerprint AND status='retired'", ("$fingerprint", fingerprint));
            await revive.ExecuteNonQueryAsync(token);
            await using var read = Command(connection, transaction, "SELECT id,fingerprint,provider,model,dimensions,status FROM embedding_spaces WHERE fingerprint=$fingerprint", ("$fingerprint", fingerprint));
            await using var reader = await read.ExecuteReaderAsync(token); await reader.ReadAsync(token); return ReadSpace(reader);
        }, cancellationToken);
    }
    public async ValueTask<EmbeddingSpace?> ActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var read = Command(lease.Connection, null, "SELECT id,fingerprint,provider,model,dimensions,status FROM embedding_spaces WHERE status='active' ORDER BY activated_at DESC LIMIT 1");
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSpace(reader) : null;
    }
    public async ValueTask<IReadOnlyList<EmbeddingWork>> PendingAsync(string spaceId, int limit = 32, CancellationToken cancellationToken = default)
    {
        var work = new List<EmbeddingWork>();
        await using var lease = await index.OpenReadConnectionAsync(cancellationToken);
        await using var transaction = await lease.Connection.BeginTransactionAsync(cancellationToken);
        var documents = new List<(string Id, long Revision, string Generation)>();
        await using (var read = Command(lease.Connection, transaction,
            $"SELECT DISTINCT c.document_id,c.revision,g.generation_json FROM chunks c JOIN indexed_documents d ON d.document_id=c.document_id JOIN indexed_document_generations g ON g.document_id=c.document_id WHERE d.status NOT IN ({DocumentStatusSql.Unavailable},'queued','processing','failed','skipped','needs_ocr') AND NOT EXISTS(SELECT 1 FROM chunk_vectors v WHERE v.chunk_rowid=c.rowid AND v.space_id=$space) ORDER BY c.document_id LIMIT $limit", ("$space", spaceId), ("$limit", limit)))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) documents.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        foreach (var document in documents)
        {
            var chunks = new List<TextChunk>();
            await using var read = Command(lease.Connection, transaction, "SELECT id,ordinal,context,text,start_offset,end_offset,token_count,input_hash,page FROM chunks WHERE document_id=$id ORDER BY ordinal", ("$id", document.Id));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) chunks.Add(new TextChunk(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt32(8)));
            work.Add(new EmbeddingWork(document.Id, document.Revision, document.Generation, chunks));
        }
        return work;
    }
    public async ValueTask<float[]?> CachedAsync(string spaceId, string inputHash, CancellationToken cancellationToken = default)
    {
        await using var lease = await index.OpenReadConnectionAsync(cancellationToken);
        await using var read = Command(lease.Connection, null, "SELECT vector FROM embedding_cache WHERE space_id=$space AND input_hash=$hash", ("$space", spaceId), ("$hash", inputHash));
        if (await read.ExecuteScalarAsync(cancellationToken) is not byte[] bytes) return null;
        var vector = new float[bytes.Length / sizeof(float)]; Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length); return vector;
    }
    public async ValueTask<bool> SaveAsync(EmbeddingSpace space, EmbeddingWork work, IReadOnlyDictionary<string, float[]> vectors, CancellationToken cancellationToken = default)
    {
        using var documentLock = await PublicationCoordinator.AcquireDocumentLockAsync(state, work.DocumentId, cancellationToken);
        var vectorRegistry = SecondBrain.Storage.Retrieval.VectorSnapshotRegistry.For(index);
        using var vectorPublication = await vectorRegistry.EnterAsync(cancellationToken);
        bool committed;
        var publishIndexed = false;
        try
        {
        committed = await index.QueueWriteAsync(async (connection, transaction, token) =>
        {
            // Validate authoritative fences inside the index transaction under the shared document lock.
            await using (var lease = await state.OpenReadConnectionAsync(token))
            await using (var current = Command(lease.Connection, null,
                "SELECT d.revision,f.generation_json,d.status,EXISTS(SELECT 1 FROM embedding_spaces WHERE id=$space AND status='active') FROM documents d JOIN publication_fences f ON f.document_id=d.id WHERE d.id=$id", ("$id", work.DocumentId), ("$space", space.Id)))
            await using (var reader = await current.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token) || reader.GetInt64(0) != work.Revision || reader.GetString(1) != work.GenerationJson || reader.GetString(2) is "deleted" or "purged" or "failed" or "skipped" or "needs_ocr") return false;
                publishIndexed = reader.GetInt64(3) != 0;
            }
            foreach (var chunk in work.Chunks)
            {
                if (!vectors.TryGetValue(chunk.Id, out var vector) || vector.Length != space.Dimensions || vector.Any(value => !float.IsFinite(value))) throw new ArgumentException("Invalid vector.");
                var norm = Math.Sqrt(vector.Sum(value => (double)value * value)); if (norm == 0) throw new ArgumentException("Zero vector.");
                var normalized = vector.Select(value => (float)(value / norm)).ToArray();
                var bytes = new byte[normalized.Length * sizeof(float)]; Buffer.BlockCopy(normalized, 0, bytes, 0, bytes.Length);
                await using var cache = Command(connection, transaction,
                    "INSERT OR IGNORE INTO embedding_cache(space_id,input_hash,vector,created_at) VALUES($space,$hash,$bytes,$now)", ("$space", space.Id), ("$hash", chunk.InputHash), ("$bytes", bytes), ("$now", Now()));
                await cache.ExecuteNonQueryAsync(token);
                await using var owner = Command(connection, transaction,
                    "INSERT OR IGNORE INTO embedding_cache_owners(input_hash,document_id) VALUES($hash,$id)", ("$hash", chunk.InputHash), ("$id", work.DocumentId));
                await owner.ExecuteNonQueryAsync(token);
                await using var insert = Command(connection, transaction,
                    "INSERT OR REPLACE INTO chunk_vectors(space_id,chunk_rowid,vector) SELECT $space,rowid,$bytes FROM chunks WHERE id=$id AND revision=$revision AND input_hash=$hash", ("$space", space.Id), ("$bytes", bytes), ("$id", chunk.Id), ("$revision", work.Revision), ("$hash", chunk.InputHash));
                if (await insert.ExecuteNonQueryAsync(token) != 1) throw new StaleEmbeddingWorkException();
            }
            await using (var missing = Command(connection, transaction,
                "SELECT COUNT(*) FROM chunks c WHERE c.document_id=$id AND NOT EXISTS(SELECT 1 FROM chunk_vectors v WHERE v.chunk_rowid=c.rowid AND v.space_id=$space)", ("$id", work.DocumentId), ("$space", space.Id)))
                if (Convert.ToInt64(await missing.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0) throw new StaleEmbeddingWorkException();
            if (publishIndexed)
            {
                await using var indexed = Command(connection, transaction,
                    "UPDATE indexed_documents SET status='indexed' WHERE document_id=$id AND revision=$revision AND status='indexed_partial'", ("$id", work.DocumentId), ("$revision", work.Revision));
                await indexed.ExecuteNonQueryAsync(token);
            }
            return true;
        }, cancellationToken);
        }
        catch (StaleEmbeddingWorkException) { return false; }
        if (committed)
        {
            vectorRegistry.Committed(work.DocumentId);
            await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                if (publishIndexed)
                {
                    await using var indexed = Command(connection, transaction,
                        "UPDATE documents SET status='indexed' WHERE id=$id AND revision=$revision AND status='indexed_partial'", ("$id", work.DocumentId), ("$revision", work.Revision));
                    await indexed.ExecuteNonQueryAsync(token);
                }
                await using var jobs = Command(connection, transaction, "UPDATE jobs SET status='completed',updated_at=$now WHERE document_id=$id AND revision=$revision AND kind='embed' AND status NOT IN ('cancelled','failed')", ("$id", work.DocumentId), ("$revision", work.Revision), ("$now", Now()));
                return await jobs.ExecuteNonQueryAsync(token);
            }, cancellationToken);
        }
        return committed;
    }
    private sealed class StaleEmbeddingWorkException : Exception;
    public async ValueTask ReconcileActiveAsync(string spaceId, CancellationToken cancellationToken = default)
    {
        // Index publication and authoritative status are separate durable commits. An
        // interruption between them leaves complete vectors but no pending embedding work.
        var candidates = new List<(string Id, long Revision, string Generation)>();
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        await using (var read = Command(lease.Connection, null,
            "SELECT d.id,d.revision,f.generation_json FROM documents d JOIN publication_fences f ON f.document_id=d.id WHERE d.status='indexed_partial' AND d.publish_state='published'"))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) candidates.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        foreach (var candidate in candidates)
        {
            using var documentGate = await PublicationCoordinator.AcquireDocumentLockAsync(state, candidate.Id, cancellationToken);
            using var vectorGate = await SecondBrain.Storage.Retrieval.VectorSnapshotRegistry.For(index).EnterAsync(cancellationToken);
            await using (var lease = await index.OpenReadConnectionAsync(cancellationToken))
            await using (var read = Command(lease.Connection, null,
                "SELECT 1 FROM indexed_documents d JOIN indexed_document_generations g ON g.document_id=d.document_id WHERE d.document_id=$id AND d.revision=$revision AND g.generation_json=$generation AND d.status='indexed' AND EXISTS(SELECT 1 FROM chunks c WHERE c.document_id=d.document_id) AND NOT EXISTS(SELECT 1 FROM chunks c WHERE c.document_id=d.document_id AND NOT EXISTS(SELECT 1 FROM chunk_vectors v WHERE v.chunk_rowid=c.rowid AND v.space_id=$space))",
                ("$id", candidate.Id), ("$revision", candidate.Revision), ("$generation", candidate.Generation), ("$space", spaceId)))
                if (await read.ExecuteScalarAsync(cancellationToken) is null) continue;
            await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var repair = Command(connection, transaction,
                    "UPDATE documents SET status='indexed' WHERE id=$id AND revision=$revision AND status='indexed_partial' AND publish_state='published' AND EXISTS(SELECT 1 FROM publication_fences WHERE document_id=$id AND generation_json=$generation) AND EXISTS(SELECT 1 FROM embedding_spaces WHERE id=$space AND status='active')",
                    ("$id", candidate.Id), ("$revision", candidate.Revision), ("$generation", candidate.Generation), ("$space", spaceId));
                if (await repair.ExecuteNonQueryAsync(token) == 0) return 0;
                await using var jobs = Command(connection, transaction,
                    "UPDATE jobs SET status='completed',updated_at=$now WHERE document_id=$id AND revision=$revision AND kind='embed' AND status NOT IN ('cancelled','failed')",
                    ("$id", candidate.Id), ("$revision", candidate.Revision), ("$now", Now()));
                return await jobs.ExecuteNonQueryAsync(token);
            }, cancellationToken);
        }
    }
    public async ValueTask<bool> ActivateAsync(string spaceId, CancellationToken cancellationToken = default)
    {
        if ((await ActiveAsync(cancellationToken))?.Id == spaceId)
        {
            await ReconcileActiveAsync(spaceId, cancellationToken);
            return true;
        }
        using var admissionGate = await PublicationCoordinator.AcquirePublicationGateAsync(state, cancellationToken);
        var registry = SecondBrain.Storage.Retrieval.VectorSnapshotRegistry.For(index);
        using var vectorGate = await registry.EnterAsync(cancellationToken);
        var activated = await index.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var authoritative = new List<(string Id, long Revision, string Generation)>();
            await using (var reservedLease = await state.OpenReadConnectionAsync(token))
            await using (var reserved = Command(reservedLease.Connection, null, "SELECT 1 FROM meta WHERE key LIKE 'mutation_document_reservation:%' LIMIT 1"))
                if (await reserved.ExecuteScalarAsync(token) is not null) return false;
            await using (var lease = await state.OpenReadConnectionAsync(token))
            await using (var read = Command(lease.Connection, null,
                $"SELECT d.id,d.revision,f.generation_json,d.publish_state,d.status FROM documents d JOIN publication_fences f ON f.document_id=d.id WHERE d.status NOT IN ({DocumentStatusSql.Unavailable},'failed','skipped','needs_ocr')"))
            await using (var reader = await read.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                {
                    if (reader.GetString(3) != "published" || reader.GetString(4) is "queued" or "processing") return false;
                    authoritative.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
                }
            foreach (var document in authoritative)
            {
                await using var coverage = Command(connection, transaction,
                    "SELECT 1 FROM indexed_documents d JOIN indexed_document_generations g ON g.document_id=d.document_id WHERE d.document_id=$id AND d.revision=$revision AND g.generation_json=$generation AND EXISTS(SELECT 1 FROM chunks c WHERE c.document_id=d.document_id) AND NOT EXISTS(SELECT 1 FROM chunks c WHERE c.document_id=d.document_id AND NOT EXISTS(SELECT 1 FROM chunk_vectors v WHERE v.chunk_rowid=c.rowid AND v.space_id=$space))", ("$id", document.Id), ("$revision", document.Revision), ("$generation", document.Generation), ("$space", spaceId));
                if (await coverage.ExecuteScalarAsync(token) is null) return false;
            }
            // Admission is frozen until both descriptor activation and projection statuses finish.
            await state.QueueWriteAsync(async (stateConnection, stateTransaction, stateToken) =>
            {
                await using var retire = Command(stateConnection, stateTransaction, "UPDATE embedding_spaces SET status='retired',retired_at=$now WHERE status='active' AND id<>$space", ("$space", spaceId), ("$now", Now())); await retire.ExecuteNonQueryAsync(stateToken);
                await using var activate = Command(stateConnection, stateTransaction, "UPDATE embedding_spaces SET status='active',activated_at=COALESCE(activated_at,$now) WHERE id=$space AND status IN ('building','active')", ("$space", spaceId), ("$now", Now()));
                if (await activate.ExecuteNonQueryAsync(stateToken) != 1) throw new InvalidOperationException("Embedding space cannot activate.");
                foreach (var document in authoritative)
                {
                    await using var status = Command(stateConnection, stateTransaction, "UPDATE documents SET status='indexed' WHERE id=$id AND revision=$revision AND status='indexed_partial'", ("$id", document.Id), ("$revision", document.Revision)); await status.ExecuteNonQueryAsync(stateToken);
                }
                return 0;
            }, token);
            foreach (var document in authoritative)
            {
                await using var status = Command(connection, transaction, "UPDATE indexed_documents SET status='indexed' WHERE document_id=$id AND revision=$revision AND status='indexed_partial'", ("$id", document.Id), ("$revision", document.Revision)); await status.ExecuteNonQueryAsync(token);
            }
            return true;
        }, cancellationToken);
        // Switching space invalidates by descriptor on the next acquisition; SQL vectors did not change.
        return activated;
    }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static EmbeddingSpace ReadSpace(DbDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5));
    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) => SqliteDocumentRepository.Command(connection, transaction, sql, parameters);
}
