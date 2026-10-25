using System.Text.Json;

namespace SecondBrain.Core.Providers;

/// <summary>A bounded, tool-free enrichment request. Local ids carry no document text or identity.</summary>
public sealed record BatchChatRequest(string Id, string System, string Input, int MaxOutputTokens,
    JsonElement? Schema = null, string? Reasoning = null);
public sealed record BatchReceipt(string Id, string? InputFileId = null);
public sealed record BatchSnapshot(BatchReceipt Receipt, string State, bool Terminal,
    string? OutputFileId = null, string? ErrorFileId = null);
public sealed record BatchChatResult(string Id, string? Text, ModelUsageTotals? Usage, string? FailureCode);

/// <summary>Native batch lifecycle. The caller persists staging and submission intent before each external side effect.</summary>
public interface IBatchProvider
{
    Task<string?> StageAsync(string submissionKey, IReadOnlyList<BatchChatRequest> requests, CancellationToken token = default);
    Task<string?> FindStagedAsync(string submissionKey, CancellationToken token = default);
    Task<BatchReceipt> SubmitAsync(string submissionKey, string? stagedFileId, IReadOnlyList<BatchChatRequest> requests, CancellationToken token = default);
    /// <summary>Find a previously submitted request after an ambiguous response; absence never authorizes blind resubmission.</summary>
    Task<BatchReceipt?> FindAsync(string submissionKey, CancellationToken token = default);
    Task<BatchSnapshot> PollAsync(BatchReceipt receipt, CancellationToken token = default);
    Task CancelAsync(BatchReceipt receipt, CancellationToken token = default);
    Task<IReadOnlyList<BatchChatResult>> CollectAsync(BatchSnapshot snapshot, IReadOnlyList<string> expectedIds, CancellationToken token = default);
    Task DeleteFilesAsync(BatchSnapshot snapshot, CancellationToken token = default);
}
