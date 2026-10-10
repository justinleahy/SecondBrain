using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Ingestion;
using Xunit;

namespace SecondBrain.Core.Tests.Retrieval;

public sealed class TextProcessingTests
{
    [Theory]
    [InlineData("English words and punctuation. ")]
    [InlineData("你好世界。 ")]
    [InlineData("👩🏽‍💻🌏 ")]
    [InlineData("e\u0301\t\r\n")]
    public void UnicodeChunksBoundExactEmbeddingBytesAndCoverLosslessInput(string phrase)
    {
        var text = string.Concat(Enumerable.Repeat(phrase, 100));
        var projection = TextProcessor.Process("fixture", "多言語 🌏", text, maximumTokens: 96, targetTokens: 64, overlapTokens: 12);
        var covered = new bool[text.Length];
        foreach (var chunk in projection.Chunks)
        {
            Assert.Equal(text[chunk.StartOffset..chunk.EndOffset], chunk.Text);
            var input = Encoding.UTF8.GetBytes(chunk.Context + "\n" + chunk.Text);
            Assert.InRange(input.Length, 1, 96);
            Assert.Equal(input.Length, chunk.TokenCount);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(input)), chunk.InputHash);
            Assert.False(char.IsLowSurrogate(chunk.Text[0]));
            Assert.False(char.IsHighSurrogate(chunk.Text[^1]));
            for (var index = chunk.StartOffset; index < chunk.EndOffset; index++) covered[index] = true;
        }
        Assert.All(covered, Assert.True);
    }

    [Fact]
    public void HeadingHierarchyChangesContextWithoutCrossingSectionBoundary()
    {
        const string text = "# First\nIntro paragraph.\n\n## Detail\nA detail.\n\n# Next\nFinal paragraph.\n";
        var projection = TextProcessor.Process("fixture", "Notebook", text);
        Assert.Equal(new[] { "Notebook > First", "Notebook > First > Detail", "Notebook > Next" }, projection.Chunks.Select(chunk => chunk.Context));
        Assert.Equal(text, string.Concat(projection.Chunks.Select(chunk => chunk.Text)));
    }

    [Theory]
    [InlineData("```text\n# Not a heading\n0123456789012345678901234567890123456789\n```\n")]
    [InlineData("| Name | Value |\n| --- | --- |\n| One | 123456789012345678901234567890 |\n")]
    public void BoundedFencesAndTablesRemainInOneChunk(string block)
    {
        var text = "# Heading\n\n" + block + "\nFollowing paragraph.";
        var projection = TextProcessor.Process("fixture", "Notebook", text, maximumTokens: 160, targetTokens: 40, overlapTokens: 8);
        Assert.Contains(projection.Chunks, chunk => chunk.Text.Contains(block, StringComparison.Ordinal));
        Assert.All(projection.Chunks, chunk => Assert.Equal("Notebook > Heading", chunk.Context));
        Assert.All(projection.Chunks, chunk => Assert.InRange(chunk.TokenCount, 1, 160));
    }

    [Fact]
    public void OversizedFenceSplitsWithinBoundWithoutDroppingText()
    {
        var text = "```\n" + string.Concat(Enumerable.Repeat("界🌏\n", 100)) + "```\n";
        var projection = TextProcessor.Process("fixture", "Code", text, maximumTokens: 80, targetTokens: 64, overlapTokens: 8);
        Assert.True(projection.Chunks.Count > 1);
        var covered = new bool[text.Length];
        foreach (var chunk in projection.Chunks)
        {
            Assert.InRange(chunk.TokenCount, 1, 80);
            for (var index = chunk.StartOffset; index < chunk.EndOffset; index++) covered[index] = true;
        }
        Assert.All(covered, Assert.True);
    }

    [Fact]
    public void NormalizedOffsetsResolveEachSearchCharacterToTheLosslessSource()
    {
        const string text = "  Hello\r\n\tworld  🌏\n";
        var projection = TextProcessor.Process("fixture", "Offsets", text);
        Assert.Equal(" Hello world 🌏 ", projection.SearchText);
        Assert.Equal(text, projection.LosslessText);
        Assert.Equal(text.Length, projection.OffsetMap[^1]);
        Assert.Equal(projection.SearchText.Length + 1, projection.OffsetMap.Length);
        for (var i = 0; i < projection.SearchText.Length; i++)
        {
            var original = text[projection.OffsetMap[i]];
            Assert.Equal(projection.SearchText[i], char.IsWhiteSpace(original) ? ' ' : original);
            Assert.True(projection.OffsetMap[i + 1] > projection.OffsetMap[i]);
        }
    }

    [Fact]
    public void TinyModelCannotAcceptAnUnboundedSingleCharacter()
    {
        var error = Assert.Throws<IngestionConflictException>(() => TextProcessor.Process("fixture", "", "🌏", maximumTokens: 4, targetTokens: 3, overlapTokens: 0));
        Assert.Equal("embedding-input-bound", error.Code);
    }

    [Fact]
    public void SearchNormalizationComposesNfcAndMapsBackToOriginalCombiningSequence()
    {
        const string text = "Cafe\u0301\u200B  de\u0301ja\u0300\uFEFF";
        var projection = TextProcessor.Process("fixture", "", text);
        Assert.Equal("Café déjà", projection.SearchText);
        Assert.Equal(text, projection.LosslessText);
        var offset = projection.SearchText.IndexOf('é');
        Assert.Equal("e\u0301\u200B", text[projection.OffsetMap[offset]..projection.OffsetMap[offset + 1]]);
        Assert.Equal(text.Length, projection.OffsetMap[^1]);
    }

    [Fact]
    public void SearchNormalizationPreservesWhitespaceInsideCodeAndTables()
    {
        const string code = "```\n  a\t  b\n\n```\n";
        const string table = "| A  | B |\n| -- | -- |\n| 1  |  2 |\n";
        var projection = TextProcessor.Process("fixture", "", "Outside   spaces\n" + code + "\n" + table);
        Assert.StartsWith("Outside spaces ", projection.SearchText);
        Assert.Contains(code, projection.SearchText, StringComparison.Ordinal);
        Assert.Contains(table, projection.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCaptureStillHasBoundedTitleChunk()
    {
        var projection = TextProcessor.Process("fixture", new string('界', 20), "", maximumTokens: 16, targetTokens: 8, overlapTokens: 2);
        var chunk = Assert.Single(projection.Chunks);
        Assert.InRange(chunk.TokenCount, 1, 16);
        Assert.Empty(chunk.Text);
    }
}
