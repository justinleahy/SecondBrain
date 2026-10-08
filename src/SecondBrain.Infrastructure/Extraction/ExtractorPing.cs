using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace SecondBrain.Infrastructure.Extraction;

/// <summary>Bounded ping over the M0 extractor's versioned, big-endian length framing.</summary>
public static class ExtractorPing
{
    public static async ValueTask<bool> CheckAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var id = Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, id, operation = "ping" });
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
            await stream.WriteAsync(header, deadline.Token);
            await stream.WriteAsync(payload, deadline.Token);
            await stream.ReadExactlyAsync(header, deadline.Token);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is 0 or > 65536) return false;
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, deadline.Token);
            using var response = JsonDocument.Parse(bytes);
            var root = response.RootElement;
            return root.TryGetProperty("version", out var version) && version.GetInt32() == 1 &&
                root.TryGetProperty("id", out var responseId) && responseId.GetString() == id &&
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("result", out var result) && result.GetString() == "pong";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is IOException or SocketException or JsonException or
            InvalidOperationException or ArgumentException or PlatformNotSupportedException) { return false; }
    }
}
