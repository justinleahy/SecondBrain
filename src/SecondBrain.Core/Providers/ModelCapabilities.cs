namespace SecondBrain.Core.Providers;

/// <summary>
/// Truthful capabilities of one concrete model, rather than its adapter;
/// absent capabilities use the degradation rules in spec §13.3.
/// </summary>
public sealed record ModelCapabilities
{
    /// <summary>Gets whether incremental response streaming is supported (§13.3).</summary>
    public bool Streaming { get; init; }

    /// <summary>Gets whether native tool calling is supported; required for chat (§13.3).</summary>
    public bool Tools { get; init; }

    /// <summary>Gets whether the provider supplies span-level citations (§13.3).</summary>
    public bool NativeCitations { get; init; }

    /// <summary>Gets whether reasoning preferences can be mapped to provider controls (§13.3).</summary>
    public bool ReasoningControl { get; init; }

    /// <summary>Gets the optional neutral-preference to provider-control mapping (§13.3).</summary>
    public IReadOnlyDictionary<string, string>? ReasoningMapping { get; init; }

    /// <summary>Gets whether prompt caching is supported (§13.3).</summary>
    public bool PromptCaching { get; init; }

    /// <summary>Gets whether schema-constrained structured output is supported (§13.3).</summary>
    public bool StructuredOutput { get; init; }

    /// <summary>Gets whether an optional batch implementation is available (§13.3).</summary>
    public bool Batch { get; init; }
}
