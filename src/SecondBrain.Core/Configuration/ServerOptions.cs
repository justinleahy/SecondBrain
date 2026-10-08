using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>Listener, Host, Origin and trusted proxy values from spec.md §15.2 and Appendix B.</summary>
public sealed class ServerOptions
{
    /// <summary>Explicit loopback or private-network listener definitions; none are implied.</summary>
    [YamlMember(Alias = "listeners")]
    public List<ListenerOptions> Listeners { get; set; } = [];

    /// <summary>Allowed request Host values.</summary>
    [YamlMember(Alias = "hosts")]
    public List<string> Hosts { get; set; } = [];

    /// <summary>Exact permitted browser origins, including scheme and any non-default port.</summary>
    [YamlMember(Alias = "origins")]
    public List<string> Origins { get; set; } = [];

    /// <summary>Proxy addresses allowed to provide forwarded headers.</summary>
    [YamlMember(Alias = "trusted_proxies")]
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>The public-origin Access gate, when a Cloudflare public hostname is configured.</summary>
    [YamlMember(Alias = "cloudflare_access")]
    public CloudflareAccessOptions? CloudflareAccess { get; set; }
}

/// <summary>A Kestrel listener from spec.md §15.2 SEC-1/SEC-2 and Appendix B.</summary>
public sealed class ListenerOptions
{
    /// <summary>The configured transport scheme: http or https.</summary>
    [YamlMember(Alias = "scheme")]
    public string Scheme { get; set; } = "http";

    /// <summary>The explicit loopback or private interface address.</summary>
    [YamlMember(Alias = "bind")]
    public string Bind { get; set; } = "127.0.0.1";

    /// <summary>The listener port; zero denotes an omitted value requiring validation.</summary>
    [YamlMember(Alias = "port")]
    public int Port { get; set; }

    /// <summary>The certificate selector or file, such as tailscale; required for HTTPS.</summary>
    [YamlMember(Alias = "certificate")]
    public string? Certificate { get; set; }
}

/// <summary>Cloudflare Access assertion settings from spec.md §15.2 SEC-3 and Appendix B.</summary>
public sealed class CloudflareAccessOptions
{
    /// <summary>The Host value on which every request must carry an Access assertion.</summary>
    [YamlMember(Alias = "public_hostname")]
    public string PublicHostname { get; set; } = string.Empty;

    /// <summary>The configured Access team domain that publishes the signing keys.</summary>
    [YamlMember(Alias = "team_domain")]
    public string TeamDomain { get; set; } = string.Empty;

    /// <summary>The application's expected Access token audience.</summary>
    [YamlMember(Alias = "audience")]
    public string Audience { get; set; } = string.Empty;
}
