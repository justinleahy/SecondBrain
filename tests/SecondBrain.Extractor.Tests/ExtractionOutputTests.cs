using System.Net.Sockets;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Extractor.Wire;
using SecondBrain.Extractor;
using SecondBrain.Infrastructure.Extraction;
using Xunit;

namespace SecondBrain.Extractor.Tests;

public sealed class ExtractionOutputTests
{
    [Fact]
    public async Task DescriptorTransportPreservesTerminalDeadlineBound()
    {
        var result = await Exchange(new("skipped", "unknown", "application/octet-stream", "", Error: "bound_exceeded", Bound: "extraction_deadline"));
        Assert.Equal("skipped", result.Status); Assert.Equal("extraction_deadline", result.Bound);
    }
    [Fact]
    public async Task DaemonRejectsDishonestDecodedTextBeyondBound()
    {
        var result = await Exchange(new("extracted", "text", "text/plain", new string('x', 10 * 1024 * 1024 + 1)));
        Assert.Equal("skipped", result.Status); Assert.Equal("output_bounds", result.Bound);
    }
    [Fact]
    public async Task DaemonRejectsPageOffsetsOutsideLosslessText()
    {
        var result = await Exchange(new("extracted", "pdf", "application/pdf", "short", Pages: [new(1, 5, int.MaxValue)]));
        Assert.Equal("extractor_protocol_failed", result.Error);
    }
    [Fact]
    public async Task DaemonRejectsTranscriptWithForgedSegmentOffsets()
    {
        var result = await Exchange(new("extracted", "vtt", "text/vtt", "short", Segments: [new(0, "Ada", 0, 1, 0, 500)]));
        Assert.Equal("extractor_protocol_failed", result.Error);
    }

    [Fact]
    public async Task DaemonRejectsCalendarCoverageDifferentFromItsRequest()
    {
        var wrong = new CalendarSnapshot(new("2025-01-01T00:00:00Z", "2025-02-01T00:00:00Z", "UTC"), [], []);
        var result = await Exchange(new("extracted", "calendar", "text/calendar", "", Calendar: wrong), calendar: true);
        Assert.Equal("extractor_protocol_failed", result.Error);
    }

    [Fact]
    public async Task DaemonRejectsCalendarInstanceWithoutAMember()
    {
        var wrong = new CalendarSnapshot(new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", "UTC"), [], [new("forged", "", "2026-10-04T00:00:00Z", "2026-10-04T01:00:00Z", "confirmed")]);
        var result = await Exchange(new("extracted", "calendar", "text/calendar", "", Calendar: wrong), calendar: true);
        Assert.Equal("extractor_protocol_failed", result.Error);
    }

    private static async Task<ExtractionResult> Exchange(ExtractionResult dishonest, bool calendar = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "sb-output-" + Guid.NewGuid().ToString("N")); var filePath = Path.GetTempFileName();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptAsync(timeout.Token); using var request = UnixDescriptorTransport.Receive(socket, timeout.Token);
                var parsed = System.Text.Json.JsonSerializer.Deserialize<ExtractorRequest>(request.Json, ExtractorProtocol.JsonOptions)!;
                ExtractorSocketServer.SendExtraction(socket, parsed.Id, dishonest, timeout.Token);
            });
            await using var file = File.OpenRead(filePath);
            var client = new DescriptorExtractorClient(path);
            var result = calendar
                ? await client.ExtractCalendarAsync(file, new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", "UTC"), timeout.Token)
                : await client.ExtractAsync(file, ".txt", timeout.Token);
            await server; return result;
        }
        finally { File.Delete(path); File.Delete(filePath); }
    }
}
