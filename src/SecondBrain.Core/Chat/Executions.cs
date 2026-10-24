using System.Text.Json;
using SecondBrain.Core.Auth;

namespace SecondBrain.Core.Chat;

public interface IChatExecutionRepository
{
    ValueTask<ChatEgressManifest> ManifestAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ChatToolActivity>> ActivityAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<string> BeginToolAsync(string turnId, string name, JsonElement arguments, int metadataRecords,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask CompleteToolAsync(string executionId, JsonElement result, IReadOnlyList<string> references,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask FailToolAsync(string executionId, string reason, CancellationToken cancellationToken = default);
}

public sealed record ChatEgressDocument(string DocumentId, long? Revision, string Origin);
public sealed record ChatEgressManifest(string Provider, string Model, IReadOnlyList<ChatEgressDocument> Documents,
    bool HistoryIncluded, bool HistoryCompacted, long ToolResultBytes, long ChargedTokens,
    IReadOnlyList<ChatProviderExposure>? ProviderCalls = null);
public sealed record ChatProviderExposure(string CallId, string Provider, string Model, string? Endpoint, string[] DocumentIds);
public sealed record ChatProviderTarget(string Provider, string Model, string? Endpoint);
public sealed record ChatToolActivity(string Id, string Name, string Status);
public sealed record ChatRecordedCall(string Id, string Name, string ArgumentsHash, JsonElement Arguments, JsonElement? Result,
    string Status, string[] DocumentReferences, bool Redacted = false);
public sealed record ChatResumeState(int Attempt, JsonElement? InitialContext, IReadOnlyList<ChatRecordedCall> Calls,
    string[] AllowedTools, bool HasCommittedWrite);
