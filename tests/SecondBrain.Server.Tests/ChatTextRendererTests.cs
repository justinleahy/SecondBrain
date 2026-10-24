using SecondBrain.Server.Chat;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ChatTextRendererTests
{
    [Fact]
    public void OnlyKnownCitationsBecomeLinksToTheirOwnTurn()
    {
        var html = ChatTextRenderer.Render("Evidence [1], other source [2], unknown [99].", "turn-a", [1, 2]);
        Assert.Contains("href=\"#source-turn-a-1\"", html);
        Assert.Contains("href=\"#source-turn-a-2\"", html);
        Assert.Contains("aria-label=\"Go to source 2\"", html);
        Assert.DoesNotContain("#source-turn-a-99", html);
        Assert.Contains("unknown [99]", html);
        Assert.NotEqual(ChatTextRenderer.CitationTargetId("turn-a", 1), ChatTextRenderer.CitationTargetId("turn-b", 1));
    }

    [Fact]
    public void CitationLinksWorkInEmphasisButNotInCodeOrUnverifiedDrafts()
    {
        var html = ChatTextRenderer.Render("**Evidence [1]** and `[1]`\n\n```text\n[1]\n```", "turn-a", [1]);
        Assert.Contains("<strong>Evidence <a", html);
        Assert.Contains("<code>[1]</code>", html);
        Assert.Contains("<pre><code>[1]\n</code></pre>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<a "));
        Assert.DoesNotContain("<a", ChatTextRenderer.Render("Draft [1]"));
        Assert.DoesNotContain("<a", ChatTextRenderer.Render("Unknown [1]", "turn-a", []));
    }

    [Theory]
    [InlineData("turn\" onclick=\"alert(1)")]
    [InlineData("../other")]
    [InlineData("#anchor")]
    public void CitationTargetsRejectUnsafeAttributeValues(string turn)
    {
        Assert.Throws<ArgumentException>(() => ChatTextRenderer.Render("[1]", turn, [1]));
    }

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

    [Fact]
    public void TablesRenderInlineFormattingAlignmentAndBoundedRows()
    {
        var html = ChatTextRenderer.Render("""
            ## Milestones

            | Milestone | Status | Count |
            | --- | :---: | ---: |
            | **M2** | `deployed` | 1895 |
            | M3 | Next |
            | M4 | Planned | 0 | Ignore excess |

            After the table.
            """);
        Assert.Contains("<thead><tr><th>Milestone</th><th class=\"align-center\">Status</th><th class=\"align-right\">Count</th></tr></thead>", html);
        Assert.Contains("<td><strong>M2</strong></td><td class=\"align-center\"><code>deployed</code></td>", html);
        Assert.Contains("<td>M3</td><td class=\"align-center\">Next</td><td class=\"align-right\"></td>", html);
        Assert.DoesNotContain("Ignore excess", html);
        Assert.EndsWith("</tbody></table></div><p>After the table.</p>", html);
    }

    [Fact]
    public void TableCellsPreserveCodePipesAndCitationBoundariesAndEncodeHtml()
    {
        var html = ChatTextRenderer.Render("""
            Source | Content
            --- | ---
            [1] | `a|b` and c\|d <script>alert(1)</script>
            [99] | ![image](https://example.com/pixel)
            """, "turn-a", [1]);
        Assert.Contains("href=\"#source-turn-a-1\"", html);
        Assert.Contains("<code>a|b</code> and c|d &lt;script&gt;", html);
        Assert.Contains("<td>[99]</td>", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("source-turn-a-99", html);
    }

    [Fact]
    public void CodeFencesAndInvalidSeparatorsDoNotBecomeTables()
    {
        var html = ChatTextRenderer.Render("```\nA | B\n--- | ---\n```\nA | B\n-- | text");
        Assert.DoesNotContain("<table", html);
        Assert.Contains("<pre><code>A | B\n--- | ---\n</code></pre>", html);
    }
}
