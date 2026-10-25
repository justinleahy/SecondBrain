using System.Data.Common;
using Dapper;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Ingestion;

namespace SecondBrain.Storage.Durability;

/// <summary>Stages member lifecycle changes in the same transaction as the journaled container change.</summary>
internal static class CalendarLifecycle
{
    internal static async Task ApplyAsync(DbConnection connection, DbTransaction transaction, string containerId, string operation, string now, CancellationToken token)
    {
        if (operation is not ("delete" or "restore" or "purge" or "source-remove")) return;
        if (await connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM calendar_batches WHERE container_id=@id", new { id = containerId }, transaction, token)) == 0) return;
        var members = (await connection.QueryAsync<Member>(Sql("""
            SELECT d.id Id,d.status Status,d.revision Revision FROM documents d WHERE d.container_document_id=@id ORDER BY d.id
            """, new { id = containerId }, transaction, token))).ToArray();
        foreach (var member in members)
        {
            string? status = null;
            if (operation == "purge" && member.Status != "purged") status = "purged";
            else if (operation == "source-remove" && !DocumentStatusNames.IsUnavailable(member.Status)) status = "removed_from_source";
            else if (operation == "delete" && !DocumentStatusNames.IsUnavailable(member.Status))
            {
                await connection.ExecuteAsync(Sql("INSERT OR IGNORE INTO calendar_deleted_members(container_id,document_id,prior_status) VALUES(@containerId,@Id,@Status)", new { containerId, member.Id, member.Status }, transaction, token));
                status = "deleted";
            }
            else if (operation == "restore" && member.Status == "deleted")
                status = await connection.ExecuteScalarAsync<string?>(Sql("SELECT prior_status FROM calendar_deleted_members WHERE container_id=@containerId AND document_id=@Id", new { containerId, member.Id }, transaction, token));
            if (status is null) continue;
            if (operation == "purge")
                await connection.ExecuteAsync(Sql(PurgeCleanup.MarkSql, new { id = member.Id }, transaction, token));
            await connection.ExecuteAsync(Sql("""
                UPDATE documents SET revision=revision+1,status=@status,publish_state='publishing',updated_at=@now,
                  deleted_at=CASE WHEN @status IN ('deleted','purged') THEN @now ELSE NULL END WHERE id=@Id;
                INSERT INTO revisions(document_id,revision,cause,content_hash,created_at)
                  SELECT id,revision,@cause,content_hash,@now FROM documents WHERE id=@Id;
                UPDATE document_projections SET revision=revision+1 WHERE document_id=@Id;
                UPDATE jobs SET status='cancelled',lease_until=NULL,updated_at=@now WHERE document_id=@Id AND status IN ('queued','running');
                """, new { member.Id, status, now, cause = "container-" + operation }, transaction, token));
            if (operation == "purge")
                await connection.ExecuteAsync(Sql("""
                    UPDATE payloads SET bytes=X'',sha256='',redacted_at=@now WHERE EXISTS(SELECT 1 FROM json_each(document_refs_json) WHERE value=@Id);
                    UPDATE documents SET title='[purged]',props_json='{}',props_original_json='{}',content_hash='',source_url=NULL,
                      claimed_client=NULL,error=NULL,size_bytes=0,occurred_at=NULL,occurred_offset=NULL,occurred_basis=NULL,occurred_precision=NULL WHERE id=@Id;
                    UPDATE jobs SET status='cancelled',payload_json='{}',error=NULL WHERE document_id=@Id;
                    DELETE FROM overrides WHERE document_id=@Id;
                    """, new { member.Id, now }, transaction, token));
        }
        if (operation is "restore" or "purge")
            await connection.ExecuteAsync(Sql("DELETE FROM calendar_deleted_members WHERE container_id=@containerId", new { containerId }, transaction, token));
        // Pause a deleted subscription and fence network work. Restore does not silently resume it.
        if (operation is "delete" or "purge")
            await connection.ExecuteAsync(Sql("""
                INSERT OR IGNORE INTO calendar_lifecycle_pauses(source_id) SELECT source_id FROM calendar_subscriptions WHERE container_id=@containerId;
                UPDATE sources SET status=@status WHERE id IN (SELECT source_id FROM calendar_subscriptions WHERE container_id=@containerId) AND status<>'removed';
                UPDATE calendar_subscriptions SET generation=generation+1,lease_id=NULL,lease_until=NULL,requested=0,deep_requested=0,next_attempt_at=NULL WHERE container_id=@containerId;
                """, new { containerId, status = operation == "purge" ? "removed" : "disabled" }, transaction, token));
        if (operation is "delete" or "restore")
            await connection.ExecuteAsync(Sql("""
                UPDATE calendar_subscriptions SET snapshot_path=(SELECT json_extract(CAST(p.bytes AS TEXT),'$.Path')
                  FROM document_projections d JOIN payloads p ON p.id=d.payload_id WHERE d.document_id=@containerId)
                WHERE container_id=@containerId;
                """, new { containerId }, transaction, token));
        if (operation == "purge")
            await connection.ExecuteAsync(Sql("""
                UPDATE calendar_subscriptions SET snapshot_hash=NULL,snapshot_path=NULL,etag=NULL,last_modified=NULL,rejected_hash=NULL,rejected_generation=NULL WHERE container_id=@containerId;
                UPDATE calendar_members SET source_hash='' WHERE container_id=@containerId;
                """, new { containerId }, transaction, token));
        await connection.ExecuteAsync(Sql("""
            UPDATE calendar_batches SET container_revision=(SELECT revision FROM documents WHERE id=@containerId),
              generation_json=(SELECT generation_json FROM publication_fences WHERE document_id=@containerId),phase='publishing' WHERE container_id=@containerId;
            INSERT INTO calendar_lifecycle_batches(container_id,operation) VALUES(@containerId,@operation)
              ON CONFLICT(container_id) DO UPDATE SET operation=excluded.operation;
            DELETE FROM calendar_batch_members WHERE container_id=@containerId;
            INSERT INTO calendar_batch_members(container_id,document_id,revision,generation_json)
              SELECT @containerId,d.id,d.revision,f.generation_json FROM documents d JOIN publication_fences f ON f.document_id=d.id
              WHERE d.id=@containerId OR d.container_document_id=@containerId;
            """, new { containerId, operation }, transaction, token));
    }

    private static CommandDefinition Sql(string sql, object? values, DbTransaction transaction, CancellationToken token) => new(sql, values, transaction, cancellationToken: token);
    private sealed class Member
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "";
        public long Revision { get; set; }
    }
}
