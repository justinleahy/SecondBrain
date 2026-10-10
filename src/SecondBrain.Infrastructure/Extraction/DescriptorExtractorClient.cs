using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Extraction;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Extractor.Wire;

namespace SecondBrain.Infrastructure.Extraction;

/// <summary>Only immutable staged read-only descriptors are admitted; no source path crosses the socket.</summary>
public sealed class DescriptorExtractorClient(string socketPath) : IExtractorClient
{
    public ValueTask<ExtractionResult> ExtractAsync(Stream stagedInput, string formatHint, CancellationToken cancellationToken = default)
    {
        if (stagedInput is not FileStream file) throw new ArgumentException("Extraction requires a staged file descriptor.", nameof(stagedInput));
        return new ValueTask<ExtractionResult>(Task.Run(() => Extract(file, formatHint, cancellationToken), cancellationToken));
    }

    private ExtractionResult Extract(FileStream file, string hint, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            UnixDescriptorTransport.RequireReadOnlyRegularFile(file.SafeFileHandle);
            socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token).AsTask().GetAwaiter().GetResult();
            var id = Guid.NewGuid().ToString("N");
            UnixDescriptorTransport.Send(socket, ExtractorProtocol.Serialize(new ExtractorRequest(id, "extract_v2", FormatHint: hint)), file.SafeFileHandle, deadline.Token);
            using var output = new MemoryStream();
            for (var sequence = 0; sequence < 1024; sequence++)
            {
                using var frame = UnixDescriptorTransport.Receive(socket, deadline.Token);
                if (frame.Descriptor is not null) throw new InvalidDataException("Unexpected result descriptor.");
                var chunk = JsonSerializer.Deserialize<ExtractionChunk>(frame.Json, ExtractorProtocol.JsonOptions) ?? throw new InvalidDataException();
                if (chunk.Id != id || chunk.Version != 2 || chunk.Sequence != sequence || chunk.Data is null || chunk.Data.Length > 45000) throw new InvalidDataException();
                var data = Convert.FromBase64String(chunk.Data);
                if (data.Length > 32768 || output.Length + data.Length > 20 * 1024 * 1024) return Failure("output_bytes");
                output.Write(data);
                if (!chunk.Final) continue;
                var result = JsonSerializer.Deserialize<ExtractionResult>(output.GetBuffer().AsSpan(0, checked((int)output.Length)), ExtractorProtocol.ResultJsonOptions) ?? throw new InvalidDataException();
                if (result.LosslessText is null || Encoding.UTF8.GetByteCount(result.LosslessText) > 10 * 1024 * 1024 || (result.Tags?.Count ?? 0) + (result.Aliases?.Count ?? 0) > 200 || result.Links?.Count > 200 || result.Pages?.Count > 2000) return Failure("output_bounds");
                if (result.DeclaredCreatedAt?.Length > 128 || result.DeclaredUpdatedAt?.Length > 128 || result.Aliases?.Any(value => value is null || value.Length > 4096) == true || result.Title?.Length > 4096 || result.DeclaredType?.Length > 128 || result.Tags?.Any(value => value is null || value.Length > 4096) == true || result.Links?.Any(value => value is null || value.Length > 4096) == true || result.Properties is not null && JsonSerializer.SerializeToUtf8Bytes(result.Properties).Length > 65536) return Failure("metadata_bytes");
                if (result.PhysicalFormat is not ("markdown" or "text" or "pdf" or "html" or "docx" or "unknown") || result.MimeType is null || result.MimeType.Length > 128) throw new InvalidDataException();
                if (result.Status is not ("extracted" or "needs_ocr" or "failed" or "skipped")) throw new InvalidDataException();
                if (result.Pages is not null && result.Pages.Any(page => page.Number < 1 || page.Start < 0 || page.Length < 0 || (long)page.Start + page.Length > result.LosslessText.Length)) throw new InvalidDataException();
                return result;
            }
            return Failure("output_chunks");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("skipped", "unknown", "application/octet-stream", "", Error: "bound_exceeded", Bound: "extraction_deadline"); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or JsonException or FormatException or InvalidOperationException)
        { return new("failed", "unknown", "application/octet-stream", "", Error: "extractor_protocol_failed"); }
    }
    private static ExtractionResult Failure(string bound) => new("skipped", "unknown", "application/octet-stream", "", Error: "bound_exceeded", Bound: bound);
}
