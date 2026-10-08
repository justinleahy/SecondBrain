using YamlDotNet.Serialization;

namespace SecondBrain.Core.Providers;

/// <summary>
/// Limits resolved for a concrete model, or nullable configuration overrides;
/// unspecified values remain unknown until catalog resolution (spec §13.3, Appendix B).
/// </summary>
public sealed record ModelLimits
{
    /// <summary>Gets the model's total context token limit (§13.3).</summary>
    [YamlMember(Alias = "context_tokens")]
    public int? ContextTokens { get; init; }

    /// <summary>Gets the maximum response token count (§13.3).</summary>
    [YamlMember(Alias = "max_output_tokens")]
    public int? MaxOutputTokens { get; init; }

    /// <summary>Gets the embedding vector dimensions (§13.3).</summary>
    [YamlMember(Alias = "embed_dimensions")]
    public int? EmbedDimensions { get; init; }

    /// <summary>Gets the maximum tokens in one embedding input (§13.3).</summary>
    [YamlMember(Alias = "embed_max_input_tokens")]
    public int? EmbedMaxInputTokens { get; init; }

    /// <summary>Gets the maximum inputs in one embedding batch (§13.3).</summary>
    [YamlMember(Alias = "embed_batch_max")]
    public int? EmbedBatchMax { get; init; }
}
