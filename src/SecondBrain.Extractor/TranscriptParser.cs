using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Extractor;

/// <summary>Bounded WebVTT/SubRip parsing. Cue markup is text; it never loads resources.</summary>
internal static class TranscriptParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Timing = new(@"^(?<from>(?:\d{2,}:)?\d{2}:\d{2}[.,]\d{3})\s+-->\s+(?<to>(?:\d{2,}:)?\d{2}:\d{2}[.,]\d{3})(?:[ \t]+.*)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
    private static readonly Regex Voices = new(@"<v(?:\.[^\s>]+)*[ \t]+([^>]+)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
    private static readonly Regex Markup = new(@"</?(?:b|i|u|font|c|v|lang|ruby|rt)(?:[.\s][^>]*)?>|<(?:\d{2,}:)?\d{2}:\d{2}\.\d{3}>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);

    internal static ExtractionResult Parse(string source, bool webVtt, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(source) > DocumentParser.MaximumTextBytes) return Failed("decoded_bytes");
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var position = 0;
        if (webVtt)
        {
            if (lines.Length == 0 || !Regex.IsMatch(lines[0], @"^WEBVTT(?:[ \t].*)?$", RegexOptions.CultureInvariant, RegexTimeout)) return Malformed();
            // Header metadata ends at the first blank line.
            while (position < lines.Length && lines[position].Length != 0) position++;
        }
        var text = new StringBuilder();
        var segments = new List<ExtractionSegment>();
        while (position < lines.Length)
        {
            token.ThrowIfCancellationRequested();
            while (position < lines.Length && string.IsNullOrWhiteSpace(lines[position])) position++;
            if (position == lines.Length) break;
            var first = position;
            while (position < lines.Length && !string.IsNullOrWhiteSpace(lines[position])) position++;
            var end = position;
            if (webVtt && (lines[first] is "STYLE" or "REGION" || lines[first] == "NOTE" || lines[first].StartsWith("NOTE ", StringComparison.Ordinal) || lines[first].StartsWith("NOTE\t", StringComparison.Ordinal))) continue;
            var timingIndex = lines[first].Contains("-->", StringComparison.Ordinal) ? first : first + 1;
            if (timingIndex >= end) return Malformed();
            var timing = Timing.Match(lines[timingIndex]);
            if (!timing.Success || !TryTime(timing.Groups["from"].Value, out var start) || !TryTime(timing.Groups["to"].Value, out var finish) || finish < start) return Malformed();
            if (!webVtt && (first == timingIndex || !long.TryParse(lines[first], NumberStyles.None, CultureInfo.InvariantCulture, out _))) return Malformed();
            var body = string.Join('\n', lines[(timingIndex + 1)..end]);
            // A voice change within a cue produces separate segments at the same time range.
            var boundaries = new List<(int Index, int ContentStart, string Speaker)>();
            foreach (Match voice in Voices.Matches(body))
            {
                token.ThrowIfCancellationRequested();
                if (boundaries.Count + segments.Count >= TranscriptSegments.MaximumCount) return Failed("transcript_segments");
                boundaries.Add((voice.Index, voice.Index + voice.Length, WebUtility.HtmlDecode(voice.Groups[1].Value).Trim()));
            }
            if (boundaries.Count == 0)
            {
                if (!Append(body, null, start, finish)) return Failed("transcript_segments");
            }
            else
            {
                if (boundaries[0].Index > 0 && !Append(body[..boundaries[0].Index], null, start, finish)) return Failed("transcript_segments");
                for (var i = 0; i < boundaries.Count; i++)
                {
                    var part = boundaries[i];
                    if (part.Speaker.Length > 256 || part.Speaker.Any(char.IsControl)) return Failed("transcript_speaker");
                    if (!Append(body[part.ContentStart..(i + 1 == boundaries.Count ? body.Length : boundaries[i + 1].Index)], part.Speaker, start, finish)) return Failed("transcript_segments");
                }
            }
        }
        return new("extracted", webVtt ? "vtt" : "srt", webVtt ? "text/vtt" : "application/x-subrip", text.ToString(), Segments: segments);

        bool Append(string cue, string? speaker, long start, long end)
        {
            var plain = WebUtility.HtmlDecode(Markup.Replace(cue, "")).Trim();
            if (plain.Length == 0) return true;
            if (segments.Count >= TranscriptSegments.MaximumCount) return false;
            if (text.Length != 0) text.Append('\n');
            var offset = text.Length;
            text.Append(plain);
            segments.Add(new(segments.Count, string.IsNullOrWhiteSpace(speaker) ? null : speaker, start, end, offset, plain.Length));
            return true;
        }
    }

    private static bool TryTime(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Replace(',', '.').Split(':', '.');
        if (parts.Length is not (3 or 4)) return false;
        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        var hours = parts.Length == 4 ? numbers[0] : 0;
        var minutes = numbers[^3]; var seconds = numbers[^2];
        if (hours > 1000000 || minutes >= 60 || seconds >= 60) return false;
        milliseconds = ((hours * 60 + minutes) * 60 + seconds) * 1000 + numbers[^1];
        return true;
    }
    private static ExtractionResult Malformed() => new("failed", "unknown", "application/octet-stream", "", Error: "malformed_transcript");
    private static ExtractionResult Failed(string bound) => new("skipped", "unknown", "application/octet-stream", "", Error: "bound_exceeded", Bound: bound);
}
