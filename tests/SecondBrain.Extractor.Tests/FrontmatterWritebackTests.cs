using System.Text;
using SecondBrain.Core.Ingestion;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Extractor;
using Xunit;

namespace SecondBrain.Extractor.Tests;

public sealed class FrontmatterWritebackTests
{
    private static readonly DocumentEnrichment Result = new("fingerprint", "A summary with \"quotes\" and \nnewlines.", ["garden", "a: b"], [], []);

    [Theory]
    [InlineData("Original body.\r\nWith café and 日本語.\r\n")]
    [InlineData("\ufeffOriginal body.\n")]
    public void AddsGeneratedFrontmatterWithoutChangingBodyBytes(string input)
    {
        var original = Encoding.UTF8.GetBytes(input);
        var output = EnrichmentFrontmatter.Apply(original, Parse(original), Result);
        Assert.True(output.AsSpan().EndsWith(Encoding.UTF8.GetBytes(input.TrimStart('\ufeff'))));
        Assert.Equal(input.StartsWith('\ufeff'), Encoding.UTF8.GetString(output).StartsWith('\ufeff'));
        var parsed = Parse(output);
        Assert.Equal("extracted", parsed.Status); Assert.True(parsed.Frontmatter!.Writable);
        Assert.Contains("secondbrain_enrichment:", parsed.LosslessText);
    }

    [Fact]
    public void PreservesAuthoredFieldsAndReplacesOnlyItsOwnedBlock()
    {
        const string before = "---\r\ntitle: Original\r\ntags: [authored]\r\naliases: [Other]\r\ncustom: untouched\r\nprops:\r\n  author: Ada\r\n---\r\n# Body\r\nKeep [link](https://example.test).";
        var original = Encoding.UTF8.GetBytes(before);
        var first = EnrichmentFrontmatter.Apply(original, Parse(original), Result);
        var second = EnrichmentFrontmatter.Apply(first, Parse(first), Result with { Summary = "Replacement" });
        var rendered = Encoding.UTF8.GetString(second);
        Assert.Equal(1, rendered.Split(FrontmatterLayout.StartMarker).Length - 1);
        Assert.DoesNotContain("A summary", rendered);
        Assert.Contains("Replacement", rendered);
        var parsed = Parse(second);
        Assert.Equal(new[] { "authored" }, parsed.Tags); Assert.Equal("Original", parsed.Title);
        Assert.Equal("Ada", parsed.Properties!["author"].GetString());
        Assert.Equal(new[] { "Other" }, parsed.Aliases);
        var block = parsed.Frontmatter!;
        Assert.Equal(before, rendered.Remove(block.GeneratedStart!.Value, block.GeneratedLength));
    }

    [Theory]
    [InlineData("---\n---\nBody")]
    [InlineData("---\r\n---\r\nBody")]
    public void EmptyFrontmatterRemainsValid(string text)
    {
        var input = Encoding.UTF8.GetBytes(text);
        var result = EnrichmentFrontmatter.Apply(input, Parse(input), Result);
        var parsed = Parse(result); Assert.Equal("extracted", parsed.Status);
        var span = parsed.Frontmatter!;
        Assert.Equal(text, Encoding.UTF8.GetString(result).Remove(span.GeneratedStart!.Value, span.GeneratedLength));
    }

    [Theory]
    [InlineData("---\nsecondbrain_enrichment: {summary: authored}\n---\nbody")]
    [InlineData("---\n# secondbrain-enrichment:v1\nsecondbrain_enrichment: {}\n---\nbody")]
    [InlineData("---\na: &x [1]\nb: *x\n---\nbody")]
    public void RefusesAuthoredCollisionsIncompleteMarkersAndUnsafeYaml(string text)
    {
        var input = Encoding.UTF8.GetBytes(text);
        Assert.Throws<InvalidDataException>(() => EnrichmentFrontmatter.Apply(input, Parse(input), Result));
    }

    [Fact]
    public void RejectsForgedRangesAndNonOwnedReplacement()
    {
        const string text = "---\ntitle: keep\n---\nbody";
        var bytes = Encoding.UTF8.GetBytes(text); var result = Parse(bytes);
        Assert.Throws<InvalidDataException>(() => EnrichmentFrontmatter.Apply(bytes, result with { Frontmatter = new(16, 4, 12) }, Result));
        Assert.Throws<InvalidDataException>(() => EnrichmentFrontmatter.Apply(bytes, result with { Frontmatter = new(200) }, Result));
        Assert.Throws<InvalidDataException>(() => EnrichmentFrontmatter.Apply(bytes, result with { LosslessText = "changed" }, Result));
    }

    private static ExtractionResult Parse(byte[] bytes)
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllBytes(path, bytes); using var file = File.OpenRead(path); return DocumentParser.Extract(file.SafeFileHandle, ".md", CancellationToken.None); }
        finally { File.Delete(path); }
    }
}
