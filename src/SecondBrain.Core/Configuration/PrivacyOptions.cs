using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>Provider-data and control-plane egress values from spec.md §15.6 and Appendix B.</summary>
public sealed class PrivacyOptions
{
    /// <summary>Whether every role binding, including fallbacks, must be local (§13.5).</summary>
    [YamlMember(Alias = "local_only")]
    public bool LocalOnly { get; set; } = true;

    /// <summary>Exact trusted scheme/host/port endpoints; addresses are pinned by the loader.</summary>
    [YamlMember(Alias = "trusted_services")]
    public List<string> TrustedServices { get; set; } = [];

    /// <summary>Whether the startup and hourly egress canary is enabled (§15.6 SEC-17).</summary>
    [YamlMember(Alias = "egress_canary")]
    public bool EgressCanary { get; set; } = true;

    /// <summary>The public host:port the host rule must block (M0 build plan §4 item 8).</summary>
    [YamlMember(Alias = "canary_target")]
    public string CanaryTarget { get; set; } = "1.1.1.1:443";

    /// <summary>Explicit DNS and Access certificate endpoint allowances, separate from providers.</summary>
    [YamlMember(Alias = "control_plane_egress")]
    public List<string> ControlPlaneEgress { get; set; } = [];
}
