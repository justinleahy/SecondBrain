using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Configuration;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Core.Ingestion;

public sealed record ClassificationContext(string DocumentId, long Revision, string GenerationJson, string ContentHash,
    string Stamp, string Type, string TypeOrigin, string? OverrideType, string? MappedType, string? SourceDefault);
public sealed record ContentClassification(string ContextStamp, string ConfigurationStamp, string GenerationJson,
    string Fingerprint, string? Type, double? Confidence, string Status);
public sealed record ContentTypeChoice(string Type, string Origin, double? Confidence = null);

public interface IClassificationWorkRepository
{
    Task<ClassificationContext?> ReadAsync(string documentId, long revision, CancellationToken token = default);
    Task<ContentClassification?> CachedAsync(ClassificationContext work, string fingerprint, CancellationToken token = default);
    Task<bool> SaveAsync(ClassificationContext work, ContentClassification result, CancellationToken token = default);
}

/// <summary>The single ING-8 precedence matrix, shared by classification and publication.</summary>
public static class ContentTypeSelection
{
    public static IReadOnlyList<string> ModelTypes { get; } = ["note", "article", "transcript", "file"];
    public static ContentTypeChoice? Fixed(ClassificationContext work, ExtractionResult extraction)
    {
        if (work.OverrideType is { } overridden) return Choice(overridden, "user");
        if (work.TypeOrigin is "user" or "door") return Choice(work.Type, work.TypeOrigin);
        if (extraction.DeclaredType is { } declared) return Choice(declared, "frontmatter");
        if (extraction.PhysicalFormat is "vtt" or "srt") return new("transcript", "format");
        if (work.MappedType is { } mapped) return Choice(mapped, "type-map");
        if (work.SourceDefault is { } source) return Choice(source, "source");
        return null;
    }
    public static ContentTypeChoice Select(ClassificationContext work, ExtractionResult extraction, ContentClassification? prediction, SecondBrainOptions options)
    {
        if (Fixed(work, extraction) is { } fixedType) return fixedType;
        if (options.Classification.Llm && prediction is { Status: "classified", Type: { } type, Confidence: { } confidence }
            && prediction.ContextStamp == work.Stamp && prediction.GenerationJson == work.GenerationJson
            && prediction.ConfigurationStamp == ConfigurationStamp(options) && ModelTypes.Contains(type)
            && double.IsFinite(confidence) && confidence >= options.Classification.MinConfidence && confidence <= 1)
            return new(type, "llm", confidence);
        return new(extraction.PhysicalFormat switch
        { "pdf" or "html" => "article", "md" or "markdown" or "txt" or "text" => "note", _ => "file" }, "format-default");
    }
    public static string ConfigurationStamp(SecondBrainOptions options) => Hash(JsonSerializer.Serialize(new
    { options.Classification, options.Models.Enrich, options.Providers, options.Privacy }));
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ContentTypeChoice Choice(string type, string origin) => new(ModelTypes.Contains(type) ? type : "file", origin);
}
