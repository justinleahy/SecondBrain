using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Sources;

public sealed partial class SqliteFolderDocumentRepository
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStateStore, ConcurrentDictionary<string, SemaphoreSlim>> SourceGates = new();

    private async ValueTask<IDisposable> AcquireSourceGateAsync(string id, CancellationToken token)
    {
        var gate = SourceGates.GetValue(state, static _ => new()).GetOrAdd(id, static _ => new(1, 1));
        await gate.WaitAsync(token);
        return new SourceGateLease(gate);
    }
    private sealed class SourceGateLease(SemaphoreSlim gate) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; gate.Release(); } }
    }
    private async ValueTask EnsureActiveAsync(string id, CancellationToken token)
    {
        await using var read = await state.OpenReadConnectionAsync(token);
        if (await read.Connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM sources WHERE id=@id AND status='active'", new { id }, null, token)) != 1)
            throw new IngestionConflictException("source-disabled");
    }

    public async ValueTask PurgeAsync(string sourceId, IngestionAuthority authority, CancellationToken cancellationToken = default)
    {
        if (files is null) throw new InvalidOperationException("Managed files are required for source purge.");
        using var sourceGate = await AcquireSourceGateAsync(sourceId, cancellationToken);
        using (await PublicationCoordinator.AcquirePublicationGateAsync(state, cancellationToken))
        await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority, Scope.Admin, DateTimeOffset.UtcNow,
                sessionOnly: true, stepUpWindow: TimeSpan.FromMinutes(optionsMonitor?.CurrentValue.Auth.StepUpMinutes ?? options?.Value.Auth.StepUpMinutes ?? 10), cancellationToken: token);
            if (sourceId == "inbox") throw new IngestionConflictException("protected-source");
            if (await connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM sources WHERE id=@sourceId AND kind='folder'", new { sourceId }, transaction, token)) != 1)
                throw new IngestionConflictException("not-found");
            if (await connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM folder_source_purges WHERE source_id=@sourceId", new { sourceId }, transaction, token)) != 0) return 0;
            var now = Now();
            await connection.ExecuteAsync(Sql("""
                INSERT INTO folder_source_purges(source_id,operation_id,requested_at) VALUES(@sourceId,@operation,@now);
                UPDATE sources SET status='removed' WHERE id=@sourceId;
                DELETE FROM folder_scan_candidates WHERE source_id=@sourceId;
                DELETE FROM meta WHERE key='folder-reconciliation:'||@sourceId;
                UPDATE documents SET status='removed_from_source',publish_state='publishing',
                  updated_at=CASE WHEN status='removed_from_source' THEN updated_at ELSE @now END
                  WHERE source_id=@sourceId AND status NOT IN ('deleted','purged');
                UPDATE jobs SET status='cancelled',lease_until=NULL,updated_at=@now WHERE document_id IN
                  (SELECT id FROM documents WHERE source_id=@sourceId) AND status IN ('queued','running');
                """, new { sourceId, operation = Ulid.NewUlid().ToString(), now }, transaction, token));
            return 0;
        }, cancellationToken);
        await CompleteSourcePurgeAsync(sourceId, cancellationToken);
    }

    public async ValueTask<int> RecoverPurgesAsync(CancellationToken cancellationToken = default)
    {
        if (files is null) return 0;
        string[] ids;
        await using (var read = await state.OpenReadConnectionAsync(cancellationToken))
            ids = (await read.Connection.QueryAsync<string>(Sql("SELECT source_id FROM folder_source_purges WHERE completed_at IS NULL ORDER BY COALESCE(last_attempt_at,requested_at),source_id LIMIT 32", null, null, cancellationToken))).ToArray();
        var completed = 0;
        foreach (var id in ids)
        {
            using var sourceGate = await AcquireSourceGateAsync(id, cancellationToken);
            try { await CompleteSourcePurgeAsync(id, cancellationToken); completed++; }
            catch (Exception error) when (IsPurgeFailure(error)) { }
        }
        return completed;
    }

    private async ValueTask CompleteSourcePurgeAsync(string sourceId, CancellationToken token)
    {
        string? operation;
        await using (var read = await state.OpenReadConnectionAsync(token))
            operation = await read.Connection.QuerySingleOrDefaultAsync<string>(Sql("SELECT operation_id FROM folder_source_purges WHERE source_id=@sourceId AND completed_at IS NULL", new { sourceId }, null, token));
        if (operation is null) return;
        try
        {
            await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                await connection.ExecuteAsync(Sql("UPDATE folder_source_purges SET last_attempt_at=@now,error_code=NULL WHERE source_id=@sourceId", new { sourceId, now = Now() }, transaction, ct)); return 0;
            }, token);
            await publication.RecoverAsync(token);
            // Bounded pages rotate conflicts behind unattempted roots. Container purge covers its children.
            for (var page = 0; page < 16; page++)
            {
                string[] ids;
                await using (var read = await state.OpenReadConnectionAsync(token))
                    ids = (await read.Connection.QueryAsync<string>(Sql("""
                        SELECT d.id FROM documents d LEFT JOIN folder_source_purge_attempts a ON a.document_id=d.id
                        WHERE d.source_id=@sourceId AND d.status<>'purged'
                          AND NOT EXISTS(SELECT 1 FROM documents parent WHERE parent.id=d.container_document_id AND parent.source_id=@sourceId AND parent.status<>'purged')
                        ORDER BY COALESCE(a.last_attempt_at,''),d.id LIMIT 32
                        """, new { sourceId }, null, token))).ToArray();
                if (ids.Length == 0) break;
                var failed = false;
                foreach (var id in ids)
                {
                    await state.QueueWriteAsync(async (connection, transaction, ct) =>
                    {
                        await connection.ExecuteAsync(Sql("INSERT INTO folder_source_purge_attempts(document_id,last_attempt_at) VALUES(@id,@now) ON CONFLICT(document_id) DO UPDATE SET last_attempt_at=excluded.last_attempt_at", new { id, now = Now() }, transaction, ct)); return 0;
                    }, token);
                    try { await PurgeSourceDocumentAsync(id, operation, token); }
                    catch (Exception error) when (IsPurgeFailure(error)) { failed = true; }
                }
                if (failed && ids.Length < 32) break;
            }
            await publication.RecoverAsync(token);
            await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                if (await connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM documents WHERE source_id=@sourceId AND (status<>'purged' OR publish_state<>'published')", new { sourceId }, transaction, ct)) != 0)
                    throw new IngestionConflictException("folder-purge-pending");
                await connection.ExecuteAsync(Sql("""
                    UPDATE folder_source_purges SET completed_at=@now,error_code=NULL WHERE source_id=@sourceId;
                    DELETE FROM folder_source_purge_attempts WHERE document_id IN (SELECT id FROM documents WHERE source_id=@sourceId);
                    DELETE FROM meta WHERE key='source-scan-error:'||@sourceId;
                    """, new { sourceId, now = Now() }, transaction, ct)); return 0;
            }, token);
        }
        catch (Exception error) when (IsPurgeFailure(error))
        {
            await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                await connection.ExecuteAsync(Sql("""
                    UPDATE folder_source_purges SET error_code='folder_purge_pending' WHERE source_id=@sourceId AND completed_at IS NULL;
                    INSERT INTO meta(key,value) VALUES('source-scan-error:'||@sourceId,@error) ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                    """, new { sourceId, error = JsonSerializer.Serialize(new { error = "folder_purge_pending", at = Now() }) }, transaction, ct)); return 0;
            }, token);
            throw;
        }
    }

    private async ValueTask PurgeSourceDocumentAsync(string id, string operation, CancellationToken token)
    {
        // This also replays interrupted file batches and finalization from the accepted operation.
        await files!.PurgeOwnedDocumentFilesAsync(id, operation, token);
        await publication.RecoverAsync(token);
        PurgeDocument? document;
        await using (var read = await state.OpenReadConnectionAsync(token))
            document = await read.Connection.QuerySingleOrDefaultAsync<PurgeDocument>(Sql("""
                SELECT d.revision Revision,d.type Type,d.type_origin TypeOrigin,d.status Status,f.generation_json Generation
                FROM documents d JOIN publication_fences f ON f.document_id=d.id WHERE d.id=@id
                """, new { id }, null, token));
        if (document is null || document.Status == "purged") return;
        var payload = "folder-purge-projection:" + operation + ":" + id;
        var projection = JsonSerializer.SerializeToUtf8Bytes(new TextProjection("", "", [0], [], [], files.GetLifecyclePath(id, ".bin", "purge")));
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            await connection.ExecuteAsync(Sql("INSERT OR IGNORE INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@payload,@hash,@projection,@refs,@now)",
                new { payload, hash = Convert.ToHexStringLower(SHA256.HashData(projection)), projection, refs = JsonSerializer.Serialize(new[] { id }), now = Now() }, transaction, ct)); return 0;
        }, token);
        await files.ApplyLifecycleAsync(id, null, ".bin", "purge", null, [], new MutationFinalization
        {
            MutationIdentity = "folder-source-purge:" + operation + ":" + id, DocumentId = id,
            RevisionBefore = document.Revision, RevisionAfter = document.Revision + 1, RevisionCause = "purge", GenerationJson = document.Generation,
            PurgeDocumentLineage = true, SuppressDocument = true,
            DocumentMetadata = new("[purged]", document.Type, document.TypeOrigin, "{}", "{}", "purged", payload, UpdateOccurred: true)
        }, token);
        if (await publication.PublishCurrentAsync(new(id, document.Revision + 1, document.Generation), token) != PublicationOutcome.Published)
            throw new IngestionConflictException("folder-purge-pending");
    }
    private static bool IsPurgeFailure(Exception error) => error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or IngestionConflictException;
    private static CommandDefinition Sql(string sql, object? parameters, System.Data.Common.DbTransaction? transaction, CancellationToken token) => new(sql, parameters, transaction, cancellationToken: token);
    private sealed class PurgeDocument
    {
        public long Revision { get; set; }
        public string Type { get; set; } = "";
        public string TypeOrigin { get; set; } = "";
        public string Status { get; set; } = "";
        public string Generation { get; set; } = "{}";
    }
}
