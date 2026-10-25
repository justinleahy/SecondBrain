namespace SecondBrain.Extraction.Contracts;

/// <summary>A cue's speaker and relative time range, anchored in the extracted UTF-16 text.</summary>
public sealed record ExtractionSegment(int Ordinal, string? Speaker, long StartMs, long EndMs, int Start, int Length);

public static class TranscriptSegments
{
    public const int MaximumCount = 20000;

    public static bool IsValid(string text, IReadOnlyList<ExtractionSegment>? segments)
    {
        if (segments is null) return true;
        if (segments.Count > MaximumCount) return false;
        var previousEnd = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (segment is null || segment.Ordinal != index || segment.StartMs < 0 || segment.EndMs < segment.StartMs ||
                segment.Start < previousEnd || segment.Length < 1 || (long)segment.Start + segment.Length > text.Length ||
                segment.Speaker is { Length: > 256 } || segment.Speaker?.Any(char.IsControl) == true) return false;
            if (segment.Start > 0 && char.IsLowSurrogate(text[segment.Start]) && char.IsHighSurrogate(text[segment.Start - 1])) return false;
            previousEnd = segment.Start + segment.Length;
            if (previousEnd < text.Length && char.IsHighSurrogate(text[previousEnd - 1]) && char.IsLowSurrogate(text[previousEnd])) return false;
        }
        return true;
    }
}
