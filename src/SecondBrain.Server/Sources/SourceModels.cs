using System.Text.Json.Serialization;

namespace SecondBrain.Server.Sources;

public sealed record CreateSourceRequest
{
    public string? Name { get; init; }
    public string Path { get; init; } = "";
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];
    public bool Recursive { get; init; } = true;
    public string Mode { get; init; } = "index";
    public bool Enrich { get; init; }

    [JsonPropertyName("default_type")]
    public string? DefaultType { get; init; }

    [JsonPropertyName("type_map")]
    public IReadOnlyDictionary<string, string> TypeMap { get; init; } = new Dictionary<string, string>();

    public string Watch { get; init; } = "events";

    [JsonPropertyName("poll_interval_s")]
    public int PollIntervalSeconds { get; init; } = 600;
}

/// <summary>Folder configuration persisted in sources.config_json using the specification's names.</summary>
public sealed record FolderSourceConfiguration(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("include")] IReadOnlyList<string> Include,
    [property: JsonPropertyName("exclude")] IReadOnlyList<string> Exclude,
    [property: JsonPropertyName("recursive")] bool Recursive,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("enrich")] bool Enrich,
    [property: JsonPropertyName("default_type")] string? DefaultType,
    [property: JsonPropertyName("type_map")] IReadOnlyDictionary<string, string> TypeMap,
    [property: JsonPropertyName("watch")] string Watch,
    [property: JsonPropertyName("poll_interval_s")] int PollIntervalSeconds);

public sealed record SourceRecord(
    string Id,
    string Kind,
    string Name,
    FolderSourceConfiguration Config,
    string Status,
    [property: JsonPropertyName("last_scan_at")] DateTimeOffset? LastScanAt,
    [property: JsonPropertyName("last_full_scan_at")] DateTimeOffset? LastFullScanAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public interface ISourceRepository
{
    Task AddAsync(SourceRecord source, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SourceRecord>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed record SourcePathValidation(bool Accepted, string? CanonicalPath, string? Reason);

public interface ISourcePathValidator
{
    SourcePathValidation Validate(string path);
}
