using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>M0 source-root settings from spec.md §6, §7.2 FLD-1 and Appendix B.</summary>
public sealed class SourcesOptions
{
    /// <summary>Admin-configured absolute roots under which sources may be registered.</summary>
    [YamlMember(Alias = "allowed_roots")]
    public List<string> AllowedRoots { get; set; } = [];

    /// <summary>The separately owned incoming tree outside the private data root (§6).</summary>
    [YamlMember(Alias = "incoming_root")]
    public string IncomingRoot { get; set; } = "/srv/secondbrain-incoming";
}
