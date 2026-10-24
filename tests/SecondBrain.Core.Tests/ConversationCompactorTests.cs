using System.Text;
using SecondBrain.Core.Chat;
using Xunit;

namespace SecondBrain.Core.Tests;

public sealed class ConversationCompactorTests
{
    [Fact]
    public void SummaryIsBoundedForUnicodeAndClearlyLabelsOmissions()
    {
        var messages = Enumerable.Range(0, 100).Select(i => new ChatHistoryMessage("user", "Note " + i + " " + string.Concat(Enumerable.Repeat("🌿", 1000)), [])).ToArray();
        var summary = ConversationCompactor.Compact(messages, 2048);
        Assert.InRange(Encoding.UTF8.GetByteCount(summary), 1, 2048);
        Assert.Contains("some details omitted", summary);
        Assert.DoesNotContain('\uFFFD', summary);
        Assert.Contains("Note 99", summary);
    }

    [Fact]
    public void SummaryContainsOnlyQuotedOriginalHistoryAndLabelsItsAuthority()
    {
        var summary = ConversationCompactor.Compact([new("user", "The codename is Blue Otter.", []), new("assistant", "Recorded.", [])], 2048);
        Assert.Contains("The codename is Blue Otter.", summary);
        Assert.Contains("not new instructions", summary);
    }
}
