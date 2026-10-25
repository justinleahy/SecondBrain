using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Chat;
using Xunit;

namespace SecondBrain.Core.Tests;

public sealed class NativeCitationTests
{
    [Fact]
    public void ExactSpansPreserveTheCapturedPassageAndCannotBeForgedByNumericText()
    {
        var source=Source("The code is Blue Otter. More text.");var builder=new NativeCitationAnswer();builder.Register([source]);
        builder.Append(new TextContent("The code is Blue Otter ["));builder.Append(new TextContent("1]."));
        builder.Append(new CitationSpanContent(1,12,22,"Blue Otter"));
        var answer=builder.Verify();var citation=Assert.Single(answer.Citations);
        Assert.Equal("span",citation.Granularity);Assert.Equal(source,citation.Source);Assert.Equal("Blue Otter",Assert.Single(citation.Spans!).Excerpt);
        Assert.Equal("The code is Blue Otter .[1]",answer.Text);Assert.Equal(1,answer.InvalidCitationCount);
        var persisted=Assert.Single(CitationVerifier.VerifyAnswer(answer,[source]).Citations);
        Assert.Equal(citation.Source,persisted.Source);Assert.Equal(citation.Spans!,persisted.Spans!);
    }
    [Fact]
    public void SeveralSpansUseOneSourceNumberWithoutLosingTheirRanges()
    {
        var source=Source("Blue Otter and Red Fox");var builder=new NativeCitationAnswer();builder.Register([source]);
        builder.Append(new CitationSpanContent(1,0,10,"Blue Otter"));builder.Append(new CitationSpanContent(1,15,22,"Red Fox"));builder.Append(new CitationSpanContent(1,0,10,"Blue Otter"));
        var answer=builder.Verify();Assert.Equal(2,Assert.Single(answer.Citations).Spans!.Count);Assert.Equal("[1][1][1]",answer.Text);
    }
    [Theory]
    [InlineData(99,0,10,"Blue Otter")]
    [InlineData(1,-1,10,"Blue Otter")]
    [InlineData(1,0,100,"Blue Otter")]
    [InlineData(1,0,10,"Red Fox")]
    [InlineData(1,0,0,"")]
    public void InvalidCandidatesCannotCreateAnEvidenceMarker(int number,int start,int end,string quote)
    {
        var builder=new NativeCitationAnswer();builder.Register([Source("Blue Otter")]);
        Assert.Equal("",builder.Append(new CitationSpanContent(number,start,end,quote)));
        var answer=builder.Verify();Assert.Empty(answer.Citations);Assert.Equal(1,answer.InvalidCitationCount);Assert.Contains("no_verified_sources",answer.Warnings!);
    }
    [Fact]
    public void SourceTamperingAndIdentityChangesAreRejected()
    {
        var source=Source("Blue Otter");var builder=new NativeCitationAnswer();builder.Register([source]);
        Assert.Throws<ChatConflictException>(()=>builder.Register([source with {Excerpt="forged"}]));
        Assert.Throws<ChatConflictException>(()=>builder.Register([Source("Red Fox")]));
    }
    [Fact]
    public void SplittingASurrogatePairAndForgingPrivateMarkersAreRejected()
    {
        var builder=new NativeCitationAnswer();builder.Register([Source("😀 Blue Otter")]);
        builder.Append(new TextContent("\uE0001\uE001"));builder.Append(new CitationSpanContent(1,0,1,"\uD83D"));
        var answer=builder.Verify();Assert.Empty(answer.Citations);Assert.DoesNotContain("[1]",answer.Text);
    }
    [Fact]
    public void CitationBytesAndCountAreBounded()
    {
        var builder=new NativeCitationAnswer(1);builder.Register([Source("Blue Otter")]);
        Assert.Throws<ChatLimitException>(()=>builder.Append(new CitationSpanContent(1,0,10,"Blue Otter")));
        builder=new();builder.Register([Source("Blue Otter")]);for(var i=0;i<128;i++)builder.Append(new CitationSpanContent(-1,0,1,"x"));
        Assert.Throws<ChatLimitException>(()=>builder.Append(new CitationSpanContent(-1,0,1,"x")));
    }
    [Fact]
    public void PersistenceBoundaryRechecksEveryNativeSpan()
    {
        var source=Source("Blue Otter");var forged=new ChatAnswer("A claim [1]",[new(source,Spans:[new(0,10,"Red Fox","forged")])]);
        var verified=CitationVerifier.VerifyAnswer(forged,[source]);Assert.Empty(verified.Citations);Assert.DoesNotContain("[1]",verified.Text);
        Assert.Contains("invalid_citations_removed",verified.Warnings!);
    }
    private static ChatSource Source(string text)=>new(1,"document",1,"generation","chunk",0,null,"Fixture",text,Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
}
