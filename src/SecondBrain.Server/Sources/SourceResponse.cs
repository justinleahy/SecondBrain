using System.Text.Json.Serialization;
using SecondBrain.Core.Sources;

namespace SecondBrain.Server.Sources;

/// <summary>Public source metadata. No URL or protected envelope belongs to this type.</summary>
public sealed record SourceResponse(string Id, string Kind, string Name, string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FolderSourceConfiguration? Config { get; init; }
    [JsonPropertyName("last_scan_at")] public DateTimeOffset? LastScanAt { get; init; }
    [JsonPropertyName("last_full_scan_at")] public DateTimeOffset? LastFullScanAt { get; init; }
    public string? LastScanError { get; init; }
    public DateTimeOffset? LastScanErrorAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Origin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Freshness { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("poll_interval_s")] public int? PollIntervalSeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("last_attempt_at")] public DateTimeOffset? LastAttemptAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("last_success_at")] public DateTimeOffset? LastSuccessAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("last_change_at")] public DateTimeOffset? LastChangeAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("next_attempt_at")] public DateTimeOffset? NextAttemptAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("container_id")] public string? ContainerId { get; init; }
    public static SourceResponse From(SourceRecord value) => new(value.Id, value.Kind, value.Name, value.Status, value.CreatedAt)
    { Config = value.Config, LastScanAt = value.LastScanAt, LastFullScanAt = value.LastFullScanAt, LastScanError = value.LastScanError, LastScanErrorAt = value.LastScanErrorAt };
    public static SourceResponse From(CalendarSubscription value) => new(value.Id, value.Kind, value.Name, value.Status, value.CreatedAt)
    { Origin = value.Origin, Freshness = value.Freshness, Error = value.Error, PollIntervalSeconds = value.PollIntervalSeconds, LastAttemptAt = value.LastAttemptAt,
        LastSuccessAt = value.LastSuccessAt, LastChangeAt = value.LastChangeAt, NextAttemptAt = value.NextAttemptAt, ContainerId = value.ContainerId };
}
