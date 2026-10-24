using System.Text.Json;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;

namespace SecondBrain.Storage.Chat;

public sealed partial class SqliteChatRepository
{
    public ValueTask<IReadOnlyList<ChatToolActivity>> ActivityAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<ChatToolActivity>>(async (connection, transaction, token) =>
        {
            await AuthorizeAsync(connection, transaction, actor, false, token);
            await OwnTurnAsync(connection, transaction, turnId, actor, token);
            return (await connection.QueryAsync<ChatToolActivity>(Sql("SELECT id Id,tool Name,status Status FROM tool_executions WHERE turn_id=@turnId ORDER BY rowid LIMIT 100", new { turnId }, transaction, token))).ToArray();
        }, cancellationToken);

    public ValueTask<ChatEgressManifest> ManifestAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) =>
        ReadAsync(async (connection, transaction, token) =>
    {
        await AuthorizeAsync(connection, transaction, actor, false, token);
        await OwnTurnAsync(connection, transaction, turnId, actor, token);
        var turn = await TurnAsync(connection, transaction, turnId, token);
        var sources = (await connection.QueryAsync<ManifestSource>(Sql("SELECT document_id Id,revision Revision FROM chat_sources WHERE turn_id=@turnId GROUP BY document_id,revision", new { turnId }, transaction, token))).ToArray();
        var refs = await ReferencesAsync(connection, transaction, turnId, token);
        var documents = sources.Select(source => new ChatEgressDocument(source.Id, source.Revision, "retrieval"))
            .Concat(refs.Where(id => sources.All(source => source.Id != id)).Select(id => new ChatEgressDocument(id, null, "history or tool result"))).ToArray();
        var counts = await connection.QuerySingleAsync<ManifestCounts>(Sql("""
            SELECT d.tool_result_bytes Bytes,(SELECT COALESCE(SUM(charged_tokens),0) FROM chat_model_calls WHERE turn_id=@turnId) Tokens,
              EXISTS(SELECT 1 FROM chat_events e WHERE e.turn_id=@turnId AND e.kind='history') History,
              EXISTS(SELECT 1 FROM chat_events e WHERE e.turn_id=@turnId AND e.kind='compaction') Compacted
            FROM chat_turn_details d WHERE d.turn_id=@turnId
            """, new { turnId, conversation = turn.ConversationId, ordinal = turn.Ordinal }, transaction, token));
        var policy = Policy(turn);
        var exposures = await connection.QueryAsync<ProviderExposureRow>(Sql("""
            SELECT call_id CallId,provider Provider,model Model,endpoint Endpoint,documents_json Documents
            FROM chat_provider_exposures WHERE turn_id=@turnId ORDER BY rowid
            """, new { turnId }, transaction, token));
        return new ChatEgressManifest(policy.Provider, policy.Model, documents, counts.History != 0, counts.Compacted != 0, counts.Bytes, counts.Tokens,
            exposures.Select(row => new ChatProviderExposure(row.CallId, row.Provider, row.Model, row.Endpoint, JsonSerializer.Deserialize<string[]>(row.Documents, Json) ?? [])).ToArray());
    }, cancellationToken);
    public ValueTask<string> BeginToolAsync(string turnId, string name, JsonElement arguments, int metadataRecords,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default) => state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        var turn = await RunningAsync(connection, transaction, turnId, actor, token);
        var allowed = await connection.ExecuteScalarAsync<string>(Sql("SELECT allowed_tools_json FROM turns WHERE id=@turnId", new { turnId }, transaction, token));
        if (!(JsonSerializer.Deserialize<string[]>(allowed!, Json) ?? []).Contains(name, StringComparer.Ordinal)) throw new ChatToolNotAllowedException();
        if (metadataRecords < 0 || JsonSerializer.SerializeToUtf8Bytes(arguments, Json).Length > 32_768) throw new ArgumentException("Tool arguments exceed their limits.");
        var policy = Policy(turn);
        if (await connection.ExecuteAsync(Sql("""
            UPDATE chat_turn_details SET tool_calls=tool_calls+1,metadata_records=metadata_records+@metadataRecords
            WHERE turn_id=@turnId AND tool_calls<@calls AND metadata_records+@metadataRecords<=@records
            """, new { turnId, metadataRecords, calls = policy.MaxToolCalls, records = policy.MaxMetadataRecords }, transaction, token)) != 1)
            throw new ChatLimitException("This turn reached its tool or metadata limit.");
        var id = NewId();
        var refs = await ReferencesAsync(connection, transaction, turnId, token);
        await RequireLiveLineageAsync(connection, transaction, refs, token);
        var payload = await PayloadAsync(connection, transaction, arguments, refs, "chat-tool-arguments", turnId, token);
        await connection.ExecuteAsync(Sql("""
            INSERT INTO tool_executions(id,turn_id,tool,payload_id,args_hash,status,initiating_credential_id,credential_generation,account_epoch)
            VALUES(@id,@turnId,@name,@payload,@hash,'running',@actor,@generation,@epoch)
            """, new { id, turnId, name, payload, hash = Hash(JsonSerializer.SerializeToUtf8Bytes(arguments, Json)),
            actor = actor.Id, generation = actor.Generation, epoch = actor.AccountEpoch }, transaction, token));
        return id;
    }, cancellationToken);

    public ValueTask CompleteToolAsync(string executionId, JsonElement result, IReadOnlyList<string> references,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default) => new(state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        var execution = await connection.QuerySingleOrDefaultAsync<ExecutionRow>(Sql("SELECT turn_id TurnId,tool Name,status Status FROM tool_executions WHERE id=@executionId", new { executionId }, transaction, token))
            ?? throw new ChatNotFoundException();
        var turn = await RunningAsync(connection, transaction, execution.TurnId, actor, token);
        if (execution.Status != "running") throw new ChatConflictException("This tool call has already finished.");
        var turnId = execution.TurnId;
        var refs = (await ReferencesAsync(connection, transaction, turnId, token)).Concat(references).Distinct().ToArray();
        await RequireLiveLineageAsync(connection, transaction, refs, token);
        var envelope = new { name = execution.Name, executionId, result };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json).Length;
        if (await connection.ExecuteAsync(Sql("""
            UPDATE chat_turn_details SET tool_result_bytes=tool_result_bytes+@bytes
            WHERE turn_id=@turnId AND tool_result_bytes+@bytes+
              (SELECT COALESCE(SUM(byte_count),0) FROM chat_sources WHERE turn_id=@turnId)<=@limit
            """, new { turnId, bytes, limit = Policy(turn).MaxReadBytes }, transaction, token)) != 1)
            throw new ChatLimitException("This turn reached its tool-result byte limit.");
        var payload = await PayloadAsync(connection, transaction, result, refs, "chat-tool-result", turnId, token);
        await connection.ExecuteAsync(Sql("UPDATE payloads SET document_refs_json=@refs WHERE id=(SELECT payload_id FROM tool_executions WHERE id=@executionId)",
            new { refs = JsonSerializer.Serialize(refs, Json), executionId }, transaction, token));
        await connection.ExecuteAsync(Sql("UPDATE tool_executions SET status='committed',result_payload_id=@payload,committed_at=@now WHERE id=@executionId", new { executionId, payload, now = Now() }, transaction, token));
        if (refs.Length != 0) await connection.ExecuteAsync(Sql("UPDATE turns SET retrieved_content=1 WHERE id=@turnId", new { turnId }, transaction, token));
        await EventAsync(connection, transaction, turnId, "tool", envelope, refs, token);
        return 0;
    }, cancellationToken).AsTask());

    public ValueTask FailToolAsync(string executionId, string reason, CancellationToken cancellationToken = default) => new(state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        // Only a fixed diagnostic code is stored; exception text can contain retrieved content or provider details.
        if (reason is not ("denied" or "cap" or "failed" or "cancelled")) throw new ArgumentException("Invalid tool failure code.");
        return await connection.ExecuteAsync(Sql("UPDATE tool_executions SET status='failed',result_summary=@reason,committed_at=@now WHERE id=@executionId AND status='running'", new { executionId, reason, now = Now() }, transaction, token));
    }, cancellationToken).AsTask());

    private sealed class ExecutionRow
    {
        public string TurnId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
    }
    private sealed class ManifestSource { public string Id { get; set; } = ""; public long Revision { get; set; } }
    private sealed class ProviderExposureRow
    {
        public string CallId { get; set; } = ""; public string Provider { get; set; } = ""; public string Model { get; set; } = "";
        public string? Endpoint { get; set; } public string Documents { get; set; } = "[]";
    }
    private sealed class ManifestCounts { public long Bytes { get; set; } public long Tokens { get; set; } public long History { get; set; } public long Compacted { get; set; } }
}
