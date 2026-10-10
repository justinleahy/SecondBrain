using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecondBrain.Extractor.Wire;

/// <summary>Versioned, bounded socket framing shared by the sandbox and the daemon transport adapter.</summary>
public static class ExtractorProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 64 * 1024;
    public const int MaximumProbeBytes = 64 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static JsonSerializerOptions ResultJsonOptions { get; } = new(JsonOptions) { MaxDepth = 16 };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
}
