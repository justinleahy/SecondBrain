using System.Data.Common;
using System.Globalization;
using Dapper;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;

namespace SecondBrain.Storage.Durability;

public sealed partial class PublicationCoordinator
{
    private async Task<PublicationFence?> PrepareSubscriptionRootAsync(DbConnection connection, DbTransaction transaction, CalendarSubscriptionPublication publication, CancellationToken token)
    {
        var work = publication.Work;
        var now = clock.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var source = await connection.QuerySingleOrDefaultAsync<SubscriptionRoot>(Sql("""
            SELECT s.name Name,c.snapshot_hash Hash,c.snapshot_path Path
            FROM calendar_subscriptions c JOIN sources s ON s.id=c.source_id
            WHERE c.source_id=@SourceId AND c.container_id=@ContainerId AND c.generation=@Generation AND c.lease_id=@LeaseId AND c.lease_until>@now
              AND (s.status='active' OR @localOnly=1 AND s.status='disabled')
            """, new { work.SourceId, work.ContainerId, work.Generation, work.LeaseId, now, localOnly = work.MaterializationOnly ? 1 : 0 }, transaction, token));
        if (source is null) return null;
        if (publication.SnapshotHash.Length != 64 || !publication.SnapshotHash.All(char.IsAsciiHexDigit) || publication.SnapshotLength is < 1 or > 50L * 1024 * 1024 || publication.ETag?.Length > 512)
            throw new CalendarSubscriptionException("calendar_snapshot_invalid");
        if (work.MaterializationOnly && (publication.SnapshotHash != source.Hash || publication.SnapshotPath != source.Path)) return null;
        if (source.Hash != publication.SnapshotHash || source.Path != publication.SnapshotPath)
        {
            // New bytes must already be an immutable, journaled original owned by this container.
            var owned = await connection.ExecuteScalarAsync<long>(Sql("""
                SELECT COUNT(*) FROM mutations m JOIN payloads p ON p.id=m.payload_id
                WHERE m.kind='import' AND m.status='finalized' AND m.destination=@path AND p.sha256=@hash AND length(p.bytes)=@length
                  AND EXISTS(SELECT 1 FROM json_each(p.document_refs_json) WHERE value=@id)
                """, new { path = publication.SnapshotPath, hash = publication.SnapshotHash, length = publication.SnapshotLength, id = work.ContainerId }, transaction, token));
            if (owned == 0) throw new CalendarSubscriptionException("calendar_snapshot_unowned");
        }
        var prior = await connection.QuerySingleOrDefaultAsync<SubscriptionDocument>(Sql("SELECT revision Revision,content_hash Hash,status Status FROM documents WHERE id=@id", new { id = work.ContainerId }, transaction, token));
        if (prior is not null && DocumentStatusNames.IsUnavailable(prior.Status)) return null;
        var changed = prior is null || prior.Hash != publication.SnapshotHash;
        var revision = prior is null ? 1 : prior.Revision + (changed ? 1 : 0);
        var generation = ChunkingProfile.FromOptions(optionsMonitor?.CurrentValue.Chunking).GenerationJson;
        var path = source.Hash == publication.SnapshotHash && source.Path is not null ? source.Path : publication.SnapshotPath;
        await connection.ExecuteAsync(Sql("""
            INSERT INTO documents(id,source_id,type,type_origin,props_json,props_original_json,title,mime,provenance,natural_key,content_hash,size_bytes,revision,publish_state,status,created_at,updated_at)
            VALUES(@id,@source,'file','format','{}','{}',@title,'text/calendar',@provenance,@natural,@hash,@length,@revision,'publishing','processing',@now,@now)
            ON CONFLICT(id) DO UPDATE SET content_hash=excluded.content_hash,size_bytes=excluded.size_bytes,revision=excluded.revision,
              publish_state=CASE WHEN @changed THEN 'publishing' ELSE documents.publish_state END,
              status=CASE WHEN @changed THEN 'processing' ELSE documents.status END,error=NULL,updated_at=CASE WHEN @changed THEN @now ELSE documents.updated_at END;
            INSERT INTO calendar_subscription_publications(container_id,source_id,source_generation,lease_id,snapshot_hash,snapshot_path,etag,last_modified,accepted_at,materialization_only)
            VALUES(@id,@source,@sourceGeneration,@lease,@hash,@path,@etag,@modified,@now,@localOnly)
            ON CONFLICT(container_id) DO UPDATE SET source_generation=excluded.source_generation,lease_id=excluded.lease_id,snapshot_hash=excluded.snapshot_hash,
              snapshot_path=excluded.snapshot_path,etag=excluded.etag,last_modified=excluded.last_modified,accepted_at=excluded.accepted_at,materialization_only=excluded.materialization_only;
            """, new { id = work.ContainerId, source = work.SourceId, title = source.Name, provenance = "calendar_subscription:" + work.SourceId,
                natural = "calendar_subscription:" + work.SourceId, hash = publication.SnapshotHash, length = publication.SnapshotLength, revision, now, changed,
                sourceGeneration = work.Generation, lease = work.LeaseId, path, etag = publication.ETag, modified = publication.Modified?.ToUniversalTime().ToString("O"), localOnly = work.MaterializationOnly ? 1 : 0 }, transaction, token));
        if (changed)
            await connection.ExecuteAsync(Sql("INSERT INTO revisions(document_id,revision,cause,content_hash,created_at) VALUES(@id,@revision,'calendar-refresh',@hash,@now)", new { id = work.ContainerId, revision, hash = publication.SnapshotHash, now }, transaction, token));
        if (changed)
            await SaveCalendarProjectionAsync(connection, transaction, work.ContainerId, revision, generation, new ChunkingProfile().Process(work.ContainerId, source.Name, "", path: path), now, token);
        else
            generation = await connection.ExecuteScalarAsync<string>(Sql("SELECT generation_json FROM publication_fences WHERE document_id=@id", new { id = work.ContainerId }, transaction, token)) ?? throw new IngestionConflictException("calendar-fence");
        return new(work.ContainerId, revision, generation);
    }

    private async Task FinishSubscriptionAsync(DbConnection connection, DbTransaction transaction, string containerId, CancellationToken token)
    {
        var pending = await connection.QuerySingleOrDefaultAsync<SubscriptionCompletion>(Sql("SELECT source_id SourceId,source_generation Generation,lease_id LeaseId,snapshot_hash Hash,snapshot_path Path,etag ETag,last_modified Modified,accepted_at Accepted,materialization_only LocalOnly FROM calendar_subscription_publications WHERE container_id=@containerId", new { containerId }, transaction, token));
        if (pending is null) return;
        var interval = await connection.ExecuteScalarAsync<int>(Sql("SELECT poll_interval_s FROM calendar_subscriptions WHERE source_id=@SourceId", pending, transaction, token));
        var now = clock.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var next = clock.GetUtcNow().AddSeconds(interval).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var affected = await connection.ExecuteAsync(Sql("""
            UPDATE calendar_subscriptions SET
              last_change_at=CASE WHEN @LocalOnly=0 AND (snapshot_hash IS NULL OR snapshot_hash<>@Hash) THEN @Accepted ELSE last_change_at END,
              last_success_at=CASE WHEN @LocalOnly=0 THEN @Accepted ELSE last_success_at END,
              error_code=CASE WHEN @LocalOnly=0 THEN NULL ELSE error_code END,
              failure_count=CASE WHEN @LocalOnly=0 THEN 0 ELSE failure_count END,
              rejected_hash=CASE WHEN @LocalOnly=0 THEN NULL ELSE rejected_hash END,rejected_generation=CASE WHEN @LocalOnly=0 THEN NULL ELSE rejected_generation END,
              snapshot_hash=@Hash,snapshot_path=@Path,etag=CASE WHEN @LocalOnly=0 THEN @ETag ELSE etag END,last_modified=CASE WHEN @LocalOnly=0 THEN @Modified ELSE last_modified END,
              lease_id=NULL,lease_until=NULL,next_attempt_at=CASE WHEN requested=1 THEN @now WHEN @LocalOnly=0 THEN @next ELSE next_attempt_at END
            WHERE source_id=@SourceId AND generation=@Generation AND lease_id=@LeaseId;
            """, new { pending.SourceId, pending.Generation, pending.LeaseId, pending.Hash, pending.Path, pending.ETag, pending.Modified, pending.Accepted, pending.LocalOnly, now, next }, transaction, token));
        if (affected != 1) throw new IngestionConflictException("calendar-source-fence");
        await connection.ExecuteAsync(Sql("DELETE FROM calendar_subscription_publications WHERE container_id=@containerId", new { containerId }, transaction, token));
        await connection.ExecuteAsync(Sql("DELETE FROM calendar_materialization_retry WHERE source_id=@SourceId", pending, transaction, token));
    }
    private sealed class SubscriptionRoot { public string Name { get; set; } = ""; public string? Hash { get; set; } public string? Path { get; set; } }
    private sealed class SubscriptionDocument { public long Revision { get; set; } public string Hash { get; set; } = ""; public string Status { get; set; } = ""; }
    private sealed class SubscriptionCompletion
    {
        public string SourceId { get; set; } = ""; public long Generation { get; set; } public string LeaseId { get; set; } = "";
        public string Hash { get; set; } = ""; public string Path { get; set; } = ""; public string? ETag { get; set; } public string? Modified { get; set; }
        public string Accepted { get; set; } = ""; public int LocalOnly { get; set; }
    }
}
