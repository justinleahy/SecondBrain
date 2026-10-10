namespace SecondBrain.Core.Ingestion;

public sealed record ClaimedInput(string RelativeClaimPath, string OriginalName);
public interface IInboxClaims
{
    ValueTask<string> EnsureAsync(string incomingRoot, CancellationToken cancellationToken = default);
    ValueTask<ClaimedInput> ClaimAsync(string root, string relativePath, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ClaimedInput> EnumerateAsync(string root, CancellationToken cancellationToken = default);
    ValueTask CompleteAsync(string root, ClaimedInput claim, CancellationToken cancellationToken = default);
}
