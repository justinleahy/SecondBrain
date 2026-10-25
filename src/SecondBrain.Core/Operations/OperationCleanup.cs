using SecondBrain.Core.Ingestion;

namespace SecondBrain.Core.Operations;

public sealed record OperationCleanupResult(int Workspaces, int DebugLogs, int Deferred, bool MoreWork);

public interface IOperationCleanup
{
    Task<OperationCleanupResult> RunAsync(IngestionAuthority? actor = null, CancellationToken token = default);
}
