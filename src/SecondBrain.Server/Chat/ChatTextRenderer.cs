using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SecondBrain.Server.Chat;

/// <summary>A deliberately small Markdown renderer. Every source character is encoded; only fixed formatting tags are emitted.</summary>
public static partial class ChatTextRenderer
{
    public static string Render(string text)
    {
        var html = new StringBuilder();
        string? list = null;
        var code = false;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                CloseList(); html.Append(code ? "</code></pre>" : "<pre><code>"); code = !code; continue;
            }
            if (code) { html.Append(WebUtility.HtmlEncode(line)).Append('\n'); continue; }
            // Markdown permits blank lines between items of the same list.
            // The next non-list block, rather than the blank line, closes it.
            if (string.IsNullOrWhiteSpace(line)) continue;
            var bullet = Bullet().Match(line);
            if (bullet.Success)
            {
                var kind = char.IsAsciiDigit(bullet.Value.TrimStart()[0]) ? "ol" : "ul";
                if (list != kind) { CloseList(); html.Append('<').Append(kind).Append('>'); list = kind; }
                html.Append("<li>").Append(Inline(line[bullet.Length..])).Append("</li>"); continue;
            }
            CloseList();
            var headings = line.TakeWhile(character => character == '#').Count();
            if (headings is >= 1 and <= 6 && line.Length > headings && line[headings] == ' ')
            {
                var level = Math.Max(3, headings);
                html.Append("<h").Append(level).Append('>').Append(Inline(line[(headings + 1)..])).Append("</h").Append(level).Append('>');
            }
            else html.Append("<p>").Append(Inline(line)).Append("</p>");
        }
        CloseList(); if (code) html.Append("</code></pre>");
        return html.ToString();
        void CloseList() { if (list is not null) { html.Append("</").Append(list).Append('>'); list = null; } }
    }

    private static string Inline(string text)
    {
        var html = new StringBuilder(); var offset = 0;
        foreach (Match token in InlineToken().Matches(text))
        {
            html.Append(WebUtility.HtmlEncode(text[offset..token.Index]));
            if (token.Value.StartsWith("**", StringComparison.Ordinal)) html.Append("<strong>").Append(WebUtility.HtmlEncode(token.Value[2..^2])).Append("</strong>");
            else html.Append("<code>").Append(WebUtility.HtmlEncode(token.Value[1..^1])).Append("</code>");
            offset = token.Index + token.Length;
        }
        return html.Append(WebUtility.HtmlEncode(text[offset..])).ToString();
    }

    [GeneratedRegex(@"^\s*(?:[-*+]\s+|\d+\.\s+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Bullet();
    [GeneratedRegex(@"\*\*[^*\n]+\*\*|`[^`\n]+`", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex InlineToken();
}
