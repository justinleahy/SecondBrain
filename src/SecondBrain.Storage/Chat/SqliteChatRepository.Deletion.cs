using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;

namespace SecondBrain.Storage.Chat;

public sealed partial class SqliteChatRepository
{
    public ValueTask DeleteConversationAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default) =>
        new(state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction,
                new IngestionAuthority(actor.Id, actor.Generation, actor.AccountEpoch, "", actor.Kind),
                Scope.Write, clock.GetUtcNow(), cancellationToken: token);
            await OwnConversationAsync(connection, transaction, id, actor, token);
            if (await connection.ExecuteScalarAsync<int>(Sql("""
                SELECT COUNT(*) FROM turns WHERE conversation_id=@id AND status='running'
                """, new { id }, transaction, token)) != 0)
                throw new ChatConflictException("Wait for the current response to finish before deleting this conversation.");
            if (await connection.ExecuteScalarAsync<int>(Sql("""
                SELECT COUNT(*) FROM pending_operations p JOIN turns t ON t.id=p.turn_id
                LEFT JOIN chat_proposal_details d ON d.proposal_id=p.id LEFT JOIN mutations m ON m.id=d.mutation_id
                WHERE t.conversation_id=@id AND (p.status='executing' OR m.status IN ('prepared','applied'))
                """, new { id }, transaction, token)) != 0)
                throw new ChatConflictException("Wait for the approved change to finish before deleting this conversation.");

            var payloads = (await connection.QueryAsync<string>(Sql("""
                SELECT DISTINCT r.payload_id FROM payload_refs r JOIN turns t ON t.id=r.referrer_id
                WHERE t.conversation_id=@id AND r.referrer_kind LIKE 'chat-%'
                """, new { id }, transaction, token))).ToArray();
            await connection.ExecuteAsync(Sql("""
                INSERT INTO chat_usage_history(credential_id,charged_tokens,created_at)
                  SELECT c.credential_id,c.charged_tokens,c.created_at FROM chat_model_calls c
                  JOIN turns t ON t.id=c.turn_id WHERE t.conversation_id=@id
                    AND NOT EXISTS(SELECT 1 FROM usage_calls u WHERE u.call_id='chat/'||c.turn_id||'/'||c.call_id);
                INSERT INTO chat_deleted_requests(credential_id,request_id,deleted_at)
                  SELECT d.credential_id,d.request_id,@now FROM chat_turn_details d
                  JOIN turns t ON t.id=d.turn_id WHERE t.conversation_id=@id;
                DELETE FROM chat_proposal_details WHERE proposal_id IN
                  (SELECT p.id FROM pending_operations p JOIN turns t ON t.id=p.turn_id WHERE t.conversation_id=@id);
                DELETE FROM pending_operations WHERE turn_id IN (SELECT id FROM turns WHERE conversation_id=@id);
                DELETE FROM payload_refs WHERE referrer_kind LIKE 'chat-%' AND referrer_id IN
                  (SELECT id FROM turns WHERE conversation_id=@id);
                DELETE FROM conversations WHERE id=@id;
                """, new { id, now = Now() }, transaction, token));
            // Keep journal/document-owned payloads; saved notes and their recovery
            // records are independent from the conversation being removed.
            foreach (var batch in payloads.Chunk(500))
                await connection.ExecuteAsync(Sql("""
                    DELETE FROM payloads WHERE id IN @batch
                      AND NOT EXISTS(SELECT 1 FROM payload_refs r WHERE r.payload_id=payloads.id)
                    """, new { batch }, transaction, token));
            return 0;
        }, cancellationToken).AsTask());
}
