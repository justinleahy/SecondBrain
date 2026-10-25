using System.Text.Json;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Extractor.Wire;
using Xunit;

namespace SecondBrain.Extractor.Tests;

public sealed class ExtractionResultContractTests
{
    [Fact]
    public void ExtractionResultRoundTripsEveryFieldUnderTheSocketResultOptions()
    {
        var sent = new ExtractionResult("needs_ocr", "pdf", "application/pdf", "page text", "Title", "article",
            new Dictionary<string, JsonElement> { ["author"] = JsonSerializer.SerializeToElement("Ada") }, ["tag"], ["target"], [new(1, 0, 9)], "reason", "named_bound", Aliases: ["Alias"], DeclaredCreatedAt: "2026-01-01", DeclaredUpdatedAt: "2026-02-01", Segments: [new(0, "Ada", 1000, 2000, 0, 9)]);
        var json = JsonSerializer.SerializeToUtf8Bytes(sent, ExtractorProtocol.ResultJsonOptions);
        var received = JsonSerializer.Deserialize<ExtractionResult>(json, ExtractorProtocol.ResultJsonOptions)!;
        Assert.Equal(sent.Status, received.Status); Assert.Equal(sent.PhysicalFormat, received.PhysicalFormat); Assert.Equal(sent.MimeType, received.MimeType);
        Assert.Equal(sent.LosslessText, received.LosslessText); Assert.Equal(sent.Title, received.Title); Assert.Equal(sent.DeclaredType, received.DeclaredType);
        Assert.Equal("Ada", received.Properties!["author"].GetString()); Assert.Equal(sent.Tags, received.Tags); Assert.Equal(sent.Links, received.Links);
        Assert.Equal((1, 0, 9), (received.Pages![0].Number, received.Pages[0].Start, received.Pages[0].Length)); Assert.Equal(sent.Error, received.Error); Assert.Equal(sent.Bound, received.Bound);
        Assert.Equal(sent.Aliases, received.Aliases); Assert.Equal(sent.DeclaredCreatedAt, received.DeclaredCreatedAt); Assert.Equal(sent.DeclaredUpdatedAt, received.DeclaredUpdatedAt);
        Assert.Equal(sent.Segments, received.Segments);
        Assert.Equal(json, JsonSerializer.SerializeToUtf8Bytes(received, ExtractorProtocol.ResultJsonOptions));
    }
}
