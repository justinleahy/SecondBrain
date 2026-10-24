using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Core.Chat;

public sealed record ChatWritePlan(string Operation, string DocumentId, long Revision, string Destination,
    JsonElement Payload, string MutationId, string[]? RelatedDocumentIds = null, bool AppendOnly = false);
public sealed record ChatProposal(string Id, string TurnId, string Tool, string Status, ChatWritePlan? Plan,
    string CreatedAt, string ExpiresAt, string? FailureCode = null, string? ExecutionId = null);

public interface IChatProposalRepository
{
    ValueTask<ChatProposal> ProposeAsync(string turnId, string tool, JsonElement arguments, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<ChatProposal> ProposeForCallAsync(string turnId, string tool, JsonElement arguments, string executionId, AuthenticatedCredential actor, CancellationToken cancellationToken = default)
        => ProposeAsync(turnId, tool, arguments, actor, cancellationToken);
    ValueTask<ChatProposal> ProposeExternalUpdateAsync(JsonElement arguments, AuthenticatedCredential actor, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("External proposals require the durable approval repository.");
    ValueTask<IReadOnlyList<ChatProposal>> ListAsync(string? conversationId, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<ChatProposal> ApproveAsync(string id, AuthenticatedCredential actor, bool automatic = false, CancellationToken cancellationToken = default);
    ValueTask<ChatProposal> DiscardAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<ChatProposal> EditAsync(string id, JsonElement arguments, AuthenticatedCredential actor, CancellationToken cancellationToken = default);
    ValueTask<int> RecoverApprovalsAsync(CancellationToken cancellationToken = default);
}

/// <summary>A durable approval claim, consumed only by the document writer after checking the stored plan.</summary>
public sealed record ApprovedDocumentWrite(string ProposalId, string ExecutionId, ChatWritePlan Plan);
