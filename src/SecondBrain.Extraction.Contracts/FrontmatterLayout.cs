namespace SecondBrain.Extraction.Contracts;

/// <summary>UTF-16 offsets into the exact extracted Markdown text, inspected inside the sandbox.</summary>
public sealed record FrontmatterLayout(int HeaderEnd, int? GeneratedStart = null, int GeneratedLength = 0, bool Writable = true)
{
    public const string Key = "secondbrain_enrichment";
    public const string StartMarker = "# secondbrain-enrichment:v1\n";
    public const string EndMarker = "# /secondbrain-enrichment:v1\n";
}
