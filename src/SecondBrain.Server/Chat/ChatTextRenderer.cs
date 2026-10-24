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
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                CloseList(); html.Append(code ? "</code></pre>" : "<pre><code>"); code = !code; continue;
            }
            if (code) { html.Append(WebUtility.HtmlEncode(line)).Append('\n'); continue; }
            if (index + 1 < lines.Length && line.Contains('|') && TableColumns(line) is var headers &&
                TableColumns(lines[index + 1]) is var separators && headers.Length == separators.Length &&
                separators.All(cell => TableSeparator().IsMatch(cell)))
            {
                CloseList();
                html.Append("<div class=\"markdown-table\"><table><thead><tr>");
                WriteCells(headers, "th");
                html.Append("</tr></thead><tbody>");
                index++;
                while (index + 1 < lines.Length && lines[index + 1].Contains('|') &&
                    !lines[index + 1].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    html.Append("<tr>");
                    WriteCells(TableColumns(lines[++index]), "td");
                    html.Append("</tr>");
                }
                html.Append("</tbody></table></div>");
                continue;

                void WriteCells(string[] cells, string tag)
                {
                    for (var column = 0; column < headers.Length; column++)
                    {
                        var separator = separators[column];
                        var alignment = separator.EndsWith(':') ? separator.StartsWith(':') ? "align-center" : "align-right" : "";
                        html.Append('<').Append(tag);
                        if (alignment.Length > 0) html.Append(" class=\"").Append(alignment).Append('"');
                        html.Append('>').Append(Inline(column < cells.Length ? cells[column] : "", turnId, sources));
                        html.Append("</").Append(tag).Append('>');
                    }
                }
            }
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

    private static string[] TableColumns(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var code = false;
        line = line.Trim();
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\\' && index + 1 < line.Length && line[index + 1] == '|')
            { cell.Append('|'); index++; continue; }
            if (character == '`') code = !code;
            if (character == '|' && !code) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(character);
        }
        cells.Add(cell.ToString().Trim());
        if (cells.Count > 1 && cells[0].Length == 0) cells.RemoveAt(0);
        if (cells.Count > 1 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
        return cells.ToArray();
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
    [GeneratedRegex(@"^:?-{3,}:?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TableSeparator();
}
