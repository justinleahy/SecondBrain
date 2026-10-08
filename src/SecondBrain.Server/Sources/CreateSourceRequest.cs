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
