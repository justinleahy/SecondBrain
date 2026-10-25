using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Storage.Sources;

public sealed partial class SqliteCalendarSubscriptionRepository
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStateStore, SemaphoreSlim> PurgeGates = new();

    public async ValueTask PurgeAsync(string id, IngestionAuthority actor, CancellationToken token = default)
    {
        if (files is null) throw new InvalidOperationException("Source purge requires managed files.");
        await StageRemovalAsync(id, actor, true, token);
        await CompletePurgeAsync(id, token);
    }

    public async ValueTask<bool> ExpireAsync(string id, RetentionExpiry expiry, CancellationToken token = default)
    {
        expiry.Validate();
        if (files is null) throw new InvalidOperationException("Source expiry requires managed files.");
        if (await StageRemovalAsync(id, null, true, token, expiry) is null) return false;
        await CompletePurgeAsync(id, token);
        return true;
    }

    public async ValueTask<int> RecoverPurgesAsync(CancellationToken token = default)
    {
        if (files is null || publications is null) return 0;
        string[] ids;
        await using (var read = await state.OpenReadConnectionAsync(token))
            ids = (await read.Connection.QueryAsync<string>(Sql("SELECT source_id FROM calendar_source_purges WHERE completed_at IS NULL ORDER BY COALESCE(last_attempt_at,requested_at),source_id LIMIT 32", null, null, token))).ToArray();
        var completed = 0;
        foreach (var id in ids)
        {
            try { await CompletePurgeAsync(id, token); completed++; }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or IngestionConflictException)
            {
                // Keep the durable intent. An operator may repair an unreadable/conflicting owned file;
                // one failed source must not prevent recovery of the other accepted purges.
            }
        }
        return completed;
    }

    private async ValueTask CompletePurgeAsync(string id, CancellationToken token)
    {
        var gate = PurgeGates.GetValue(state, static _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            PurgeWork? work;
            await using (var read = await state.OpenReadConnectionAsync(token))
                work = await read.Connection.QuerySingleOrDefaultAsync<PurgeWork>(Sql("""
                    SELECT p.operation_id Operation,c.container_id Container FROM calendar_source_purges p
                    JOIN calendar_subscriptions c ON c.source_id=p.source_id WHERE p.source_id=@id AND p.completed_at IS NULL
                    """, new { id }, null, token));
            if (work is null) return;
            await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                await connection.ExecuteAsync(Sql("UPDATE calendar_source_purges SET last_attempt_at=@now,error_code=NULL WHERE source_id=@id AND completed_at IS NULL", new { id, now = Format(clock.GetUtcNow()) }, transaction, ct));
                return 0;
            }, token);
            await publications!.RecoverCalendarAsync(work.Container, token);
            await files!.PurgeOwnedCalendarFilesAsync(work.Container, work.Operation, token);

            PurgeRoot? root;
            await using (var read = await state.OpenReadConnectionAsync(token))
                root = await read.Connection.QuerySingleOrDefaultAsync<PurgeRoot>(Sql("""
                    SELECT d.revision Revision,d.type Type,d.type_origin TypeOrigin,d.status Status,f.generation_json Generation
                    FROM documents d JOIN publication_fences f ON f.document_id=d.id WHERE d.id=@container
                    """, new { container = work.Container }, null, token));
            if (root is not null && root.Status != "purged")
            {
                var projectionId = "source-purge-projection:" + work.Operation;
                var projection = JsonSerializer.SerializeToUtf8Bytes(new TextProjection("", "", [0], [], [], files.GetLifecyclePath(work.Container, ".ics", "purge")));
                await state.QueueWriteAsync(async (connection, transaction, ct) =>
                {
                    await connection.ExecuteAsync(Sql("""
                        INSERT OR IGNORE INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@projectionId,@hash,@projection,@refs,@now)
                        """, new { projectionId, hash = Convert.ToHexStringLower(SHA256.HashData(projection)), projection, refs = JsonSerializer.Serialize(new[] { work.Container }), now = Format(clock.GetUtcNow()) }, transaction, ct));
                    return 0;
                }, token);
                await files.ApplyLifecycleAsync(work.Container, null, ".ics", "purge", null, [], new MutationFinalization
                {
                    MutationIdentity = "source-purge:" + work.Operation, DocumentId = work.Container,
                    RevisionBefore = root.Revision, RevisionAfter = root.Revision + 1, RevisionCause = "purge", GenerationJson = root.Generation,
                    SuppressDocument = true, PurgeDocumentLineage = true,
                    DocumentMetadata = new("[purged]", root.Type, root.TypeOrigin, "{}", "{}", "purged", projectionId, UpdateOccurred: true)
                }, token);
                if (await publications.PublishCurrentAsync(new(work.Container, root.Revision + 1, root.Generation), token) != PublicationOutcome.Published)
                    throw new IngestionConflictException("calendar-purge-pending");
            }
            await publications.RecoverCalendarAsync(work.Container, token);
            using var publicationGate = await SecondBrain.Storage.Durability.PublicationCoordinator.AcquirePublicationGateAsync(state, token);
            await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                if (await connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM documents WHERE source_id=@id AND status<>'purged'", new { id }, transaction, ct)) != 0)
                    throw new IngestionConflictException("calendar-purge-pending");
                await connection.ExecuteAsync(Sql("""
                    UPDATE payloads SET bytes=X'',sha256='',redacted_at=@now WHERE EXISTS(SELECT 1 FROM json_each(document_refs_json) WHERE value=@container);
                    UPDATE calendar_subscriptions SET protected_url='',snapshot_hash=NULL,snapshot_path=NULL,etag=NULL,last_modified=NULL,
                      rejected_hash=NULL,rejected_generation=NULL,error_code=NULL WHERE source_id=@id;
                    UPDATE calendar_source_purges SET completed_at=@now,error_code=NULL WHERE source_id=@id;
                    """, new { id, container = work.Container, now = Format(clock.GetUtcNow()) }, transaction, ct));
                return 0;
            }, token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or IngestionConflictException)
        {
            await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                await connection.ExecuteAsync(Sql("UPDATE calendar_source_purges SET error_code='calendar_purge_pending' WHERE source_id=@id AND completed_at IS NULL", new { id }, transaction, ct));
                return 0;
            }, token);
            throw;
        }
        finally { gate.Release(); }
    }

    private sealed class PurgeWork { public string Operation { get; set; } = ""; public string Container { get; set; } = ""; }
    private sealed class PurgeRoot
    {
        public long Revision { get; set; }
        public string Type { get; set; } = "";
        public string TypeOrigin { get; set; } = "";
        public string Status { get; set; } = "";
        public string Generation { get; set; } = "{}";
    }
}
