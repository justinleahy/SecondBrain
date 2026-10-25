using System.Text;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Extractor;
using Xunit;

namespace SecondBrain.Extractor.Tests;

public sealed class TranscriptParserTests
{
    [Fact]
    public void WebVttPreservesOverlapsVoiceChangesUnicodeAndExactOffsets()
    {
        var result = Parse("WEBVTT\r\n\r\nNOTE ignored metadata\r\nprivate\r\n\r\nfirst\r\n00:01.250 --> 00:03.000 align:start\r\n<v Ada>Pricing &amp; α😀</v><v Bob>Yes.</v>\r\n\r\n00:02.000 --> 00:04.000\r\n<v Ada>Continued\r\non two lines.</v>\r\n", ".txt");
        Assert.Equal("extracted", result.Status); Assert.Equal("vtt", result.PhysicalFormat);
        Assert.Equal("Pricing & α😀\nYes.\nContinued\non two lines.", result.LosslessText);
        Assert.Equal(["Ada", "Bob", "Ada"], result.Segments!.Select(segment => segment.Speaker));
        Assert.Equal([1250L, 1250, 2000], result.Segments!.Select(segment => segment.StartMs));
        Assert.Equal("Yes.", result.LosslessText.Substring(result.Segments![1].Start, result.Segments[1].Length));
        Assert.True(TranscriptSegments.IsValid(result.LosslessText, result.Segments));
    }

    [Fact]
    public void SrtHasUnknownSpeakersAndPreservesMultilineCues()
    {
        var result = Parse("1\n00:00:01,500 --> 00:00:03,200\n<b>Hello</b>\nworld.\n\n2\n00:00:02,000 --> 00:00:04,000\nA second overlapping cue.\n", ".srt");
        Assert.Equal("srt", result.PhysicalFormat); Assert.Equal("Hello\nworld.\nA second overlapping cue.", result.LosslessText);
        Assert.All(result.Segments!, segment => Assert.Null(segment.Speaker));
        Assert.Equal(1500, result.Segments![0].StartMs); Assert.Equal(3200, result.Segments[0].EndMs);
    }

    [Theory]
    [InlineData("WEBVTT\n\n00:62.000 --> 00:63.000\ninvalid minutes", ".vtt")]
    [InlineData("WEBVTT\n\n00:03.000 --> 00:01.000\nreverse", ".vtt")]
    [InlineData("WEBVTT\n\ninvalid cue\nplain", ".vtt")]
    [InlineData("1\n00:00:01,000 --> invalid\nplain", ".srt")]
    public void MalformedCuesNeverSilentlyBecomePlainText(string text, string hint)
    {
        var result = Parse(text, hint);
        Assert.Equal("failed", result.Status); Assert.Equal("malformed_transcript", result.Error); Assert.Empty(result.LosslessText);
    }

    [Fact]
    public void CueCountAndSpeakerLengthAreBounded()
    {
        var result = Parse("WEBVTT\n\n" + string.Concat(Enumerable.Repeat("00:00.000 --> 00:01.000\ncue\n\n", TranscriptSegments.MaximumCount + 1)), ".vtt");
        Assert.Equal("transcript_segments", result.Bound);
        result = Parse("WEBVTT\n\n00:00.000 --> 00:01.000\n<v " + new string('x', 257) + ">cue", ".vtt");
        Assert.Equal("transcript_speaker", result.Bound);
        result = Parse("WEBVTT\n\n00:00.000 --> 00:01.000\n" + string.Concat(Enumerable.Repeat("<v Ada>word</v>", TranscriptSegments.MaximumCount + 1)), ".vtt");
        Assert.Equal("transcript_segments", result.Bound);
    }

    [Fact]
    public void LiteralComparisonsAndUnknownTagsRemainText()
    {
        var result = Parse("WEBVTT\n\n00:00.000 --> 00:01.000\n<v Ada>cost < 10 > 5 <script>inert</script></v>", ".vtt");
        Assert.Equal("cost < 10 > 5 <script>inert</script>", result.LosslessText);
    }

    [Fact]
    public void SegmentValidationRejectsForgedRangesAndSplitSurrogates()
    {
        Assert.False(TranscriptSegments.IsValid("😀", [new(0, "Ada", 0, 1, 1, 1)]));
        Assert.False(TranscriptSegments.IsValid("one", [new(0, "Ada", 5, 1, 0, 3)]));
        Assert.False(TranscriptSegments.IsValid("one", [new(0, "Ada", 0, 1, 0, int.MaxValue)]));
        Assert.False(TranscriptSegments.IsValid("one", [new(0, "Ada\nBob", 0, 1, 0, 3)]));
    }

    private static ExtractionResult Parse(string text, string hint)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
            using var descriptor = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
            return DocumentParser.Extract(descriptor, hint, CancellationToken.None);
        }
        finally { File.Delete(path); }
    }
}
