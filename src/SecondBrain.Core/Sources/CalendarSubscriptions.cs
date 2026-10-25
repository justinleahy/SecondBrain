using System.Text.Json.Serialization;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Core.Sources;

public static class CalendarFreshness
{
    public static string State(string status, string? error, DateTimeOffset? lastSuccess, int interval, DateTimeOffset now) =>
        status == "disabled" ? "paused" : error is not null ? "failed" : lastSuccess is null ? "never_synced" : now - lastSuccess.Value > TimeSpan.FromSeconds((long)interval * 2) ? "stale" : "current";
}

public sealed record CalendarSubscription(string Id, string Name, string Origin, string Status,
    [property: JsonPropertyName("poll_interval_s")] int PollIntervalSeconds,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_attempt_at")] DateTimeOffset? LastAttemptAt,
    [property: JsonPropertyName("last_success_at")] DateTimeOffset? LastSuccessAt,
    [property: JsonPropertyName("last_change_at")] DateTimeOffset? LastChangeAt,
    [property: JsonPropertyName("next_attempt_at")] DateTimeOffset? NextAttemptAt,
    string? Error, string Freshness,
    [property: JsonPropertyName("container_id")] string? ContainerId)
{
    public string Kind => "calendar_subscription";
}

public interface ICalendarSubscriptionSecrets
{
    string Protect(string url);
    string Unprotect(string envelope);
}

public interface ICalendarFetchAuthority
{
    ValueTask<bool> IsCurrentAsync(string sourceId, long generation, CancellationToken cancellationToken = default);
}

public sealed class CalendarDownloadRequest(string sourceId, long generation, string url, string? etag = null, DateTimeOffset? modified = null)
{
    public string SourceId { get; } = sourceId;
    public long Generation { get; } = generation;
    [JsonIgnore] public string Url { get; } = url;
    public string? ETag { get; } = etag;
    public DateTimeOffset? Modified { get; } = modified;
    public override string ToString() => $"Calendar download for {SourceId}";
}

public sealed record CalendarDownloadResult(string Status, StagedInput? Snapshot = null, string? ETag = null,
    DateTimeOffset? Modified = null, string? Error = null, int? RetryAfterSeconds = null)
{
    public override string ToString() => $"Calendar download: {Status}, {Error}";
}

public interface ICalendarDownloadTransport
{
    ValueTask<CalendarDownloadResult> DownloadAsync(CalendarDownloadRequest request, CancellationToken cancellationToken = default);
}

public sealed record CalendarRefreshWork(string SourceId, string ContainerId, long Generation, string LeaseId,
    [property: JsonIgnore] string ProtectedUrl, string? ETag, DateTimeOffset? Modified, string? SnapshotHash,
    string? SnapshotPath, string? RejectedHash, string? RejectedGeneration, bool Deep, bool MaterializationOnly = false)
{
    public override string ToString() => $"Calendar refresh {SourceId}, generation {Generation}";
}

public sealed record CalendarSubscriptionPublication(CalendarRefreshWork Work, SecondBrain.Extraction.Contracts.CalendarSnapshot Calendar,
    string SnapshotPath, string SnapshotHash, long SnapshotLength, string? ETag, DateTimeOffset? Modified);

public interface ICalendarSubscriptionRepository : ICalendarFetchAuthority
{
    ValueTask<IReadOnlyList<ConfiguredCalendarState>> ListConfiguredAsync(CancellationToken token = default);
    ValueTask ReconcileConfiguredAsync(IReadOnlyList<ConfiguredCalendarRegistration> registrations, CancellationToken token = default);
    ValueTask<CalendarSubscription> CreateAsync(string name, string origin, string protectedUrl, int interval, bool enabled, IngestionAuthority actor, CancellationToken token = default);
    ValueTask<IReadOnlyList<CalendarSubscription>> ListAsync(CancellationToken token = default);
    ValueTask<CalendarSubscription?> GetAsync(string id, CancellationToken token = default, bool includeRemoved = false);
    ValueTask UpdateAsync(string id, string? name, int? interval, bool? enabled, IngestionAuthority actor, CancellationToken token = default);
    ValueTask QueueAsync(string id, bool deep, IngestionAuthority actor, CancellationToken token = default);
    ValueTask<IReadOnlyList<CalendarRefreshWork>> ClaimDueAsync(int limit = 4, CancellationToken token = default);
    ValueTask<IReadOnlyList<CalendarRefreshWork>> ClaimMaterializationAsync(SecondBrain.Extraction.Contracts.CalendarWindow window, int limit = 4, CancellationToken token = default);
    ValueTask RemoveAsync(string id, IngestionAuthority actor, CancellationToken token = default);
    ValueTask PurgeAsync(string id, IngestionAuthority actor, CancellationToken token = default);
    ValueTask<bool> ExpireAsync(string id, RetentionExpiry expiry, CancellationToken token = default);
    ValueTask<int> RecoverPurgesAsync(CancellationToken token = default);
    ValueTask FailAsync(CalendarRefreshWork work, string error, int? retryAfterSeconds = null, string? rejectedHash = null, string? processingGeneration = null, CancellationToken token = default);
    ValueTask CompleteUnchangedAsync(CalendarRefreshWork work, CancellationToken token = default);
}

public sealed record ConfiguredCalendarState(string Id, [property: JsonIgnore] string ProtectedUrl)
{
    public override string ToString() => $"Configured calendar {Id}";
}
public sealed record ConfiguredCalendarRegistration(string Id, string Name, string Origin, int Interval, bool Enabled,
    [property: JsonIgnore] string ProtectedUrl, [property: JsonIgnore] string? ExpectedProtectedUrl)
{
    public override string ToString() => $"Configured calendar {Id}";
}
