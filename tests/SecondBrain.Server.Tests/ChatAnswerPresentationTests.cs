using SecondBrain.Core.Chat;
using SecondBrain.Server.Chat;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ChatAnswerPresentationTests
{
    [Fact]
    public void SparseSourceNumbersAndEveryTextReferenceAreRenumberedTogether()
    {
        var first = Source(1, "First");
        var third = Source(3, "Third");
        var original = new ChatAnswer("First fact [1]. Third fact [3]. Again [3].", [new(first), new(third, "changed")]);

        var display = ChatAnswerPresentation.ForDisplay(original);

        Assert.Equal("First fact [1]. Third fact [2]. Again [2].", display.Text);
        Assert.Equal(new[] { 1, 2 }, display.Citations.Select(citation => citation.Source.Number));
        Assert.Equal(third with { Number = 2 }, display.Citations[1].Source);
        Assert.Equal("changed", display.Citations[1].State);
        Assert.Equal("First fact [1]. Third fact [3]. Again [3].", original.Text);
        Assert.Equal(3, original.Citations[1].Source.Number);
    }

    [Fact]
    public void ASingleCitationStartsAtOneWithoutChangingItsRegistryNumber()
    {
        var third = Source(3, "Third");
        var original = new ChatAnswer("Only the third source [3].", [new(third)]);
        var display = ChatAnswerPresentation.ForDisplay(original);
        Assert.Equal("Only the third source [1].", display.Text);
        Assert.Equal(third.DocumentId, Assert.Single(display.Citations).Source.DocumentId);
        Assert.Equal(3, Assert.Single(original.Citations).Source.Number);
    }

    private static ChatSource Source(int number, string title) =>
        new(number, "document-" + number, 1, "generation", "chunk-" + number, 0, null, title, "Exact excerpt", "hash");
}
