using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Ingestion;

public sealed class SqliteDocumentRetention(IStateStore state, IIndexStore index, IPublicationCoordinator publications,
    IMutationJournal journal, IManagedDocumentFiles files, ICalendarSubscriptionRepository calendars,
    IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock) : IDocumentRetention
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStateStore, SemaphoreSlim> Gates = new();

    public async ValueTask<DocumentRetentionResult> RunAsync(IngestionAuthority? actor = null, int limit = 16, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        var policy = options.CurrentValue.Retention; policy.Validate();
        if (actor is not null)
            await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await IngestionAuthorityValidator.ValidateAsync(connection, transaction, actor, Scope.Admin, clock.GetUtcNow(), cancellationToken: token);
                return 0;
            }, cancellationToken);
        var gate = Gates.GetValue(state, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await journal.RecoverRetentionPurgesAsync(cancellationToken);
            await calendars.RecoverPurgesAsync(cancellationToken);
            await publications.RecoverAsync(cancellationToken);
            await PurgeCleanup.RecoverAsync(state, index, cancellationToken);
            var identities = await CompleteLedgersAsync(cancellationToken);
            var now = clock.GetUtcNow(); var trash = now.AddDays(-policy.TrashDays); var removed = now.AddDays(-policy.RemovedDays);
            Candidate[] candidates;
            await using (var read = await state.OpenReadConnectionAsync(cancellationToken))
                candidates = (await read.Connection.QueryAsync<Candidate>(Sql("""
                    SELECT d.id Id,d.source_id Source,d.revision Revision,d.status Status,d.type Type,d.type_origin TypeOrigin,
                      d.provenance Provenance,d.content_hash Hash,d.container_document_id Parent,f.generation_json Generation,
                      json_extract(CASE WHEN p.redacted_at IS NULL THEN CAST(p.bytes AS TEXT) ELSE '{}' END,'$.Path') Path,
                      c.source_id Subscription
                    FROM documents d JOIN publication_fences f ON f.document_id=d.id
                    LEFT JOIN document_projections dp ON dp.document_id=d.id LEFT JOIN payloads p ON p.id=dp.payload_id
                    LEFT JOIN retention_removed_at r ON r.document_id=d.id LEFT JOIN calendar_subscriptions c ON c.container_id=d.id
                    LEFT JOIN retention_attempts a ON a.document_id=d.id
                    WHERE d.publish_state='published' AND ((d.status='deleted' AND julianday(d.deleted_at)<=julianday(@trash))
                      OR (d.status='removed_from_source' AND julianday(r.since)<=julianday(@removed)
                        AND NOT EXISTS(SELECT 1 FROM suppressions s WHERE s.scope='document' AND s.key=d.id)))
                      AND NOT EXISTS(SELECT 1 FROM documents parent WHERE parent.id=d.container_document_id AND parent.status IN ('deleted','purged','missing','removed_from_source'))
                      AND NOT EXISTS(SELECT 1 FROM meta WHERE key='mutation_document_reservation:'||d.id)
                    ORDER BY COALESCE(a.last_attempt_at,d.updated_at),d.id LIMIT @limit
                    """, new { trash = trash.ToString("O"), removed = removed.ToString("O"), limit }, null, cancellationToken))).ToArray();
            var purged = 0; var deferred = 0;
            foreach (var candidate in candidates)
            {
                await state.QueueWriteAsync(async (connection, transaction, token) =>
                {
                    await connection.ExecuteAsync(Sql("INSERT INTO retention_attempts(document_id,last_attempt_at) VALUES(@Id,@now) ON CONFLICT(document_id) DO UPDATE SET last_attempt_at=excluded.last_attempt_at",
                        new { candidate.Id, now = clock.GetUtcNow().ToString("O") }, transaction, token));
                    return 0;
                }, cancellationToken);
                try
                {
                    using var parentLock = candidate.Parent is null ? null : await PublicationCoordinator.AcquireDocumentLockAsync(state, candidate.Parent, cancellationToken);
                    var expiry = new RetentionExpiry(candidate.Status, candidate.Status == "deleted" ? trash : removed, Ulid.NewUlid().ToString());
                    bool completed;
                    if (candidate.Subscription is not null)
                        completed = await calendars.ExpireAsync(candidate.Subscription, expiry, cancellationToken);
                    else completed = await PurgeAsync(candidate, expiry, cancellationToken);
                    if (completed)
                    {
                        purged++;
                        await PurgeCleanup.CompleteAsync(state, index, candidate.Id, cancellationToken);
                        identities += await CompleteLedgersAsync(cancellationToken, candidate.Parent);
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or IngestionConflictException or MutationRevisionException)
                { deferred++; }
            }
            identities += await CompleteLedgersAsync(cancellationToken);
            return new(purged, identities, deferred, candidates.Length == limit || deferred != 0);
        }
        finally { gate.Release(); }
    }

    private async ValueTask<bool> PurgeAsync(Candidate candidate, RetentionExpiry expiry, CancellationToken token)
    {
        if (candidate.Parent is not null || !IsManaged(candidate.Path))
        {
            return await publications.PublishAsync(new(candidate.Id, candidate.Source, candidate.Revision, candidate.Generation, "purge", "[purged]", "", candidate.Provenance)
            {
                Type = candidate.Type, TypeOrigin = candidate.TypeOrigin, PropsJson = "{}", PropsOriginalJson = "{}", Status = "purged",
                Projection = new("", "", [0], [], []), Expiry = expiry, SizeBytes = 0
            }, token) == PublicationOutcome.Published;
        }
        var extension = Path.GetExtension(candidate.Path!);
        var payload = "retention-projection:" + candidate.Id + ":" + candidate.Revision;
        var projection = JsonSerializer.SerializeToUtf8Bytes(new TextProjection("", "", [0], [], [], files.GetLifecyclePath(candidate.Id, extension, "purge")));
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            await connection.ExecuteAsync(Sql("INSERT OR IGNORE INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@payload,@hash,@projection,@refs,@now)",
                new { payload, hash = Convert.ToHexStringLower(SHA256.HashData(projection)), projection, refs = JsonSerializer.Serialize(new[] { candidate.Id }), now = clock.GetUtcNow().ToString("O") }, transaction, ct));
            return 0;
        }, token);
        await files.ApplyLifecycleAsync(candidate.Id, candidate.Path, extension, "purge", candidate.Hash, [], new MutationFinalization
        {
            MutationIdentity = "retention-purge:" + expiry.OperationId, DocumentId = candidate.Id, RevisionBefore = candidate.Revision,
            RevisionAfter = candidate.Revision + 1, RevisionCause = "purge", GenerationJson = candidate.Generation, Expiry = expiry,
            PurgeDocumentLineage = true, SuppressDocument = candidate.Status == "deleted",
            DocumentMetadata = new("[purged]", candidate.Type, candidate.TypeOrigin, "{}", "{}", "purged", payload, UpdateOccurred: true)
        }, token);
        return await publications.PublishCurrentAsync(new(candidate.Id, candidate.Revision + 1, candidate.Generation), token) == PublicationOutcome.Published;
    }

    private bool IsManaged(string? path) => path is not null && new[] { "vault", "trash" }.Any(name =>
        Path.GetFullPath(path).StartsWith(Path.Combine(Path.GetFullPath(options.CurrentValue.DataRoot), name) + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private async ValueTask<int> CompleteLedgersAsync(CancellationToken token, string? heldParent = null)
    {
        Ledger[] rows;
        await using (var read = await state.OpenReadConnectionAsync(token))
            rows = (await read.Connection.QueryAsync<Ledger>(Sql("""
                SELECT r.document_id Id,r.reason Reason,d.container_document_id Parent FROM retention_purges r JOIN documents d ON d.id=r.document_id
                WHERE r.completed_at IS NULL AND d.status='purged' AND d.publish_state='published'
                  AND NOT EXISTS(SELECT 1 FROM calendar_source_purges p JOIN calendar_subscriptions c ON c.source_id=p.source_id WHERE c.container_id=d.id AND p.completed_at IS NULL)
                ORDER BY r.document_id LIMIT 256
                """, null, null, token))).ToArray();
        var expired = 0;
        foreach (var row in rows)
        {
            using var parentLock = row.Parent is null || row.Parent == heldParent ? null : await PublicationCoordinator.AcquireDocumentLockAsync(state, row.Parent, token);
            using var gate = await PublicationCoordinator.AcquirePublicationGateAsync(state, token);
            expired += await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                if (await connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM retention_purges WHERE document_id=@Id AND completed_at IS NULL", row, transaction, ct)) == 0) return 0;
                var count = 0;
                if (row.Reason == "removed_from_source")
                {
                    count = await connection.ExecuteScalarAsync<int>(Sql("""
                        SELECT COUNT(*) FROM documents d WHERE d.status='purged' AND (d.id=@Id OR d.container_document_id=@Id)
                          AND (d.natural_key IS NOT NULL OR EXISTS(SELECT 1 FROM occurrences o WHERE o.document_id=d.id)
                            OR EXISTS(SELECT 1 FROM calendar_members m WHERE m.document_id=d.id))
                        """, row, transaction, ct));
                    await connection.ExecuteAsync(Sql("""
                        UPDATE documents SET natural_key=NULL WHERE status='purged' AND (id=@Id OR container_document_id=@Id);
                        DELETE FROM occurrences WHERE document_id IN (SELECT id FROM documents WHERE status='purged' AND (id=@Id OR container_document_id=@Id));
                        DELETE FROM calendar_members WHERE document_id IN (SELECT id FROM documents WHERE status='purged' AND (id=@Id OR container_document_id=@Id));
                        DELETE FROM absences WHERE document_id IN (SELECT id FROM documents WHERE status='purged' AND (id=@Id OR container_document_id=@Id));
                        UPDATE calendar_batches SET phase='needs_materialization' WHERE container_id=(SELECT container_document_id FROM documents WHERE id=@Id) AND phase='published';
                        """, row, transaction, ct));
                }
                await connection.ExecuteAsync(Sql("UPDATE retention_purges SET completed_at=@now WHERE document_id=@Id", new { row.Id, now = clock.GetUtcNow().ToString("O") }, transaction, ct));
                await connection.ExecuteAsync(Sql("DELETE FROM retention_attempts WHERE document_id=@Id", row, transaction, ct));
                return count;
            }, token);
        }
        return expired;
    }

    private static CommandDefinition Sql(string sql, object? values, System.Data.Common.DbTransaction? transaction, CancellationToken token) => new(sql, values, transaction, cancellationToken: token);
    private sealed class Ledger { public string Id { get; set; } = ""; public string Reason { get; set; } = ""; public string? Parent { get; set; } }
    private sealed class Candidate
    {
        public string Id { get; set; } = ""; public string Source { get; set; } = ""; public long Revision { get; set; }
        public string Status { get; set; } = ""; public string Type { get; set; } = ""; public string TypeOrigin { get; set; } = "";
        public string Provenance { get; set; } = ""; public string Hash { get; set; } = ""; public string Generation { get; set; } = "{}";
        public string? Parent { get; set; } public string? Path { get; set; } public string? Subscription { get; set; }
    }
}
