using System.Data.Common;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;

namespace SecondBrain.Storage.Chat;

public sealed partial class SqliteChatRepository
{
    public ValueTask<ChatStart> RetryAsync(string turnId, ChatRetryRequest request, ChatTurnPolicy policy, AuthenticatedCredential actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128) throw new ArgumentException("Retry request ID is required.");
        ValidatePolicy(policy);
        return state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await AuthorizeAsync(connection, transaction, actor, true, token);
            var row = await TurnAsync(connection, transaction, turnId, token);
            await OwnTurnAsync(connection, transaction, turnId, actor, token);
            if (row.Initiator != actor.Id || row.Generation != actor.Generation || row.Epoch != actor.AccountEpoch) throw new AuthorityChangedException();
            var previous = await connection.QuerySingleOrDefaultAsync<RetryRow>(Sql("SELECT use_fallback Fallback,after_sequence AfterSequence FROM chat_retries WHERE turn_id=@turnId AND request_id=@requestId",
                new { turnId, requestId = request.RequestId }, transaction, token));
            if (previous is not null)
            {
                if (previous.Fallback != request.UseFallback) throw new ChatConflictException("The retry request ID was reused with different options.");
                return new ChatStart(ToTurn(row), false, previous.AfterSequence);
            }
            if (row.Status is not ("partial" or "failed") || row.PromptRedacted is not null || Policy(row).Provider == "external")
                throw new ChatConflictException("Only an interrupted assistant turn can be retried.");
            if (await connection.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM turns WHERE conversation_id=@conversation AND (ordinal>@ordinal OR status='running')",
                new { conversation = row.ConversationId, ordinal = row.Ordinal }, transaction, token)) != 0)
                throw new ChatConflictException("Retry the latest turn before starting another message.");
            if (await connection.ExecuteScalarAsync<int>(Sql("""
                SELECT COUNT(*) FROM pending_operations p LEFT JOIN chat_proposal_details d ON d.proposal_id=p.id
                LEFT JOIN mutations m ON m.id=d.mutation_id
                WHERE p.turn_id=@turnId AND (p.status='executing' OR m.status IN ('prepared','applied'))
                """, new { turnId }, transaction, token)) != 0)
                throw new ChatConflictException("The approved write must finish recovery before this turn can resume.");
            var old = Policy(row);
            var written = await HasCommittedWriteAsync(connection, transaction, turnId, token);
            if (written && (request.UseFallback || old.Provider != policy.Provider || old.Model != policy.Model || old.ProviderEndpoint != policy.ProviderEndpoint || old.Reasoning != policy.Reasoning))
                throw new ChatConflictException("After a committed write, resume with the same provider and model.");
            // A retry can narrow budgets, but cannot reset spent counters or widen the frozen tool/filter policy.
            policy = old with
            {
                Provider = policy.Provider, Model = policy.Model, ProviderEndpoint = policy.ProviderEndpoint, Reasoning = policy.Reasoning,
                MaxOutputTokens = Math.Min(old.MaxOutputTokens, policy.MaxOutputTokens),
                MaxContextTokens = Math.Min(old.MaxContextTokens, policy.MaxContextTokens),
                MaxHistoryBytes = Math.Min(old.MaxHistoryBytes, policy.MaxHistoryBytes),
                AutoApproveNotes = old.AutoApproveNotes && policy.AutoApproveNotes
            };
            await connection.ExecuteAsync(Sql("""
                INSERT OR IGNORE INTO chat_turn_attempts(turn_id) VALUES(@turnId);
                UPDATE chat_turn_attempts SET attempt=attempt+1 WHERE turn_id=@turnId;
                INSERT OR IGNORE INTO chat_ignored_lineage(turn_id,payload_id)
                  SELECT @turnId,p.id FROM payload_refs r JOIN payloads p ON p.id=r.payload_id
                  WHERE r.referrer_id=@turnId AND r.referrer_kind LIKE 'chat-%' AND p.redacted_at IS NOT NULL;
                UPDATE turns SET status='running',ended_at=NULL,provider=@provider,model=@model WHERE id=@turnId;
                UPDATE chat_turn_details SET answer_payload_id=NULL,failure_code=NULL,policy_json=@policy WHERE turn_id=@turnId;
                """, new { turnId, provider = policy.Provider, model = policy.Model, policy = JsonSerializer.Serialize(policy, Json) }, transaction, token));
            var attempt = await connection.ExecuteScalarAsync<int>(Sql("SELECT attempt FROM chat_turn_attempts WHERE turn_id=@turnId", new { turnId }, transaction, token));
            var sequence = await EventAsync(connection, transaction, turnId, "resumed", new { attempt }, [], token);
            await connection.ExecuteAsync(Sql("INSERT INTO chat_retries(turn_id,request_id,use_fallback,attempt,after_sequence) VALUES(@turnId,@requestId,@fallback,@attempt,@sequence)",
                new { turnId, requestId = request.RequestId, fallback = request.UseFallback, attempt, sequence }, transaction, token));
            return new ChatStart(ToTurn(await TurnAsync(connection, transaction, turnId, token)), true, sequence);
        }, cancellationToken);
    }

    public ValueTask SaveInitialContextAsync(string turnId, JsonElement context, AuthenticatedCredential actor, CancellationToken cancellationToken = default) =>
        new(state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await RunningAsync(connection, transaction, turnId, actor, token);
            if (await connection.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM chat_turn_attempts WHERE turn_id=@turnId AND initial_context_payload_id IS NOT NULL", new { turnId }, transaction, token)) != 0)
                return 0;
            var refs = await ReferencesAsync(connection, transaction, turnId, token);
            await RequireLiveLineageAsync(connection, transaction, refs, token);
            var payload = await PayloadAsync(connection, transaction, context, refs, "chat-initial-context", turnId, token);
            return await connection.ExecuteAsync(Sql("UPDATE chat_turn_attempts SET initial_context_payload_id=@payload WHERE turn_id=@turnId AND initial_context_payload_id IS NULL",
                new { turnId, payload }, transaction, token));
        }, cancellationToken).AsTask());

    public ValueTask<ChatResumeState> ResumeStateAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) =>
        ReadAsync(async (connection, transaction, token) =>
        {
            await AuthorizeAsync(connection, transaction, actor, false, token);
            await OwnTurnAsync(connection, transaction, turnId, actor, token);
            return await ReadResumeStateAsync(connection, transaction, turnId, token);
        }, cancellationToken);

    public ValueTask<IReadOnlyList<ChatTranscriptTurn>> TranscriptAsync(string conversationId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<ChatTranscriptTurn>>(async (connection, transaction, token) =>
        {
            await AuthorizeAsync(connection, transaction, actor, false, token);
            await OwnConversationAsync(connection, transaction, conversationId, actor, token);
            var rows = await connection.QueryAsync<TurnRow>(Sql(TurnSelect + " WHERE t.conversation_id=@conversationId ORDER BY t.ordinal", new { conversationId }, transaction, token));
            List<ChatTranscriptTurn> result = [];
            foreach (var row in rows) result.Add(new(await CitationStatesAsync(connection, transaction, ToTurn(row), token),
                (await ReadResumeStateAsync(connection, transaction, row.Id, token)).Calls));
            return result;
        }, cancellationToken);

    private static async Task<ChatResumeState> ReadResumeStateAsync(DbConnection connection, DbTransaction transaction, string turnId, CancellationToken token)
    {
        var attempt = await connection.QuerySingleOrDefaultAsync<AttemptRow>(Sql("""
            SELECT a.attempt Attempt,p.bytes Context,p.redacted_at ContextRedacted,t.allowed_tools_json Tools
            FROM turns t LEFT JOIN chat_turn_attempts a ON a.turn_id=t.id
            LEFT JOIN payloads p ON p.id=a.initial_context_payload_id WHERE t.id=@turnId
            """, new { turnId }, transaction, token)) ?? throw new ChatNotFoundException();
        var rows = await connection.QueryAsync<ReplayRow>(Sql("""
            SELECT e.id Id,e.tool Name,e.args_hash ArgumentsHash,e.status Status,
              a.bytes Arguments,a.redacted_at ArgumentsRedacted,r.bytes Result,r.redacted_at ResultRedacted,
              r.document_refs_json Refs, p.id ProposalId,p.status ProposalStatus,plan.bytes Plan,plan.redacted_at PlanRedacted,
              plan.document_refs_json PlanRefs
            FROM tool_executions e LEFT JOIN payloads a ON a.id=e.payload_id LEFT JOIN payloads r ON r.id=e.result_payload_id
            LEFT JOIN chat_proposal_calls c ON c.invocation_id=e.id LEFT JOIN pending_operations p ON p.id=c.proposal_id
            LEFT JOIN payloads plan ON plan.id=p.payload_id
            WHERE e.turn_id=@turnId AND NOT EXISTS(SELECT 1 FROM pending_operations inner_p WHERE inner_p.execution_id=e.id)
            ORDER BY e.rowid LIMIT 100
            """, new { turnId }, transaction, token));
        List<ChatRecordedCall> calls = [];
        foreach (var row in rows)
        {
            var redacted = row.ArgumentsRedacted is not null || row.ResultRedacted is not null || row.PlanRedacted is not null;
            var arguments = !redacted && row.Arguments is { Length: > 0 } ? JsonSerializer.Deserialize<JsonElement>(row.Arguments, Json) : JsonSerializer.SerializeToElement(new { redacted = true });
            JsonElement? result = row.Result is { Length: > 0 } && !redacted ? JsonSerializer.Deserialize<JsonElement>(row.Result, Json) : null;
            var refs = row.Refs is null ? [] : JsonSerializer.Deserialize<string[]>(row.Refs, Json) ?? [];
            if (row.ProposalId is not null && !redacted)
            {
                var plan = JsonSerializer.Deserialize<ChatWritePlan>(row.Plan!, Json);
                result = JsonSerializer.SerializeToElement(new { Id = row.ProposalId, Status = row.ProposalStatus, documentId = plan?.DocumentId, FailureCode = (string?)null });
                refs = JsonSerializer.Deserialize<string[]>(row.PlanRefs ?? "[]", Json) ?? [];
            }
            if (redacted) { result = JsonSerializer.SerializeToElement(new { redacted = true, message = "The recorded tool result was removed after a source purge." }); refs = []; }
            calls.Add(new(row.Id, row.Name, row.ArgumentsHash, arguments, result, row.ProposalStatus ?? row.Status, refs, redacted));
        }
        JsonElement? context = attempt.Context is { Length: > 0 } && attempt.ContextRedacted is null ? JsonSerializer.Deserialize<JsonElement>(attempt.Context, Json) :
            attempt.ContextRedacted is not null ? JsonSerializer.SerializeToElement(new { redacted = true }) : null;
        return new(attempt.Attempt, context, calls, JsonSerializer.Deserialize<string[]>(attempt.Tools, Json) ?? [], await HasCommittedWriteAsync(connection, transaction, turnId, token));
    }

    private static Task<bool> HasCommittedWriteAsync(DbConnection connection, DbTransaction transaction, string turnId, CancellationToken token) =>
        connection.ExecuteScalarAsync<bool>(Sql("SELECT EXISTS(SELECT 1 FROM pending_operations WHERE turn_id=@turnId AND status='executed')", new { turnId }, transaction, token));

    private sealed class RetryRow { public bool Fallback { get; set; } public long AfterSequence { get; set; } }
    private sealed class AttemptRow { public int Attempt { get; set; } public byte[]? Context { get; set; } public string? ContextRedacted { get; set; } public string Tools { get; set; } = "[]"; }
    private sealed class ReplayRow
    {
        public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string ArgumentsHash { get; set; } = ""; public string Status { get; set; } = "";
        public byte[]? Arguments { get; set; } public string? ArgumentsRedacted { get; set; } public byte[]? Result { get; set; } public string? ResultRedacted { get; set; } public string? Refs { get; set; }
        public string? ProposalId { get; set; } public string? ProposalStatus { get; set; } public byte[]? Plan { get; set; } public string? PlanRedacted { get; set; } public string? PlanRefs { get; set; }
    }
}
