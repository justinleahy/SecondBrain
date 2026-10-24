using System.Text.Json;
using SecondBrain.Core.Auth;

namespace SecondBrain.Core.Chat;

public sealed record ChatRequest(string Prompt, string? ConversationId = null, string Mode = "chat", string? RequestId = null);

/// <summary>A frozen policy snapshot. A reload cannot expand the authority of an in-flight turn.</summary>
public sealed record ChatTurnPolicy(
    string Provider, string Model, int MaxToolCalls = 8, int MaxReadDocuments = 12,
    int MaxReadBytes = 200_000, int MaxMetadataRecords = 200, int MaxOutputTokens = 2048,
    int MaxHistoryMessages = 20, long HourlyTokens = 200_000, long DailyTokens = 2_000_000,
    bool AutoApproveNotes = false);

public sealed record ChatConversation(string Id, string Title, string UpdatedAt, string? ActiveTurnId);
public sealed record ChatTurn(string Id, string ConversationId, int Ordinal, string Status, string Mode,
    string Prompt, ChatTurnPolicy Policy, string StartedAt, string? EndedAt, string? FailureCode,
    ChatAnswer? Answer, bool Redacted = false);
public sealed record ChatStart(ChatTurn Turn, bool Created);
public sealed record ChatHistoryMessage(string Role, string Text, string[] DocumentReferences);
public sealed record ChatEvent(long Sequence, string Kind, JsonElement? Data, bool Redacted, string CreatedAt);
public sealed record ChatSourceRequest(string DocumentId, long Revision, string ChunkId);

/// <summary>Source bytes and their exact version, captured from the authoritative projection.</summary>
public sealed record ChatSource(int Number, string DocumentId, long Revision, string Generation,
    string ChunkId, int Ordinal, int? Page, string Title, string Excerpt, string Sha256);
public sealed record ChatCitation(ChatSource Source, string State = "current");
public sealed record ChatAnswer(string Text, IReadOnlyList<ChatCitation> Citations,
    int InvalidCitationCount = 0, string[]? Warnings = null);

/// <summary>Durable conversation authority, replay, lineage, and model-call accounting.</summary>
public interface IChatRepository
{
    ValueTask<ChatStart> StartAsync(ChatRequest request, ChatTurnPolicy policy, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ChatConversation>> ListAsync(AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ChatTurn>> GetConversationAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<ChatTurn> GetTurnAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ChatHistoryMessage>> HistoryAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ChatEvent>> EventsAsync(string turnId, long after, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<long> AppendAsync(string turnId, string kind, JsonElement data, IReadOnlyList<string> documentReferences,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ChatSource>> ExposeAsync(string turnId, IReadOnlyList<ChatSourceRequest> sources,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ChatSource>> SourcesAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask CountToolAsync(string turnId, int metadataRecords, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask CheckActiveAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask ReserveTokensAsync(string turnId, string callId, long tokens, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask SettleTokensAsync(string turnId, string callId, long? actualTokens, CancellationToken cancellationToken = default);
    ValueTask FinishAsync(string turnId, string status, ChatAnswer answer, string? failureCode = null, CancellationToken cancellationToken = default);
    ValueTask<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default);
}

public sealed class ChatConflictException(string message) : InvalidOperationException(message);
public sealed class ChatLimitException(string message) : InvalidOperationException(message);
public sealed class ChatNotFoundException() : InvalidOperationException("Conversation or turn was not found.");
