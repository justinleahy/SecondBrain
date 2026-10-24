using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SecondBrain.Server.Chat;

/// <summary>A deliberately small Markdown renderer. Every source character is encoded; only fixed formatting tags are emitted.</summary>
public static partial class ChatTextRenderer
{
    public static string Render(string text, string? turnId = null, IReadOnlyCollection<int>? sourceNumbers = null)
    {
        var html = new StringBuilder();
        var sources = (sourceNumbers ?? []).ToHashSet();
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
                html.Append("<li>").Append(Inline(line[bullet.Length..], turnId, sources)).Append("</li>"); continue;
            }
            CloseList();
            var headings = line.TakeWhile(character => character == '#').Count();
            if (headings is >= 1 and <= 6 && line.Length > headings && line[headings] == ' ')
            {
                var level = Math.Max(3, headings);
                html.Append("<h").Append(level).Append('>').Append(Inline(line[(headings + 1)..], turnId, sources)).Append("</h").Append(level).Append('>');
            }
            else html.Append("<p>").Append(Inline(line, turnId, sources)).Append("</p>");
        }
        CloseList(); if (code) html.Append("</code></pre>");
        return html.ToString();
        void CloseList() { if (list is not null) { html.Append("</").Append(list).Append('>'); list = null; } }
    }

    public static string CitationTargetId(string turnId, int number)
    {
        if (string.IsNullOrEmpty(turnId) || turnId.Length > 128 || turnId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid citation turn ID.", nameof(turnId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        return "source-" + turnId + "-" + number.ToString(CultureInfo.InvariantCulture);
    }

    private static string Inline(string text, string? turnId, IReadOnlySet<int> sources)
    {
        var html = new StringBuilder(); var offset = 0;
        foreach (Match token in InlineToken().Matches(text))
        {
            AppendText(html, text[offset..token.Index], turnId, sources);
            if (token.Value.StartsWith("**", StringComparison.Ordinal))
            {
                html.Append("<strong>"); AppendText(html, token.Value[2..^2], turnId, sources); html.Append("</strong>");
            }
            else html.Append("<code>").Append(WebUtility.HtmlEncode(token.Value[1..^1])).Append("</code>");
            offset = token.Index + token.Length;
        }
        AppendText(html, text[offset..], turnId, sources);
        return html.ToString();
    }

    private static void AppendText(StringBuilder html, string text, string? turnId, IReadOnlySet<int> sources)
    {
        if (turnId is null || sources.Count == 0) { html.Append(WebUtility.HtmlEncode(text)); return; }
        var offset = 0;
        foreach (Match marker in CitationMarker().Matches(text))
        {
            html.Append(WebUtility.HtmlEncode(text[offset..marker.Index]));
            if (int.TryParse(marker.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && sources.Contains(number))
                html.Append("<a class=\"citation-link\" data-citation-link data-enhance-nav=\"false\" href=\"#").Append(CitationTargetId(turnId, number))
                    .Append("\" aria-label=\"Go to source ").Append(number.ToString(CultureInfo.InvariantCulture)).Append("\">")
                    .Append(WebUtility.HtmlEncode(marker.Value)).Append("</a>");
            else html.Append(WebUtility.HtmlEncode(marker.Value));
            offset = marker.Index + marker.Length;
        }
        html.Append(WebUtility.HtmlEncode(text[offset..]));
    }

    [GeneratedRegex(@"^\s*(?:[-*+]\s+|\d+\.\s+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Bullet();
    [GeneratedRegex(@"\*\*[^*\n]+\*\*|`[^`\n]+`", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex InlineToken();
    [GeneratedRegex(@"\[(\d+)\]", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CitationMarker();
}
