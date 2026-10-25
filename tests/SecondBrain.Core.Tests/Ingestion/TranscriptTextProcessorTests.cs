using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Ingestion;
using SecondBrain.Extraction.Contracts;
using Xunit;

namespace SecondBrain.Core.Tests.Ingestion;

public sealed class TranscriptTextProcessorTests
{
    [Fact]
    public void WindowsRespectSpeakerBoundariesAndCarryExactExcerptLocators()
    {
        var text = "first line\nsecond line\nthird line";
        ExtractionSegment[] segments = [new(0, "Ada", 1000, 2000, 0, 10), new(1, "Ada", 1500, 3000, 11, 11), new(2, "Bob", 1000, 4000, 23, 10)];
        var result = TranscriptTextProcessor.Process("document", "Meeting", text, segments, new());
        Assert.Equal(2, result.Chunks.Count);
        Assert.Equal(new(0, 1, "Ada", 1000, 3000), result.Chunks[0].Transcript);
        Assert.Equal(new(2, 2, "Bob", 1000, 4000), result.Chunks[1].Transcript);
        Assert.All(result.Chunks, chunk =>
        {
            Assert.Equal(text[chunk.StartOffset..chunk.EndOffset], chunk.Text);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Context + "\n" + chunk.Text))), chunk.InputHash);
        });
    }

    [Fact]
    public void LongUnicodeCueSplitsWithinConfiguredLimitsWithoutInventingTimes()
    {
        var text = string.Concat(Enumerable.Repeat("α😀", 200));
        var result = TranscriptTextProcessor.Process("document", "Meeting", text, [new(0, "Ada", 100, 500, 0, text.Length)], new(64, 128, 8));
        Assert.True(result.Chunks.Count > 1);
        Assert.All(result.Chunks, chunk =>
        {
            Assert.InRange(chunk.TokenCount, 1, 128);
            Assert.Equal(text[chunk.StartOffset..chunk.EndOffset], chunk.Text);
            Assert.Equal(new(0, 0, "Ada", 100, 500), chunk.Transcript);
            Assert.False(char.IsLowSurrogate(chunk.Text[0]));
            Assert.False(char.IsHighSurrogate(chunk.Text[^1]));
        });
    }
}
