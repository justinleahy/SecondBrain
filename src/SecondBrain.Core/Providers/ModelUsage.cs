using Microsoft.Extensions.AI;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Core.Providers;

public sealed record ModelUsageRequest(string CallId, string Provider, string Model, string Role, long ReservedTokens,
    IngestionAuthority? Actor = null, string? DocumentId = null, long? Revision = null, string? JobId = null);

public sealed record ModelUsageTotals(long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? TotalTokens)
{
    public static ModelUsageTotals? From(UsageDetails? usage) => usage is null ? null :
        new(usage.InputTokenCount, usage.OutputTokenCount, usage.CachedInputTokenCount, usage.TotalTokenCount);
}

public sealed record ModelUsageRow(string? Provider, string? Model, string? Role, string? CredentialId, string? JobId,
    long Calls, long ReservedTokens, long ChargedTokens, long? InputTokens, long? OutputTokens,
    long? CachedInputTokens, long UnsettledCalls);
public sealed record ModelUsageReport(DateTimeOffset From, DateTimeOffset To, long DailyTokens, long DailyLimit,
    IReadOnlyList<ModelUsageRow> Rows, bool Truncated);

/// <summary>Durable reservations shared by all provider roles. Unknown usage keeps the reservation charged.</summary>
public interface IModelUsage
{
    Task<long> ReserveAsync(ModelUsageRequest request, CancellationToken token = default);
    Task SettleAsync(long id, ModelUsageTotals? totals, CancellationToken token = default);
    Task<ModelUsageReport> ReadAsync(IngestionAuthority actor, DateTimeOffset from, DateTimeOffset to, CancellationToken token = default);
}

public sealed class ModelBudgetException() : InvalidOperationException("The model token budget is exhausted.");
