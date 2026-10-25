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

    /// <summary>Whether the endpoint accepts the standard reasoning_effort control for this model.</summary>
    [YamlMember(Alias = "reasoning_control")]
    public bool? ReasoningControl { get; init; }

    /// <summary>Whether the model supports a provider-native stable-prefix cache breakpoint.</summary>
    [YamlMember(Alias = "prompt_caching")]
    public bool? PromptCaching { get; init; }

    /// <summary>Whether the model is approved for the adapter's native batch API.</summary>
    [YamlMember(Alias = "batch")]
    public bool? Batch { get; init; }

    /// <summary>Whether the model supports provider-native citations to supplied inline documents.</summary>
    [YamlMember(Alias = "native_citations")]
    public bool? NativeCitations { get; init; }
}
