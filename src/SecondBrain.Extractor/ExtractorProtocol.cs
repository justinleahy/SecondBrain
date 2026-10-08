using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace SecondBrain.Extractor;

/// <summary>Versioned, bounded socket framing shared with the future daemon client.</summary>
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

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
}

public sealed record ExtractorRequest(string Id, string Operation, int Version = ExtractorProtocol.Version);

public sealed record ExtractorResponse(
    string Id,
    bool Ok,
    string? Result = null,
    string? Error = null,
    int Version = ExtractorProtocol.Version,
    int? BytesRead = null,
    string? Sha256 = null);

/// <summary>Owns the received descriptor; disposing the frame closes it on every response/error path.</summary>
public sealed class ExtractorFrame(byte[] json, SafeFileHandle? descriptor) : IDisposable
{
    public byte[] Json { get; } = json;
    public SafeFileHandle? Descriptor { get; } = descriptor;
    public void Dispose() => Descriptor?.Dispose();
}
