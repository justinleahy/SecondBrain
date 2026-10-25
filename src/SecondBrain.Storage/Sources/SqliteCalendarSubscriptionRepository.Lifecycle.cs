using Dapper;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Sources;

public sealed partial class SqliteCalendarSubscriptionRepository
{
    // A staged batch is an accepted durable transaction. Finish it before changing
    // source authority; a not-yet-accepted download will then fail the generation fence.
    private async ValueTask UnderControlGateAsync(string id, Func<ValueTask> change, CancellationToken token)
    {
        while (true)
        {
            using var gate = await PublicationCoordinator.AcquirePublicationGateAsync(state, token);
            string? pending;
            await using (var lease = await state.OpenReadConnectionAsync(token))
                pending = await lease.Connection.ExecuteScalarAsync<string?>(Sql("SELECT container_id FROM calendar_batches WHERE source_id=@id AND phase='publishing'", new { id }, null, token));
            if (pending is null) { await change(); return; }
            gate.Dispose();
            if (publications is null || await publications.RecoverCalendarAsync(pending, token) != PublicationOutcome.Published)
                throw new IngestionConflictException("calendar-publication-pending");
        }
    }

    public async ValueTask RemoveAsync(string id, IngestionAuthority actor, CancellationToken token = default)
    {
        var root = await StageRemovalAsync(id, actor, false, token);
        if (root is not null) await publications!.RecoverCalendarAsync(root, token);
    }

    private async ValueTask<string?> StageRemovalAsync(string id, IngestionAuthority? actor, bool purge, CancellationToken token, RetentionExpiry? expiry = null)
    {
        if (publications is null) throw new InvalidOperationException("Source removal requires the publication coordinator.");
        string? root = null;
        await UnderControlGateAsync(id, async () =>
        {
            await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                if (actor is not null)
                    await IngestionAuthorityValidator.ValidateAsync(connection, transaction, actor, Scope.Admin, clock.GetUtcNow(),
                        sessionOnly: purge, stepUpWindow: purge ? TimeSpan.FromMinutes(10) : null, cancellationToken: ct);
                else if (expiry is null || !purge) throw new InvalidOperationException("Source removal requires authority.");
                root = await connection.ExecuteScalarAsync<string?>(Sql("SELECT c.container_id FROM calendar_subscriptions c JOIN sources s ON s.id=c.source_id WHERE s.id=@id AND s.kind='calendar_subscription'", new { id }, transaction, ct));
                if (root is null) throw new IngestionConflictException("not-found");
                if (expiry is not null && !await RetentionConditions.MatchesAsync(connection, transaction, root, expiry, ct))
                { root = null; return 0; }
                if (await connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM documents d JOIN meta m ON m.key='mutation_document_reservation:'||d.id WHERE d.source_id=@id", new { id }, transaction, ct)) != 0)
                    throw new IngestionConflictException("calendar-mutation-pending");
                var now = Format(clock.GetUtcNow());
                if (expiry is not null) await RetentionConditions.RecordAsync(connection, transaction, root, expiry, ct);
                if (purge)
                    await connection.ExecuteAsync(Sql("INSERT OR IGNORE INTO calendar_source_purges(source_id,operation_id,requested_at) VALUES(@id,@operation,@now)", new { id, operation = Ulid.NewUlid().ToString(), now }, transaction, ct));
                await connection.ExecuteAsync(Sql("""
                    UPDATE sources SET status='removed' WHERE id=@id;
                    UPDATE calendar_subscriptions SET generation=generation+1,lease_id=NULL,lease_until=NULL,requested=0,deep_requested=0,next_attempt_at=NULL WHERE source_id=@id;
                    UPDATE jobs SET status='cancelled',lease_until=NULL WHERE document_id IN (SELECT id FROM documents WHERE source_id=@id) AND status IN ('queued','running');
                    """, new { id }, transaction, ct));
                var current = await connection.QuerySingleOrDefaultAsync<RemovalRoot>(Sql("SELECT id Id,status Status FROM documents WHERE id=@root", new { root }, transaction, ct));
                if (current is not null && !DocumentStatusNames.IsUnavailable(current.Status))
                {
                    await connection.ExecuteAsync(Sql("""
                        UPDATE documents SET revision=revision+1,status='removed_from_source',publish_state='publishing',updated_at=@now WHERE id=@root;
                        INSERT INTO revisions(document_id,revision,cause,content_hash,created_at) SELECT id,revision,'source-remove',content_hash,@now FROM documents WHERE id=@root;
                        UPDATE document_projections SET revision=revision+1 WHERE document_id=@root;
                        """, new { root, now }, transaction, ct));
                    await CalendarLifecycle.ApplyAsync(connection, transaction, root, "source-remove", now, ct);
                }
                return 0;
            }, token);
        }, token);
        return root;
    }
    private sealed class RemovalRoot { public string Id { get; set; } = ""; public string Status { get; set; } = ""; }

    public async ValueTask<IReadOnlyList<CalendarRefreshWork>> ClaimMaterializationAsync(CalendarWindow window, int limit = 4, CancellationToken token = default)
    {
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit));
        var range = window.Validate();
        using var gate = await PublicationCoordinator.AcquirePublicationGateAsync(state, token);
        return await state.QueueWriteAsync<IReadOnlyList<CalendarRefreshWork>>(async (connection, transaction, ct) =>
        {
            var now = Format(clock.GetUtcNow());
            var rows = await connection.QueryAsync<WorkRow>(Sql("""
                SELECT c.source_id SourceId,c.container_id ContainerId,c.generation Generation,c.protected_url ProtectedUrl,c.etag ETag,c.last_modified Modified,
                  c.snapshot_hash SnapshotHash,c.snapshot_path SnapshotPath,c.rejected_hash RejectedHash,c.rejected_generation RejectedGeneration,0 Deep
                FROM calendar_subscriptions c JOIN sources s ON s.id=c.source_id
                LEFT JOIN calendar_coverage coverage ON coverage.container_id=c.container_id
                LEFT JOIN calendar_materialization_retry retry ON retry.source_id=c.source_id
                WHERE s.status IN ('active','disabled') AND c.snapshot_path IS NOT NULL AND (c.lease_id IS NULL OR c.lease_until<=@now)
                  AND NOT EXISTS(SELECT 1 FROM documents d WHERE d.id=c.container_id AND d.status IN ('deleted','purged','missing','removed_from_source'))
                  AND NOT EXISTS(SELECT 1 FROM meta WHERE key='mutation_document_reservation:'||c.container_id)
                  AND (retry.next_attempt_at IS NULL OR retry.next_attempt_at<=@now)
                  AND (coverage.container_id IS NULL OR coverage.covered_from<>@from OR coverage.covered_to<>@to OR coverage.time_zone<>@zone
                    OR EXISTS(SELECT 1 FROM calendar_batches b WHERE b.container_id=c.container_id AND b.phase='needs_materialization'))
                  AND NOT EXISTS(SELECT 1 FROM calendar_batches b WHERE b.container_id=c.container_id AND b.phase='publishing')
                ORDER BY c.source_id LIMIT @limit
                """, new { now, from = Format(range.From), to = Format(range.To), zone = range.Zone.Id, limit }, transaction, ct));
            var result = new List<CalendarRefreshWork>();
            foreach (var row in rows)
            {
                var lease = Ulid.NewUlid().ToString();
                await connection.ExecuteAsync(Sql("UPDATE calendar_subscriptions SET generation=generation+1,lease_id=@lease,lease_until=@until WHERE source_id=@id",
                    new { id = row.SourceId, lease, until = Format(clock.GetUtcNow().AddMinutes(4)) }, transaction, ct));
                result.Add(new(row.SourceId, row.ContainerId, row.Generation + 1, lease, row.ProtectedUrl, row.ETag, Parse(row.Modified), row.SnapshotHash, row.SnapshotPath, row.RejectedHash, row.RejectedGeneration, false, true));
            }
            return result;
        }, token);
    }

    private async ValueTask FailMaterializationAsync(CalendarRefreshWork work, string error, CancellationToken token)
    {
        if (!Errors.Contains(error)) throw new ArgumentException("Invalid calendar failure metadata.");
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            var changed = await connection.ExecuteAsync(Sql("UPDATE calendar_subscriptions SET lease_id=NULL,lease_until=NULL WHERE source_id=@SourceId AND generation=@Generation AND lease_id=@LeaseId AND NOT EXISTS(SELECT 1 FROM calendar_subscription_publications p WHERE p.source_id=@SourceId)", work, transaction, ct));
            if (changed != 0)
                await connection.ExecuteAsync(Sql("INSERT INTO calendar_materialization_retry(source_id,next_attempt_at,error_code) VALUES(@SourceId,@next,@error) ON CONFLICT(source_id) DO UPDATE SET next_attempt_at=excluded.next_attempt_at,error_code=excluded.error_code",
                    new { work.SourceId, next = Format(clock.GetUtcNow().AddMinutes(5)), error }, transaction, ct));
            return 0;
        }, token);
    }
    private sealed class SourceFence { public string Id { get; set; } = ""; public long Revision { get; set; } public string Generation { get; set; } = ""; }
}
