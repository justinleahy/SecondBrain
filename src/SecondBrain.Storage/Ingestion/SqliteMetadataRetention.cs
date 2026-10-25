using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using System.Security.Cryptography;
using System.Text.Json;

namespace SecondBrain.Storage.Ingestion;

/// <summary>Removes temporary metadata while retaining every payload still owned by live state or a journal.</summary>
public sealed class SqliteMetadataRetention(IStateStore state, IIndexStore index, IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock) : IMetadataRetention
{
    public async ValueTask<MetadataRetentionResult> RunAsync(IngestionAuthority? actor = null, int limit = 256, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        var policy = options.CurrentValue.Retention;
        policy.Validate();
        var now = clock.GetUtcNow();
        var values = new { limit, now = now.ToString("O"), proposals = now.AddDays(-policy.ResolvedProposalDays).ToString("O"),
            payloads = now.AddDays(-policy.PayloadDays).ToString("O"), idempotency = now.AddHours(-policy.IdempotencyHours).ToString("O"), traces = now.AddDays(-policy.TraceDays).ToString("O") };
        using var gate = await PublicationCoordinator.AcquirePublicationGateAsync(state, cancellationToken);
        var result = await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            if (actor is not null) await IngestionAuthorityValidator.ValidateAsync(connection, transaction, actor, Scope.Admin, now, cancellationToken: token);
            var expired = (await connection.QueryAsync<string>(Sql("""
                SELECT p.id FROM pending_operations p LEFT JOIN chat_proposal_details d ON d.proposal_id=p.id
                WHERE p.status='pending' AND julianday(p.expires_at)<=julianday(@now)
                  AND NOT EXISTS(SELECT 1 FROM mutations m WHERE m.id=d.mutation_id AND m.status IN ('prepared','applied'))
                ORDER BY p.expires_at,p.id LIMIT @limit
                """, values, transaction, token))).ToArray();
            await connection.ExecuteAsync(Sql("""
                UPDATE pending_operations SET status='expired',resolved_at=@now WHERE id IN @ids AND status='pending';
                UPDATE chat_proposal_details SET failure_code='expired' WHERE proposal_id IN @ids;
                UPDATE tool_executions SET status='expired' WHERE status='pending_approval' AND id IN
                  (SELECT execution_id FROM pending_operations WHERE id IN @ids);
                """, new { values.now, ids = expired }, transaction, token));
            foreach (var id in expired)
            {
                var turn = await connection.ExecuteScalarAsync<string?>(Sql("SELECT turn_id FROM pending_operations WHERE id=@id", new { id }, transaction, token));
                if (turn is null) continue;
                var payload = Ulid.NewUlid().ToString();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, status = "expired", reason = "expired" });
                await connection.ExecuteAsync(Sql("""
                    INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@payload,@hash,@bytes,'[]',@now);
                    INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES(@payload,'chat-event',@turn);
                    INSERT INTO chat_events(turn_id,sequence,kind,payload_id,created_at)
                      SELECT @turn,COALESCE(MAX(sequence),0)+1,'proposal',@payload,@now FROM chat_events WHERE turn_id=@turn;
                    """, new { payload, hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes, turn, values.now }, transaction, token));
            }
            var pruned = (await connection.QueryAsync<string>(Sql("""
                SELECT p.id FROM pending_operations p LEFT JOIN chat_proposal_details d ON d.proposal_id=p.id
                WHERE p.status NOT IN ('pending','executing') AND p.resolved_at IS NOT NULL AND julianday(p.resolved_at)<julianday(@proposals)
                  AND NOT EXISTS(SELECT 1 FROM mutations m WHERE m.id=d.mutation_id AND m.status IN ('prepared','applied'))
                ORDER BY p.resolved_at,p.id LIMIT @limit
                """, values, transaction, token))).ToArray();
            var releasedProposals = (await connection.QueryAsync<string>(Sql("SELECT payload_id FROM pending_operations WHERE id IN @ids", new { ids = pruned }, transaction, token))).ToArray();
            await connection.ExecuteAsync(Sql("DELETE FROM chat_proposal_details WHERE proposal_id IN @ids; DELETE FROM pending_operations WHERE id IN @ids;", new { ids = pruned }, transaction, token));
            var idempotency = await connection.ExecuteAsync(Sql("""
                DELETE FROM idempotency WHERE rowid IN (SELECT rowid FROM idempotency WHERE julianday(created_at)<julianday(@idempotency) ORDER BY created_at LIMIT @limit)
                """, values, transaction, token));
            var releasedReferences = (await connection.QueryAsync<string>(Sql("""
                DELETE FROM payload_refs WHERE rowid IN (
                  SELECT r.rowid FROM payload_refs r WHERE
                    (r.referrer_kind='pending-operation' AND NOT EXISTS(SELECT 1 FROM pending_operations p WHERE p.id=r.referrer_id))
                    OR (r.referrer_kind LIKE 'chat-%' AND NOT EXISTS(SELECT 1 FROM turns t WHERE t.id=r.referrer_id))
                    OR (r.referrer_kind='tool_execution' AND NOT EXISTS(SELECT 1 FROM tool_executions e WHERE e.id=r.referrer_id))
                    OR (r.referrer_kind='mutation' AND NOT EXISTS(SELECT 1 FROM mutations m WHERE m.id=r.referrer_id))
                    OR (r.referrer_kind IN ('document-projection','extraction-cache') AND NOT EXISTS(SELECT 1 FROM documents d WHERE d.id=r.referrer_id))
                    OR (r.referrer_kind='idempotency' AND json_valid(r.referrer_id) AND NOT EXISTS(
                      SELECT 1 FROM idempotency i WHERE i.key=json_extract(r.referrer_id,'$[0]') AND i.credential_id=json_extract(r.referrer_id,'$[1]')))
                  LIMIT @limit) RETURNING payload_id
                """, values, transaction, token))).ToArray();
            var detached = releasedReferences.Length;
            var released = releasedProposals.Concat(releasedReferences).Distinct(StringComparer.Ordinal).ToArray();
            var orphaned = await connection.ExecuteAsync(Sql("""
                DELETE FROM payloads WHERE id IN (SELECT p.id FROM payloads p
                  WHERE (p.id IN @released OR julianday(p.created_at)<julianday(@payloads) OR julianday(p.expires_at)<=julianday(@now))
                    AND NOT EXISTS(SELECT 1 FROM payload_refs r WHERE r.payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM pending_operations x WHERE x.payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM tool_executions x WHERE x.payload_id=p.id OR x.result_payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM mutations x WHERE x.payload_id=p.id OR x.result_payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM batch_jobs x WHERE x.request_payload_id=p.id OR x.result_payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM document_projections x WHERE x.payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM chat_turn_details x WHERE x.user_payload_id=p.id OR x.answer_payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM chat_events x WHERE x.payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM chat_sources x WHERE x.payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM chat_summaries x WHERE x.payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM chat_turn_attempts x WHERE x.initial_context_payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM chat_ignored_lineage x WHERE x.payload_id=p.id)
                    AND NOT EXISTS(SELECT 1 FROM mutations m JOIN payload_refs r ON r.referrer_kind='mutation' AND r.referrer_id=m.id
                      JOIN payloads plan ON plan.id=r.payload_id JOIN json_tree(CASE WHEN json_valid(CAST(plan.bytes AS TEXT)) THEN CAST(plan.bytes AS TEXT) ELSE '{}' END) j
                      WHERE m.status IN ('prepared','applied') AND j.value=p.id)
                  ORDER BY p.created_at,p.id LIMIT @limit)
                """, new { values.limit, values.payloads, values.now, released }, transaction, token));
            return new MetadataRetentionResult(expired.Length, pruned.Length, idempotency, detached, orphaned, 0,
                new[] { expired.Length, pruned.Length, idempotency, detached, orphaned }.Any(count => count == limit));
        }, cancellationToken);
        var traces = await index.QueueWriteAsync(async (connection, transaction, token) => await connection.ExecuteAsync(Sql("""
            DELETE FROM retrieval_traces WHERE id IN (SELECT id FROM retrieval_traces WHERE julianday(at)<julianday(@traces) ORDER BY at,id LIMIT @limit)
            """, values, transaction, token)), cancellationToken);
        return result with { RetrievalTraces = traces, MoreWork = result.MoreWork || traces == limit };
    }

    private static CommandDefinition Sql(string sql, object values, System.Data.Common.DbTransaction transaction, CancellationToken token) => new(sql, values, transaction, cancellationToken: token);
}
