using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>A named provider-neutral connection from spec.md §13, §14 and Appendix B.</summary>
/// <remarks>
/// The item-1 contract accepts kind/endpoint as well as Appendix B's adapter/base_url spellings.
/// The loader must reject contradictory spellings before deserialization; these properties only
/// hold configuration values. Trust never bypasses privacy.trusted_services (§13.5).
/// </remarks>
public sealed class ProviderOptions
{
    /// <summary>The adapter kind; provisioning chooses it without a default vendor.</summary>
    [YamlMember(Alias = "kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>The endpoint text, left unparsed until loading and validation.</summary>
    [YamlMember(Alias = "endpoint")]
    public string? Endpoint { get; set; }

    /// <summary>Appendix B's adapter spelling of <see cref="Kind"/>.</summary>
    [YamlMember(Alias = "adapter")]
    public string Adapter
    {
        get => Kind;
        set => Kind = value;
    }

    /// <summary>Appendix B's base_url spelling of <see cref="Endpoint"/>.</summary>
    [YamlMember(Alias = "base_url")]
    public string? BaseUrl
    {
        get => Endpoint;
        set => Endpoint = value;
    }

    /// <summary>An unresolved ${VAR} secret reference; never a literal API key (§14).</summary>
    [YamlMember(Alias = "api_key")]
    public string? ApiKeyReference { get; set; }

    /// <summary>Explicit provider trust intent; locality is independently evaluated (§13.5).</summary>
    [YamlMember(Alias = "trusted")]
    public bool Trusted { get; set; }
}
