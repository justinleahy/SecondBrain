using SecondBrain.Server.Chat;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ChatTextRendererTests
{
    [Theory]
    [InlineData("1. First\n\n2. Second\n\n3. Third")]
    [InlineData("1. First\n\n1. Second\n\n1. Third")]
    public void BlankLinesDoNotRestartANumberedList(string markdown)
    {
        Assert.Equal("<ol><li>First</li><li>Second</li><li>Third</li></ol>", ChatTextRenderer.Render(markdown));
    }

    [Fact]
    public void AParagraphOrDifferentListKindClosesThePreviousList()
    {
        Assert.Equal("<ol><li>First</li><li>Second</li></ol><p>After.</p><ul><li>Bullet</li></ul><ol><li>New list</li></ol>",
            ChatTextRenderer.Render("1. First\n\n2. Second\n\nAfter.\n\n- Bullet\n\n1. New list"));
    }

    [Fact]
    public void FormatsListsEmphasisAndCodeWithoutTrustingModelHtml()
    {
        var html = ChatTextRenderer.Render("- **Codename:** Blue Otter [1]\n- `<script>alert(1)</script>`\n\n```html\n<img src=x onerror=alert(1)>\n```");
        Assert.Contains("<ul><li><strong>Codename:</strong> Blue Otter [1]</li>", html);
        Assert.Contains("&lt;script&gt;", html); Assert.Contains("&lt;img", html);
        Assert.DoesNotContain("<script", html); Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void LinksAndImagesRemainTextAndCannotTriggerRemoteLoadsOrScriptUrls()
    {
        var html = ChatTextRenderer.Render("[click](javascript:alert(1)) ![track](https://example.com/pixel) <iframe src='x'></iframe>");
        Assert.DoesNotContain("<a", html); Assert.DoesNotContain("<img", html); Assert.DoesNotContain("<iframe", html);
        Assert.Contains("&lt;iframe", html);
    }
}
