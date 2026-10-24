using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;

namespace SecondBrain.Storage.Chat;

/// <summary>State transactions serialize admission, event numbering, exposure, and token reservations.</summary>
public sealed partial class SqliteChatRepository(IStateStore state, TimeProvider clock) : IChatRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string TurnSelect = """
        SELECT t.id Id,t.conversation_id ConversationId,t.ordinal Ordinal,t.status Status,
          t.initiating_credential_id Initiator,t.credential_generation Generation,t.account_epoch Epoch,
          t.started_at StartedAt,t.ended_at EndedAt,d.mode Mode,d.policy_json PolicyJson,
          d.failure_code FailureCode,p.bytes PromptBytes,p.redacted_at PromptRedacted,
          a.bytes AnswerBytes,a.redacted_at AnswerRedacted
        FROM turns t JOIN chat_turn_details d ON d.turn_id=t.id
        JOIN payloads p ON p.id=d.user_payload_id LEFT JOIN payloads a ON a.id=d.answer_payload_id
        """;

    public ValueTask<ChatStart> StartAsync(ChatRequest request, ChatTurnPolicy policy, AuthenticatedCredential actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt) || Encoding.UTF8.GetByteCount(request.Prompt) > 32_768)
            throw new ArgumentException("A message must contain 1..32768 UTF-8 bytes.", nameof(request));
        if (request.Mode is not ("chat" or "ask")) throw new ArgumentException("Mode must be chat or ask.", nameof(request));
        if (request.RequestId is { Length: > 128 } || request.RequestId is "") throw new ArgumentException("Invalid request ID.", nameof(request));
        ValidatePolicy(policy);
        var requestId = request.RequestId ?? NewId();
        var hash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { request.Prompt, request.ConversationId, request.Mode }, Json));
        return state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var credential = await AuthorizeAsync(connection, transaction, actor, true, token);
            var previous = await connection.QuerySingleOrDefaultAsync<RequestRow>(Sql("""
                SELECT turn_id TurnId,request_hash Hash FROM chat_turn_details
                WHERE credential_id=@actor AND request_id=@requestId
                """, new { actor = actor.Id, requestId }, transaction, token));
            if (previous is not null)
            {
                if (previous.Hash != hash) throw new ChatConflictException("The request ID already belongs to a different message.");
                await OwnTurnAsync(connection, transaction, previous.TurnId, actor, token);
                return new ChatStart(ToTurn(await TurnAsync(connection, transaction, previous.TurnId, token)), false);
            }
            var now = Now();
            var conversationId = request.ConversationId ?? NewId();
            if (request.ConversationId is not null)
                await OwnConversationAsync(connection, transaction, conversationId, actor, token);
            else
            {
                // User-authored title only. No retrieved or model-derived text is stored outside payloads.
                var title = new string(request.Prompt.Trim().Take(80).ToArray());
                await connection.ExecuteAsync(Sql("""
                    INSERT INTO conversations(id,title,owner_kind,owner_credential_id,created_at,updated_at)
                    VALUES(@conversationId,@title,@kind,@actor,@now,@now);
                    INSERT INTO chat_conversation_owners(conversation_id,account_epoch) VALUES(@conversationId,@epoch);
                    """, new { conversationId, title, kind = actor.Kind, actor = actor.Id, now, epoch = actor.AccountEpoch }, transaction, token));
            }
            if (await connection.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM turns WHERE conversation_id=@conversationId AND status='running'",
                new { conversationId }, transaction, token)) != 0)
                throw new ChatConflictException("This conversation already has an active turn.");
            var id = NewId();
            var ordinal = await connection.ExecuteScalarAsync<int>(Sql("SELECT COALESCE(MAX(ordinal),0)+1 FROM turns WHERE conversation_id=@conversationId", new { conversationId }, transaction, token));
            var payload = await PayloadAsync(connection, transaction, request.Prompt, [], "chat-user", id, token);
            await connection.ExecuteAsync(Sql("""
                INSERT INTO turns(id,conversation_id,ordinal,status,initiating_credential_id,credential_generation,account_epoch,
                  allowed_tools_json,provider,model,started_at)
                VALUES(@id,@conversationId,@ordinal,'running',@actor,@generation,@epoch,@tools,@provider,@model,@now);
                INSERT INTO chat_turn_details(turn_id,credential_id,request_id,request_hash,mode,policy_json,user_payload_id)
                VALUES(@id,@actor,@requestId,@hash,@mode,@policy,@payload);
                UPDATE conversations SET updated_at=@now WHERE id=@conversationId;
                """, new { id, conversationId, ordinal, actor = actor.Id, generation = actor.Generation, epoch = actor.AccountEpoch,
                tools = request.Mode == "ask" || !credential.GrantedScopes.Contains(Scope.Write) ? "[\"read\"]" : "[\"read\",\"propose\"]",
                provider = policy.Provider, model = policy.Model, now, requestId, hash, mode = request.Mode,
                policy = JsonSerializer.Serialize(policy, Json), payload }, transaction, token));
            await EventAsync(connection, transaction, id, "started", new { turnId = id, conversationId, ordinal }, [], token);
            return new ChatStart(ToTurn(await TurnAsync(connection, transaction, id, token)), true);
        }, cancellationToken);
    }

    public ValueTask<IReadOnlyList<ChatConversation>> ListAsync(AuthenticatedCredential actor, CancellationToken cancellationToken = default) => ReadAsync<IReadOnlyList<ChatConversation>>(async (connection, transaction, token) =>
    {
        await AuthorizeAsync(connection, transaction, actor, false, token);
        return (await connection.QueryAsync<ChatConversation>(Sql("""
            SELECT c.id Id,c.title Title,c.updated_at UpdatedAt,
              (SELECT id FROM turns WHERE conversation_id=c.id AND status='running') ActiveTurnId
            FROM conversations c JOIN chat_conversation_owners o ON o.conversation_id=c.id
            WHERE o.account_epoch=@epoch AND ((@kind='session' AND c.owner_kind='session') OR c.owner_credential_id=@actor)
            ORDER BY c.updated_at DESC,c.id DESC LIMIT 100
            """, new { epoch = actor.AccountEpoch, kind = actor.Kind, actor = actor.Id }, transaction, token))).ToArray();
    }, cancellationToken);

    public ValueTask<IReadOnlyList<ChatTurn>> GetConversationAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => ReadAsync<IReadOnlyList<ChatTurn>>(async (connection, transaction, token) =>
    {
        await AuthorizeAsync(connection, transaction, actor, false, token);
        await OwnConversationAsync(connection, transaction, id, actor, token);
        var rows = await connection.QueryAsync<TurnRow>(Sql(TurnSelect + " WHERE t.conversation_id=@id ORDER BY t.ordinal DESC LIMIT 100", new { id }, transaction, token));
        List<ChatTurn> result = [];
        foreach (var row in rows.Reverse()) result.Add(await CitationStatesAsync(connection, transaction, ToTurn(row), token));
        return result;
    }, cancellationToken);

    public ValueTask<ChatTurn> GetTurnAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => ReadAsync(async (connection, transaction, token) =>
    {
        await AuthorizeAsync(connection, transaction, actor, false, token);
        await OwnTurnAsync(connection, transaction, id, actor, token);
        return await CitationStatesAsync(connection, transaction, ToTurn(await TurnAsync(connection, transaction, id, token)), token);
    }, cancellationToken);

    public async ValueTask CheckActiveAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) =>
        _ = await ReadAsync(async (connection, transaction, token) =>
        {
            var turn = await RunningAsync(connection, transaction, turnId, actor, token);
            await RequireLiveLineageAsync(connection, transaction, await ReferencesAsync(connection, transaction, turnId, token), token);
            return turn;
        }, cancellationToken);

    public ValueTask<IReadOnlyList<ChatHistoryMessage>> HistoryAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => ReadAsync<IReadOnlyList<ChatHistoryMessage>>(async (connection, transaction, token) =>
    {
        var current = await RunningAsync(connection, transaction, turnId, actor, token);
        var limit = Policy(current).MaxHistoryMessages / 2;
        var rows = (await connection.QueryAsync<TurnRow>(Sql(TurnSelect + " WHERE t.conversation_id=@id AND t.ordinal<@ordinal ORDER BY t.ordinal DESC LIMIT @limit",
            new { id = current.ConversationId, ordinal = current.Ordinal, limit }, transaction, token))).Reverse();
        List<ChatHistoryMessage> messages = [];
        foreach (var row in rows)
        {
            var turn = ToTurn(row);
            if (row.PromptRedacted is null) messages.Add(new("user", turn.Prompt, []));
            if (turn.Answer is null || row.AnswerRedacted is not null) continue;
            var refs = await ReferencesAsync(connection, transaction, row.Id, token);
            messages.Add(new("assistant", turn.Answer.Text, refs));
        }
        return messages;
    }, cancellationToken);

    public ValueTask<IReadOnlyList<ChatEvent>> EventsAsync(string turnId, long after, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => ReadAsync<IReadOnlyList<ChatEvent>>(async (connection, transaction, token) =>
    {
        if (after < 0) throw new ArgumentOutOfRangeException(nameof(after));
        await AuthorizeAsync(connection, transaction, actor, false, token);
        await OwnTurnAsync(connection, transaction, turnId, actor, token);
        var rows = await connection.QueryAsync<EventRow>(Sql("""
            SELECT e.sequence Sequence,e.kind Kind,e.created_at CreatedAt,p.bytes Bytes,p.redacted_at RedactedAt
            FROM chat_events e LEFT JOIN payloads p ON p.id=e.payload_id
            WHERE e.turn_id=@turnId AND e.sequence>@after ORDER BY e.sequence LIMIT 128
            """, new { turnId, after }, transaction, token));
        return rows.Select(row => new ChatEvent(row.Sequence, row.Kind,
            row.RedactedAt is null && row.Bytes is { Length: > 0 } ? JsonSerializer.Deserialize<JsonElement>(row.Bytes, Json) : null,
            row.RedactedAt is not null, row.CreatedAt)).ToArray();
    }, cancellationToken);

    public ValueTask<long> AppendAsync(string turnId, string kind, JsonElement data, IReadOnlyList<string> documentReferences,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default) => state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        if (kind is not ("delta" or "tool" or "sources" or "proposal" or "warning")) throw new ArgumentException("Invalid event kind.", nameof(kind));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Json).Length;
        if (bytes > 1_000_000 || kind == "delta" && bytes > 65_536) throw new ChatLimitException("The event exceeds its byte limit.");
        var turn = await RunningAsync(connection, transaction, turnId, actor, token);
        if (kind is "tool" or "sources")
        {
            var changed = await connection.ExecuteAsync(Sql("""
                UPDATE chat_turn_details SET tool_result_bytes=tool_result_bytes+@bytes
                WHERE turn_id=@turnId AND tool_result_bytes+@bytes+
                  (SELECT COALESCE(SUM(byte_count),0) FROM chat_sources WHERE turn_id=@turnId)<=@limit
                """, new { bytes, turnId, limit = Policy(turn).MaxReadBytes }, transaction, token));
            if (changed != 1) throw new ChatLimitException("This turn reached its tool-result byte limit.");
        }
        // Always inherit already-exposed lineage; callers cannot accidentally omit it.
        var refs = (await ReferencesAsync(connection, transaction, turnId, token)).Concat(documentReferences).Distinct().ToArray();
        await RequireLiveLineageAsync(connection, transaction, refs, token);
        if (refs.Length != 0) await connection.ExecuteAsync(Sql("UPDATE turns SET retrieved_content=1 WHERE id=@turnId", new { turnId }, transaction, token));
        return await EventAsync(connection, transaction, turnId, kind, data, refs, token);
    }, cancellationToken);

    public ValueTask FinishAsync(string turnId, string status, ChatAnswer answer, string? failureCode = null, CancellationToken cancellationToken = default) => new(state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        if (status is not ("complete" or "partial" or "failed")) throw new ArgumentException("Invalid terminal status.", nameof(status));
        var row = await TurnAsync(connection, transaction, turnId, token);
        if (row.Status != "running") return 0;
        var refs = await ReferencesAsync(connection, transaction, turnId, token);
        // Completion must remain possible after revocation or purge, without reintroducing purged bytes.
        if (!await LiveLineageAsync(connection, transaction, refs, token))
        {
            answer = new ChatAnswer("A source was purged while this answer was being generated.", [], Warnings: ["source_purged"]);
            refs = [];
            status = "partial";
            failureCode = "source_purged";
        }
        else
        {
            var verified = CitationVerifier.Verify(answer.Text, await ReadSourcesAsync(connection, transaction, turnId, token));
            answer = verified with
            {
                InvalidCitationCount = verified.InvalidCitationCount + answer.InvalidCitationCount,
                Warnings = (verified.Warnings ?? []).Concat(answer.Warnings ?? []).Distinct().ToArray()
            };
        }
        var payload = await PayloadAsync(connection, transaction, answer, refs, "chat-answer", turnId, token);
        await connection.ExecuteAsync(Sql("""
            UPDATE chat_turn_details SET answer_payload_id=@payload,failure_code=@failureCode WHERE turn_id=@turnId;
            UPDATE turns SET status=@status,ended_at=@now WHERE id=@turnId AND status='running';
            UPDATE conversations SET updated_at=@now WHERE id=@conversationId;
            """, new { payload, failureCode, turnId, status, now = Now(), conversationId = row.ConversationId }, transaction, token));
        await EventAsync(connection, transaction, turnId, "finished", new { status, failureCode, answer }, refs, token);
        return 0;
    }, cancellationToken).AsTask());

    public ValueTask<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default) => state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        var ids = (await connection.QueryAsync<string>(Sql("SELECT t.id FROM turns t JOIN chat_turn_details d ON d.turn_id=t.id WHERE t.status='running'", null, transaction, token))).ToArray();
        foreach (var id in ids)
        {
            var refs = await ReferencesAsync(connection, transaction, id, token);
            var text = new StringBuilder();
            if (await LiveLineageAsync(connection, transaction, refs, token))
            {
                var deltas = await connection.QueryAsync<byte[]>(Sql("""
                    SELECT p.bytes FROM chat_events e JOIN payloads p ON p.id=e.payload_id
                    WHERE e.turn_id=@id AND e.kind='delta' AND p.redacted_at IS NULL ORDER BY e.sequence
                    """, new { id }, transaction, token));
                foreach (var delta in deltas)
                    if (JsonSerializer.Deserialize<JsonElement>(delta, Json).TryGetProperty("text", out var part)) text.Append(part.GetString());
            }
            else refs = [];
            var answer = CitationVerifier.Verify(text.ToString(), await ReadSourcesAsync(connection, transaction, id, token));
            var payload = await PayloadAsync(connection, transaction, answer, refs, "chat-answer", id, token);
            await connection.ExecuteAsync(Sql("""
                UPDATE turns SET status='partial',ended_at=@now WHERE id=@id;
                UPDATE chat_turn_details SET failure_code='interrupted',answer_payload_id=@payload WHERE turn_id=@id;
                UPDATE chat_model_calls SET status='settled' WHERE turn_id=@id AND status='reserved';
                """, new { id, now = Now(), payload }, transaction, token));
            await EventAsync(connection, transaction, id, "finished", new { status = "partial", failureCode = "interrupted", answer }, refs, token);
        }
        return ids.Length;
    }, cancellationToken);

    private async ValueTask<T> ReadAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> read, CancellationToken token)
    {
        await using var lease = await state.OpenReadConnectionAsync(token);
        await using var transaction = await lease.Connection.BeginTransactionAsync(token);
        return await read(lease.Connection, transaction, token);
    }

    private async Task<CredentialRecord> AuthorizeAsync(DbConnection connection, DbTransaction transaction, AuthenticatedCredential actor, bool infer, CancellationToken token)
    {
        var authority = new IngestionAuthority(actor.Id, actor.Generation, actor.AccountEpoch, "", actor.Kind);
        var row = await IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority, Scope.Read, clock.GetUtcNow(), cancellationToken: token);
        if (infer) await IngestionAuthorityValidator.ValidateAsync(connection, transaction, authority, Scope.Infer, clock.GetUtcNow(), cancellationToken: token);
        return row;
    }

    private static async Task OwnConversationAsync(DbConnection connection, DbTransaction transaction, string id, AuthenticatedCredential actor, CancellationToken token)
    {
        if (await connection.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM conversations c JOIN chat_conversation_owners o ON o.conversation_id=c.id
            WHERE c.id=@id AND o.account_epoch=@epoch
              AND ((@kind='session' AND c.owner_kind='session') OR c.owner_credential_id=@actor)
            """, new { id, epoch = actor.AccountEpoch, kind = actor.Kind, actor = actor.Id }, transaction, token)) != 1) throw new ChatNotFoundException();
    }

    private static async Task OwnTurnAsync(DbConnection connection, DbTransaction transaction, string id, AuthenticatedCredential actor, CancellationToken token)
    {
        var row = await TurnAsync(connection, transaction, id, token);
        await OwnConversationAsync(connection, transaction, row.ConversationId, actor, token);
    }

    private async Task<TurnRow> RunningAsync(DbConnection connection, DbTransaction transaction, string id, AuthenticatedCredential actor, CancellationToken token)
    {
        await AuthorizeAsync(connection, transaction, actor, true, token);
        var row = await TurnAsync(connection, transaction, id, token);
        // Viewing a shared browser conversation never transfers its live initiator's authority.
        if (row.Initiator != actor.Id || row.Generation != actor.Generation || row.Epoch != actor.AccountEpoch) throw new AuthorityChangedException();
        if (row.Status != "running") throw new ChatConflictException("The turn is no longer running.");
        return row;
    }

    private static async Task<TurnRow> TurnAsync(DbConnection connection, DbTransaction transaction, string id, CancellationToken token) =>
        await connection.QuerySingleOrDefaultAsync<TurnRow>(Sql(TurnSelect + " WHERE t.id=@id", new { id }, transaction, token)) ?? throw new ChatNotFoundException();
    private static ChatTurnPolicy Policy(TurnRow row) => JsonSerializer.Deserialize<ChatTurnPolicy>(row.PolicyJson, Json) ?? throw new InvalidDataException("Missing turn policy.");
    private static ChatTurn ToTurn(TurnRow row) => new(row.Id, row.ConversationId, row.Ordinal, row.Status, row.Mode,
        row.PromptRedacted is null ? JsonSerializer.Deserialize<string>(row.PromptBytes, Json) ?? "" : "[purged]", Policy(row), row.StartedAt, row.EndedAt, row.FailureCode,
        row.AnswerRedacted is null && row.AnswerBytes is { Length: > 0 } ? JsonSerializer.Deserialize<ChatAnswer>(row.AnswerBytes, Json) : null, row.AnswerRedacted is not null);

    private async Task<string> PayloadAsync<T>(DbConnection connection, DbTransaction transaction, T value, IReadOnlyList<string> refs, string kind, string referrer, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var id = NewId();
        await connection.ExecuteAsync(Sql("""
            INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@id,@hash,@bytes,@refs,@now);
            INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES(@id,@kind,@referrer);
            """, new { id, hash = Hash(bytes), bytes, refs = JsonSerializer.Serialize(refs.Distinct().Order().ToArray(), Json), now = Now(), kind, referrer }, transaction, token));
        return id;
    }

    private async Task<long> EventAsync<T>(DbConnection connection, DbTransaction transaction, string turnId, string kind, T value, IReadOnlyList<string> refs, CancellationToken token)
    {
        var seq = await connection.ExecuteScalarAsync<long>(Sql("SELECT COALESCE(MAX(sequence),0)+1 FROM chat_events WHERE turn_id=@turnId", new { turnId }, transaction, token));
        var payload = await PayloadAsync(connection, transaction, value, refs, "chat-event", turnId, token);
        await connection.ExecuteAsync(Sql("INSERT INTO chat_events(turn_id,sequence,kind,payload_id,created_at) VALUES(@turnId,@seq,@kind,@payload,@now)", new { turnId, seq, kind, payload, now = Now() }, transaction, token));
        return seq;
    }

    private static async Task<string[]> ReferencesAsync(DbConnection connection, DbTransaction transaction, string turnId, CancellationToken token) =>
        (await connection.QueryAsync<string>(Sql("""
            SELECT DISTINCT j.value FROM payload_refs r JOIN payloads p ON p.id=r.payload_id,json_each(p.document_refs_json) j
            WHERE r.referrer_id=@turnId AND r.referrer_kind LIKE 'chat-%'
            """, new { turnId }, transaction, token))).ToArray();
    private static async Task<bool> LiveLineageAsync(DbConnection connection, DbTransaction transaction, IReadOnlyList<string> refs, CancellationToken token) =>
        refs.Count == 0 || await connection.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM documents WHERE id IN @refs AND status!='purged'", new { refs }, transaction, token)) == refs.Distinct().Count();
    private static async Task RequireLiveLineageAsync(DbConnection connection, DbTransaction transaction, IReadOnlyList<string> refs, CancellationToken token)
    {
        if (!await LiveLineageAsync(connection, transaction, refs, token)) throw new ChatConflictException("A source was purged.");
    }

    private static async Task<ChatTurn> CitationStatesAsync(DbConnection connection, DbTransaction transaction, ChatTurn turn, CancellationToken token)
    {
        if (turn.Answer is null || turn.Answer.Citations.Count == 0) return turn;
        List<ChatCitation> citations = [];
        foreach (var citation in turn.Answer.Citations)
        {
            var source = citation.Source;
            var current = await connection.QuerySingleOrDefaultAsync<SourceStateRow>(Sql("""
                SELECT d.status Status,d.revision Revision,p.generation_json Generation FROM documents d
                LEFT JOIN document_projections p ON p.document_id=d.id WHERE d.id=@id
                """, new { id = source.DocumentId }, transaction, token));
            var state = current is null || current.Status == "purged" ? "purged" : current.Status is "deleted" or "missing" ? "deleted" :
                current.Revision != source.Revision || current.Generation != source.Generation ? "changed" : "current";
            // A purge transaction redacts this entire answer. A read transaction sees either side atomically.
            citations.Add(citation with { State = state });
        }
        return turn with { Answer = turn.Answer with { Citations = citations } };
    }
    private string Now() => AuthTime.Format(clock.GetUtcNow());
    private static string NewId() => Ulid.NewUlid().ToString();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static CommandDefinition Sql(string sql, object? args, DbTransaction transaction, CancellationToken token) => new(sql, args, transaction, cancellationToken: token);
    private static void ValidatePolicy(ChatTurnPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Provider) || string.IsNullOrWhiteSpace(policy.Model) || policy.MaxToolCalls is < 1 or > 32 ||
            policy.MaxReadDocuments is < 1 or > 100 || policy.MaxReadBytes is < 1 or > 1_000_000 || policy.MaxMetadataRecords is < 1 or > 1000 ||
            policy.MaxOutputTokens is < 1 or > 32768 || policy.MaxHistoryMessages is < 0 or > 100 || policy.HourlyTokens < 1 || policy.DailyTokens < 1)
            throw new ArgumentException("Invalid chat policy.", nameof(policy));
    }

    private sealed class RequestRow { public string TurnId { get; set; } = ""; public string Hash { get; set; } = ""; }
    private sealed class SourceStateRow { public string Status { get; set; } = ""; public long Revision { get; set; } public string? Generation { get; set; } }
    private sealed class TurnRow
    {
        public string Id { get; set; } = ""; public string ConversationId { get; set; } = ""; public int Ordinal { get; set; }
        public string Status { get; set; } = ""; public string Initiator { get; set; } = ""; public long Generation { get; set; }
        public long Epoch { get; set; } public string StartedAt { get; set; } = ""; public string? EndedAt { get; set; }
        public string Mode { get; set; } = ""; public string PolicyJson { get; set; } = ""; public string? FailureCode { get; set; }
        public byte[] PromptBytes { get; set; } = []; public string? PromptRedacted { get; set; }
        public byte[]? AnswerBytes { get; set; } public string? AnswerRedacted { get; set; }
    }
    private sealed class EventRow
    {
        public long Sequence { get; set; } public string Kind { get; set; } = ""; public string CreatedAt { get; set; } = "";
        public byte[]? Bytes { get; set; } public string? RedactedAt { get; set; }
    }
}
