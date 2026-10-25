using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SecondBrain.Core.Ingestion;

/// <summary>Versioned, deterministic normalization with a reversible UTF-16 offset map.</summary>
public static class TextProcessor
{
    public const int Generation = 1;
    public static TextProjection Process(string id, string title, string text, string[]? tags = null,
        string? path = null, int maximumTokens = 1024, int targetTokens = 512, int overlapTokens = 64)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumTokens < 1 || targetTokens < 1 || targetTokens > maximumTokens || overlapTokens < 0 || overlapTokens >= targetTokens)
            throw new ArgumentException("Invalid chunk bounds.");
        var protectedBlocks = ProtectedBlocks(text);
        var (normalized, offsets) = Normalize(text, protectedBlocks);
        // UTF-8 byte count is a conservative bound for byte-fallback tokenizers. A UTF-16
        // code unit is not: many multilingual characters require multiple tokens.
        var headings = Regex.Matches(text, @"(?m)^ {0,3}(#{1,6})[ \t]+([^\r\n]+)")
            .Where(match => !protectedBlocks.Any(block => block.Start <= match.Index && match.Index < block.End))
            .Select(match => new Heading(match.Index, match.Groups[1].Length, match.Groups[2].Value.Trim().TrimEnd('#').TrimEnd())).ToArray();
        var chunks = new List<TextChunk>();
        var start = 0;
        while (start < text.Length)
        {
            if (chunks.Count >= 5000) throw new IngestionConflictException("chunk-count-bound");
            var context = ContextAt(title, headings, start, maximumTokens / 4);
            var bodyMaximum = maximumTokens - Encoding.UTF8.GetByteCount(context) - 1;
            var bodyTarget = Math.Min(targetTokens, bodyMaximum);
            var end = Utf8End(text, start, bodyTarget);
            if (end == start) end = Utf8End(text, start, bodyMaximum);
            if (end == start) throw new IngestionConflictException("embedding-input-bound");
            var nextHeading = headings.FirstOrDefault(heading => heading.Offset > start);
            if (nextHeading is not null && nextHeading.Offset < end) end = nextHeading.Offset;
            if (end < text.Length)
            {
                var paragraph = text.LastIndexOf("\n\n", end - 1, end - start, StringComparison.Ordinal);
                if (paragraph > start + (end - start) / 2) end = paragraph + 2;
            }
            var crossing = protectedBlocks.FirstOrDefault(block => block.Start < end && end < block.End);
            if (crossing is not null)
            {
                if (Encoding.UTF8.GetByteCount(text.AsSpan(start, crossing.End - start)) <= bodyMaximum) end = crossing.End;
                else if (crossing.Start > start) end = crossing.Start;
            }
            var body = text[start..end];
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context + "\n" + body)));
            chunks.Add(new TextChunk(id + ":" + chunks.Count, chunks.Count, context, body, start, end,
                Encoding.UTF8.GetByteCount(context) + Encoding.UTF8.GetByteCount(body) + 1, hash));
            if (end == text.Length) break;
            // Do not overlap backwards across a heading or into an indivisible code/table block.
            if (headings.Any(heading => heading.Offset == end) || protectedBlocks.Any(block => block.Start == end || block.End == end)) start = end;
            else
            {
                var overlap = Math.Min(overlapTokens, Math.Max(0, bodyTarget - 1));
                var next = end;
                var bytes = 0;
                while (next > start)
                {
                    var previous = next - 1;
                    if (previous > start && char.IsLowSurrogate(text[previous]) && char.IsHighSurrogate(text[previous - 1])) previous--;
                    var cost = Encoding.UTF8.GetByteCount(text.AsSpan(previous, next - previous));
                    if (bytes + cost > overlap) break;
                    bytes += cost; next = previous;
                }
                start = next > start ? next : end;
            }
        }
        if (chunks.Count == 0)
        {
            var context = ContextAt(title, headings, 0, Math.Max(0, maximumTokens - 1));
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context + "\n")));
            chunks.Add(new TextChunk(id + ":0", 0, context, "", 0, 0, Encoding.UTF8.GetByteCount(context) + 1, hash));
        }
        return new TextProjection(text, normalized, offsets, chunks,
            (tags ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), path, Links: Links(text));
    }

    private static IReadOnlyList<string> Links(string text)
    {
        var values = Regex.Matches(text, @"\[\[([^\]\r\n]+)\]\]|\[[^\]\r\n]*\]\(([^)\r\n]+)\)", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Where(value => value.Length <= 4096 && !value.Any(char.IsControl)).Distinct(StringComparer.Ordinal).Take(201).ToArray();
        if (values.Length > 200) throw new IngestionConflictException("link-count-bound");
        return values;
    }

    /// <summary>
    /// The first search-text index whose lossless offset is at or after <paramref name="losslessOffset"/>. The offset
    /// map is non-decreasing and ends with a sentinel equal to the lossless length, so every lossless offset resolves.
    /// </summary>
    public static int SearchIndex(IReadOnlyList<int> offsetMap, int losslessOffset)
    {
        ArgumentNullException.ThrowIfNull(offsetMap);
        int low = 0, high = offsetMap.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (offsetMap[middle] < losslessOffset) low = middle + 1; else high = middle;
        }
        return low;
    }

    /// <summary>The search-text view of a chunk's lossless range: what the lexical index tokenizes (spec §9 Normalize).</summary>
    public static string SearchSlice(string searchText, IReadOnlyList<int> offsetMap, int losslessStart, int losslessEnd) =>
        searchText[SearchIndex(offsetMap, losslessStart)..SearchIndex(offsetMap, losslessEnd)];

    /// <summary>Maps a range of a chunk's search slice back to offsets within the chunk's lossless text.</summary>
    public static (int Start, int End) ToLosslessRange(IReadOnlyList<int> offsetMap, int losslessStart, int losslessEnd, int sliceStart, int sliceEnd)
    {
        var first = SearchIndex(offsetMap, losslessStart);
        var start = Math.Clamp(offsetMap[first + sliceStart], losslessStart, losslessEnd);
        var end = Math.Clamp(offsetMap[first + sliceEnd], start, losslessEnd);
        return (start - losslessStart, end - losslessStart);
    }

    private static (string Text, int[] Offsets) Normalize(string text, List<Block> protectedBlocks)
    {
        var filtered = new StringBuilder(text.Length);
        var sourceOffsets = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF') continue;
            filtered.Append(text[i]); sourceOffsets.Add(i);
        }
        var normalized = new StringBuilder(filtered.Length);
        var offsets = new List<int>(filtered.Length + 1);
        var elements = StringInfo.GetTextElementEnumerator(filtered.ToString());
        var whitespace = false;
        var blockIndex = 0;
        while (elements.MoveNext())
        {
            var original = elements.GetTextElement();
            var composed = original.Normalize(NormalizationForm.FormC);
            var unchanged = original == composed;
            for (var j = 0; j < composed.Length; j++)
            {
                var source = sourceOffsets[elements.ElementIndex + (unchanged ? j : 0)];
                while (blockIndex < protectedBlocks.Count && protectedBlocks[blockIndex].End <= source) blockIndex++;
                var preserve = blockIndex < protectedBlocks.Count && protectedBlocks[blockIndex].Start <= source;
                var character = composed[j];
                if (!preserve && char.IsWhiteSpace(character))
                {
                    if (whitespace) continue;
                    character = ' '; whitespace = true;
                }
                else whitespace = false;
                normalized.Append(character); offsets.Add(source);
            }
        }
        offsets.Add(text.Length);
        return (normalized.ToString(), offsets.ToArray());
    }

    private static int Utf8End(string text, int start, int budget)
    {
        var end = start;
        var bytes = 0;
        while (end < text.Length)
        {
            var length = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
            var cost = Encoding.UTF8.GetByteCount(text.AsSpan(end, length));
            if (bytes + cost > budget) break;
            bytes += cost; end += length;
        }
        return end;
    }

    private static string ContextAt(string title, Heading[] headings, int offset, int budget)
    {
        var hierarchy = new List<Heading>();
        foreach (var heading in headings.TakeWhile(heading => heading.Offset <= offset))
        {
            hierarchy.RemoveAll(previous => previous.Level >= heading.Level);
            hierarchy.Add(heading);
        }
        var context = string.Join(" > ", new[] { title }.Concat(hierarchy.Select(heading => heading.Text)));
        return context[..Utf8End(context, 0, budget)];
    }

    private static List<Block> ProtectedBlocks(string text)
    {
        var lines = Regex.Matches(text, @"[^\n]*(?:\n|$)").Where(match => match.Length > 0).ToArray();
        var result = new List<Block>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Value.TrimStart();
            var marker = line.StartsWith("```", StringComparison.Ordinal) ? "```" : line.StartsWith("~~~", StringComparison.Ordinal) ? "~~~" : null;
            if (marker is not null)
            {
                var start = lines[i].Index;
                while (++i < lines.Length && !lines[i].Value.TrimStart().StartsWith(marker, StringComparison.Ordinal)) { }
                var end = i < lines.Length ? lines[i].Index + lines[i].Length : text.Length;
                result.Add(new Block(start, end));
            }
            else if (line.Contains('|'))
            {
                var start = lines[i].Index;
                while (i + 1 < lines.Length && lines[i + 1].Value.Contains('|')) i++;
                result.Add(new Block(start, lines[i].Index + lines[i].Length));
            }
        }
        return result;
    }

    private sealed record Heading(int Offset, int Level, string Text);
    private sealed record Block(int Start, int End);
}
