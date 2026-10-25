using System.Globalization;
using System.Text;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Core.Ingestion;

/// <summary>Groups consecutive speaker cues and retains exact segment locators on every bounded chunk.</summary>
public static class TranscriptTextProcessor
{
    public static TextProjection Process(string id, string title, string text, IReadOnlyList<ExtractionSegment> segments,
        ChunkingProfile profile, string[]? tags = null, string? path = null)
    {
        if (!TranscriptSegments.IsValid(text, segments)) throw new ArgumentException("Invalid transcript segments.", nameof(segments));
        var projection = profile.Process(id, title, text, tags, path);
        if (segments.Count == 0) return projection with { Segments = segments };
        var chunks = new List<TextChunk>();
        for (var first = 0; first < segments.Count;)
        {
            var last = first;
            var head = segments[first];
            while (last + 1 < segments.Count && segments[last + 1].Speaker == head.Speaker &&
                Encoding.UTF8.GetByteCount(text.AsSpan(head.Start, segments[last + 1].Start + segments[last + 1].Length - head.Start)) <= Math.Min(400, profile.TargetTokens)) last++;
            var tail = segments[last];
            var context = $"{Timestamp(head.StartMs)}–{Timestamp(segments.Skip(first).Take(last - first + 1).Max(segment => segment.EndMs))} · {head.Speaker ?? "Unknown speaker"} · {title}";
            var parts = profile.Process(id, context, text.Substring(head.Start, tail.Start + tail.Length - head.Start));
            foreach (var chunk in parts.Chunks)
            {
                if (chunks.Count >= 5000) throw new IngestionConflictException("chunk-count-bound");
                var start = head.Start + chunk.StartOffset;
                var end = head.Start + chunk.EndOffset;
                var covered = segments.Skip(first).Take(last - first + 1).Where(segment => segment.Start < end && segment.Start + segment.Length > start).ToArray();
                if (covered.Length == 0) continue;
                chunks.Add(chunk with { Id = id + ":" + chunks.Count, Ordinal = chunks.Count, StartOffset = start, EndOffset = end,
                    Transcript = new(covered[0].Ordinal, covered[^1].Ordinal, head.Speaker, covered.Min(segment => segment.StartMs), covered.Max(segment => segment.EndMs)) });
            }
            first = last + 1;
        }
        return projection with { Chunks = chunks, Segments = segments };
    }

    public static string Timestamp(long milliseconds) => string.Create(CultureInfo.InvariantCulture,
        $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");
}
