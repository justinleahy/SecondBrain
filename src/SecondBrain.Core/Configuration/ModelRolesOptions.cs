using SecondBrain.Core.Providers;
using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>The four provider-neutral role bindings from spec.md §13.1 and Appendix B.</summary>
public sealed class ModelRolesOptions
{
    /// <summary>The assistant/chat binding, supplied by configuration.</summary>
    [YamlMember(Alias = "chat")]
    public ModelBindingOptions? Chat { get; set; }

    /// <summary>The classification and enrichment binding, supplied by configuration.</summary>
    [YamlMember(Alias = "enrich")]
    public ModelBindingOptions? Enrich { get; set; }

    /// <summary>The chunk and query embedding binding, supplied by configuration.</summary>
    [YamlMember(Alias = "embed")]
    public ModelBindingOptions? Embed { get; set; }

    /// <summary>An optional reranking binding; unset by default (§13.1).</summary>
    [YamlMember(Alias = "rerank")]
    public ModelBindingOptions? Rerank { get; set; }
}

/// <summary>A concrete provider/model role binding from spec.md §13.1–§13.3 and Appendix B.</summary>
public sealed class ModelBindingOptions
{
    /// <summary>The name of an entry in the root providers dictionary.</summary>
    [YamlMember(Alias = "provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>The model identifier selected by the user; no model is implied.</summary>
    [YamlMember(Alias = "model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>An optional low, medium or high reasoning preference, mapped by the adapter.</summary>
    [YamlMember(Alias = "reasoning")]
    public string? Reasoning { get; set; }

    /// <summary>The optional chat fallback, eligible only before a committed write (§13.1).</summary>
    [YamlMember(Alias = "fallback")]
    public ModelBindingOptions? Fallback { get; set; }

    /// <summary>Appendix B's embedding dimension selection, when explicitly configured.</summary>
    [YamlMember(Alias = "dimensions")]
    public int? Dimensions { get; set; }

    /// <summary>Nullable overrides of the adapter's model limit catalog (§13.3).</summary>
    [YamlMember(Alias = "limits")]
    public ModelLimits Limits { get; set; } = new();

    /// <summary>Explicit capability declarations for a model absent from the adapter catalog (§13.3).</summary>
    [YamlMember(Alias = "capabilities")]
    public ModelCapabilityOptions Capabilities { get; set; } = new();
}
