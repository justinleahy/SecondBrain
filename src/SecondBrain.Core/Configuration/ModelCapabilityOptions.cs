using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>
/// Reviewed capability declarations for a model the adapter catalog does not know (§13.3).
/// Unset values stay unsupported; nothing is inferred from the endpoint, and a declaration
/// that contradicts a catalog entry is rejected.
/// </summary>
public sealed record ModelCapabilityOptions
{
    /// <summary>Whether the served model supports native tool calling; required for chat.</summary>
    [YamlMember(Alias = "tools")]
    public bool? Tools { get; init; }

    /// <summary>Whether the served model supports incremental response streaming.</summary>
    [YamlMember(Alias = "streaming")]
    public bool? Streaming { get; init; }

    /// <summary>Whether the served model supports schema-constrained structured output.</summary>
    [YamlMember(Alias = "structured_output")]
    public bool? StructuredOutput { get; init; }
}
