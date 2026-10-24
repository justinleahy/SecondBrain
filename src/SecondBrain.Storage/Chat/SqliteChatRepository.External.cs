using System.Text.Json;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;

namespace SecondBrain.Storage.Chat;

public sealed partial class SqliteChatRepository
{
    /// <summary>External write tools propose without acquiring inference or read authority.</summary>
    public async ValueTask<ChatProposal> ProposeExternalUpdateAsync(JsonElement arguments, AuthenticatedCredential actor, CancellationToken cancellationToken = default)
    {
        async Task Validate(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, CancellationToken token) =>
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction,
                new(actor.Id, actor.Generation, actor.AccountEpoch, "", actor.Kind), Scope.Write, clock.GetUtcNow(), cancellationToken: token);
        await state.QueueWriteAsync(async (connection, transaction, token) => { await Validate(connection, transaction, token); return 0; }, cancellationToken);
        var plan = await PlanAsync("update_document", arguments, cancellationToken);
        return await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await Validate(connection, transaction, token);
            var id = NewId(); var conversation = NewId(); var now = Now();
            var prompt = await PayloadAsync(connection, transaction, "External update requested for " + plan.DocumentId, [], "chat-user", id, token);
            await connection.ExecuteAsync(Sql("""
                INSERT INTO conversations(id,title,owner_kind,owner_credential_id,created_at,updated_at)
                    VALUES(@conversation,'External update proposal',@kind,@actor,@now,@now);
                INSERT INTO chat_conversation_owners(conversation_id,account_epoch) VALUES(@conversation,@epoch);
                INSERT INTO turns(id,conversation_id,ordinal,status,initiating_credential_id,credential_generation,account_epoch,
                    allowed_tools_json,provider,model,started_at,ended_at)
                    VALUES(@id,@conversation,1,'complete',@actor,@generation,@epoch,'["update_document"]','external','none',@now,@now);
                INSERT INTO chat_turn_details(turn_id,credential_id,request_id,request_hash,mode,policy_json,user_payload_id)
                    VALUES(@id,@actor,@id,@id,'chat',@policy,@prompt);
                """, new { id, conversation, kind = actor.Kind, actor = actor.Id, generation = actor.Generation,
                epoch = actor.AccountEpoch, now, policy = JsonSerializer.Serialize(new ChatTurnPolicy("external", "none"), Json), prompt }, transaction, token));
            return await InsertProposalAsync(connection, transaction, await TurnAsync(connection, transaction, id, token), "update_document", plan, actor, token);
        }, cancellationToken);
    }
}
