using System.Text.Json;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Core.Ingestion;

/// <summary>Immutable chunk bounds carried by the publication generation, including in-flight work.</summary>
public sealed record ChunkingProfile(int TargetTokens = 512, int MaxTokens = 1024, int OverlapTokens = 64)
{
    public static ChunkingProfile FromOptions(ChunkingOptions? value) => value is null ? new() : new(value.TargetTokens, value.MaxTokens, value.OverlapTokens);
    public string GenerationJson
    {
        get
        {
            var generation = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["chunker"] = TextProcessor.Generation, ["extractor"] = 1, ["normalization"] = 1 };
            // Keep the existing identity for the unchanged default profile.
            if (this != new ChunkingProfile())
            { generation["chunk_max_tokens"] = MaxTokens; generation["chunk_target_tokens"] = TargetTokens; generation["chunk_overlap_tokens"] = OverlapTokens; }
            return JsonSerializer.Serialize(generation);
        }
    }
    public static ChunkingProfile FromGeneration(string generation)
    {
        using var parsed = JsonDocument.Parse(generation);
        var root = parsed.RootElement;
        return root.TryGetProperty("chunk_target_tokens", out var target)
            ? new(target.GetInt32(), root.GetProperty("chunk_max_tokens").GetInt32(), root.GetProperty("chunk_overlap_tokens").GetInt32()) : new();
    }
    public string ApplyToGeneration(string generation)
    {
        using var parsed = JsonDocument.Parse(generation);
        var fields = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in parsed.RootElement.EnumerateObject())
            if (property.Name is not ("chunk_target_tokens" or "chunk_max_tokens" or "chunk_overlap_tokens")) fields[property.Name] = property.Value.Clone();
        if (this != new ChunkingProfile())
        {
            fields["chunk_target_tokens"] = JsonSerializer.SerializeToElement(TargetTokens);
            fields["chunk_max_tokens"] = JsonSerializer.SerializeToElement(MaxTokens);
            fields["chunk_overlap_tokens"] = JsonSerializer.SerializeToElement(OverlapTokens);
        }
        return JsonSerializer.Serialize(fields);
    }
    public TextProjection Process(string id, string title, string text, string[]? tags = null, string? path = null) =>
        TextProcessor.Process(id, title, text, tags, path, MaxTokens, TargetTokens, OverlapTokens);
}
