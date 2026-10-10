using System.Text.Json;

namespace SecondBrain.Extraction.Contracts;

public sealed record ExtractionResult(
    string Status, string PhysicalFormat, string MimeType, string LosslessText,
    string? Title = null, string? DeclaredType = null,
    IReadOnlyDictionary<string, JsonElement>? Properties = null,
    IReadOnlyList<string>? Tags = null, IReadOnlyList<string>? Links = null,
    IReadOnlyList<ExtractionPage>? Pages = null, string? Error = null, string? Bound = null,
    IReadOnlyList<string>? Aliases = null, string? DeclaredCreatedAt = null, string? DeclaredUpdatedAt = null);
