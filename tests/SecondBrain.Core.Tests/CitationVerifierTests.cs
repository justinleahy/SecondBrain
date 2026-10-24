using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Chat;
using Xunit;

namespace SecondBrain.Core.Tests;

public sealed class CitationVerifierTests
{
    [Fact]
    public void OnlyRegisteredMarkersWithVerifiedExcerptsBecomeCitations()
    {
        var source = Source("Blue Otter");
        var answer = CitationVerifier.Verify("It is Blue Otter [1]. Invented source [99]. Again [1].", [source]);
        Assert.Single(answer.Citations); Assert.Equal(source, answer.Citations[0].Source);
        Assert.Equal(1, answer.InvalidCitationCount); Assert.DoesNotContain("[99]", answer.Text);
        Assert.Contains("invalid_citations_removed", answer.Warnings!);
    }

    [Fact]
    public void TamperedExcerptAndOversizedNumericMarkersAreRejected()
    {
        var source = Source("Blue Otter") with { Excerpt = "Red Fox" };
        var answer = CitationVerifier.Verify("Red Fox [1] [99999999999999999999999]", [source]);
        Assert.Empty(answer.Citations); Assert.Equal(2, answer.InvalidCitationCount);
        Assert.Contains("no_verified_sources", answer.Warnings!);
    }

    [Fact]
    public void UncitedTextIsExplicitlyUngrounded()
    {
        var answer = CitationVerifier.Verify("I do not have enough evidence.", [Source("Blue Otter")]);
        Assert.Empty(answer.Citations); Assert.Contains("no_verified_sources", answer.Warnings!);
    }

    private static ChatSource Source(string excerpt) => new(1, "document", 3, "{\"chunker\":1}", "document:0", 0, null, "Note", excerpt,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(excerpt))));
}
