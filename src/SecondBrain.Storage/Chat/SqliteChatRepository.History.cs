using System.Text;
using System.Text.Json;
using System.Data.Common;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;

namespace SecondBrain.Storage.Chat;

public sealed partial class SqliteChatRepository
{
    private ValueTask<IReadOnlyList<ChatHistoryMessage>> CompactHistoryAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken) =>
        state.QueueWriteAsync<IReadOnlyList<ChatHistoryMessage>>(async (connection, transaction, token) =>
    {
        var current = await RunningAsync(connection, transaction, turnId, actor, token);
        var policy = Policy(current);
        if (policy.MaxHistoryMessages == 0) return Array.Empty<ChatHistoryMessage>();
        var summary = await connection.QuerySingleOrDefaultAsync<SummaryRow>(Sql("""
            SELECT s.through_ordinal ThroughOrdinal,p.bytes Bytes,p.document_refs_json Refs,p.redacted_at RedactedAt
            FROM chat_summaries s JOIN payloads p ON p.id=s.payload_id WHERE s.conversation_id=@id
            """, new { id = current.ConversationId }, transaction, token));
        var cutoff = Math.Max(summary?.ThroughOrdinal ?? 0, current.Ordinal - 1 - policy.MaxHistoryMessages / 2);
        var recent = (await connection.QueryAsync<TurnRow>(Sql(TurnSelect + " WHERE t.conversation_id=@id AND t.ordinal>@cutoff AND t.ordinal<@ordinal ORDER BY t.ordinal DESC LIMIT 100",
            new { id = current.ConversationId, cutoff, ordinal = current.Ordinal }, transaction, token))).ToArray();
        var recentBytes = 0;
        var cached = new Dictionary<string, IReadOnlyList<ChatHistoryMessage>>(StringComparer.Ordinal);
        foreach (var row in recent)
        {
            var messages = await HistoryMessagesAsync(connection, transaction, row, token);
            cached[row.Id] = messages;
            var bytes = messages.Sum(message => Encoding.UTF8.GetByteCount(message.Text));
            if (recentBytes + bytes > policy.MaxHistoryBytes / 2) { cutoff = row.Ordinal; break; }
            recentBytes += bytes;
        }
        if (cutoff > (summary?.ThroughOrdinal ?? 0))
        {
            List<ChatHistoryMessage> compact = [];
            HashSet<string> lineage = new(StringComparer.Ordinal);
            if (summary is not null)
            {
                compact.Add(new("summary", SummaryText(summary), []));
                if (summary.RedactedAt is null)
                    lineage.UnionWith(JsonSerializer.Deserialize<string[]>(summary.Refs, Json) ?? []);
            }
            var older = (await connection.QueryAsync<TurnRow>(Sql(TurnSelect + " WHERE t.conversation_id=@id AND t.ordinal>@previous AND t.ordinal<=@cutoff ORDER BY t.ordinal DESC LIMIT 200",
                new { id = current.ConversationId, previous = summary?.ThroughOrdinal ?? 0, cutoff }, transaction, token))).Reverse();
            foreach (var row in older)
            {
                foreach (var message in await HistoryMessagesAsync(connection, transaction, row, token))
                {
                    lineage.UnionWith(message.DocumentReferences);
                    compact.Add(message);
                }
            }
            var text = ConversationCompactor.Compact(compact, policy.MaxHistoryBytes / 2);
            var payload = await PayloadAsync(connection, transaction, text, lineage.ToArray(), "chat-summary", turnId, token);
            await connection.ExecuteAsync(Sql("""
                INSERT INTO chat_summaries(conversation_id,through_ordinal,payload_id,created_by_turn,updated_at)
                VALUES(@conversation,@cutoff,@payload,@turnId,@now)
                ON CONFLICT(conversation_id) DO UPDATE SET through_ordinal=excluded.through_ordinal,payload_id=excluded.payload_id,
                  created_by_turn=excluded.created_by_turn,updated_at=excluded.updated_at
                """, new { conversation = current.ConversationId, cutoff, payload, turnId, now = Now() }, transaction, token));
            summary = new() { ThroughOrdinal = cutoff, Bytes = JsonSerializer.SerializeToUtf8Bytes(text, Json), Refs = JsonSerializer.Serialize(lineage.ToArray(), Json) };
        }
        List<ChatHistoryMessage> result = [];
        if (summary is not null)
            result.Add(new("system", SummaryText(summary), summary.RedactedAt is null ? JsonSerializer.Deserialize<string[]>(summary.Refs, Json) ?? [] : []));
        foreach (var row in recent.Where(row => row.Ordinal > cutoff).Reverse())
            result.AddRange(cached[row.Id]);
        return result;
    }, cancellationToken);

    private static async Task<IReadOnlyList<ChatHistoryMessage>> HistoryMessagesAsync(DbConnection connection, DbTransaction transaction, TurnRow row, CancellationToken token)
    {
        List<ChatHistoryMessage> messages = [];
        var turn = ToTurn(row);
        if (row.PromptRedacted is null) messages.Add(new("user", turn.Prompt, []));
        if (row.AnswerRedacted is null && turn.Answer is not null)
            messages.Add(new("assistant", turn.Answer.Text, await ReferencesAsync(connection, transaction, row.Id, token)));
        var outcomes = await connection.QueryAsync<HistoryOutcome>(Sql("""
            SELECT o.tool Tool,o.status Status,p.bytes Bytes,p.redacted_at RedactedAt,p.document_refs_json Refs
            FROM pending_operations o JOIN chat_proposal_details d ON d.proposal_id=o.id JOIN payloads p ON p.id=o.payload_id
            WHERE o.turn_id=@id ORDER BY o.created_at,o.id LIMIT 32
            """, new { id = row.Id }, transaction, token));
        foreach (var outcome in outcomes)
        {
            var plan = outcome.RedactedAt is null ? JsonSerializer.Deserialize<ChatWritePlan>(outcome.Bytes, Json) : null;
            var detail = plan is null ? "details removed after a source purge" : "document " + plan.DocumentId;
            messages.Add(new("assistant", $"Recorded operation status from the server: {outcome.Tool} is {outcome.Status}; {detail}.",
                plan is null ? [] : JsonSerializer.Deserialize<string[]>(outcome.Refs, Json) ?? []));
        }
        return messages;
    }

    private static string SummaryText(SummaryRow summary) => summary.RedactedAt is null
        ? JsonSerializer.Deserialize<string>(summary.Bytes, Json) ?? ""
        : "Earlier conversation summary was removed because a source was purged.";
    private sealed class SummaryRow
    {
        public int ThroughOrdinal { get; set; }
        public byte[] Bytes { get; set; } = [];
        public string Refs { get; set; } = "[]";
        public string? RedactedAt { get; set; }
    }
    private sealed class HistoryOutcome
    {
        public string Tool { get; set; } = "";
        public string Status { get; set; } = "";
        public byte[] Bytes { get; set; } = [];
        public string? RedactedAt { get; set; }
        public string Refs { get; set; } = "[]";
    }
}
