using System.Text;
using System.Text.Json;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Core.Ingestion;

/// <summary>Applies only a bounded generated block; all authored text outside that block stays byte-for-byte.</summary>
public static class EnrichmentFrontmatter
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] Apply(byte[] original, ExtractionResult inspected, DocumentEnrichment enrichment)
    {
        EnrichmentPolicy.Validate(enrichment);
        var text = Utf8.GetString(original);
        var bom = text.StartsWith('\ufeff'); if (bom) text = text[1..];
        if (original.Length > 10 * 1024 * 1024 || inspected.Status != "extracted" || inspected.PhysicalFormat != "markdown"
            || inspected.LosslessText != text || inspected.Frontmatter is not { Writable: true } layout)
            throw new InvalidDataException("frontmatter-writeback-unavailable");
        var json = JsonSerializer.Serialize(new { summary = enrichment.Summary, tags = enrichment.Tags });
        var block = FrontmatterLayout.StartMarker + FrontmatterLayout.Key + ": " + json + "\n" + FrontmatterLayout.EndMarker;
        string rewritten;
        if (layout.HeaderEnd == 0)
        {
            if (layout.GeneratedStart is not null || text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal))
                throw new InvalidDataException("frontmatter-layout");
            rewritten = "---\n" + block + "---\n" + text;
        }
        else
        {
            var opening = text.StartsWith("---\r\n", StringComparison.Ordinal) ? 5 : text.StartsWith("---\n", StringComparison.Ordinal) ? 4 : 0;
            var end = layout.HeaderEnd;
            if (opening == 0 || end < opening || end > Math.Min(text.Length - 3, opening + 65536) || text.AsSpan(end, 3) is not "---"
                || text[end - 1] != '\n' || end + 3 < text.Length && text[end + 3] is not ('\r' or '\n'))
                throw new InvalidDataException("frontmatter-layout");
            var offset = layout.GeneratedStart ?? end; var length = layout.GeneratedLength;
            if (offset < opening || length < 0 || (long)offset + length > end || layout.GeneratedStart is null && length != 0)
                throw new InvalidDataException("frontmatter-layout");
            if (layout.GeneratedStart is not null) ValidateOwnedBlock(text.Substring(offset, length));
            rewritten = text[..offset] + block + text[(offset + length)..];
            if (Utf8.GetByteCount(rewritten[..(end - length + block.Length)]) > 65536)
                throw new InvalidDataException("frontmatter-writeback-bounds");
        }
        return Utf8.GetBytes((bom ? "\ufeff" : "") + rewritten);
    }
    private static void ValidateOwnedBlock(string block)
    {
        var prefix = FrontmatterLayout.StartMarker + FrontmatterLayout.Key + ": ";
        if (block.Length > 32768 || !block.StartsWith(prefix, StringComparison.Ordinal) || !block.EndsWith("\n" + FrontmatterLayout.EndMarker, StringComparison.Ordinal))
            throw new InvalidDataException("frontmatter-owned-block");
        var json = block[prefix.Length..^(FrontmatterLayout.EndMarker.Length + 1)];
        if (json.Contains('\n') || json.Contains('\r')) throw new InvalidDataException("frontmatter-owned-block");
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        if (parsed.RootElement.ValueKind != JsonValueKind.Object || parsed.RootElement.EnumerateObject().Count() != 2
            || !parsed.RootElement.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String
            || !parsed.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() > 5
            || tags.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("frontmatter-owned-block");
    }
}
