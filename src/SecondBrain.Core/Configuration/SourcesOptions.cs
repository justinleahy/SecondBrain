using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>M0 source-root settings from spec.md §6, §7.2 FLD-1 and Appendix B.</summary>
public sealed class SourcesOptions
{
    /// <summary>Exact HTTPS origins authorized solely for registered calendar downloads.</summary>
    [YamlMember(Alias = "calendar_allowed_origins")]
    public List<string> CalendarAllowedOrigins { get; set; } = [];
    [YamlMember(Alias = "calendar_subscriptions")]
    public List<CalendarSubscriptionOptions> CalendarSubscriptions { get; set; } = [];
    /// <summary>Admin-configured absolute roots under which sources may be registered.</summary>
    [YamlMember(Alias = "allowed_roots")]
    public List<string> AllowedRoots { get; set; } = [];

    /// <summary>The separately owned incoming tree outside the private data root (§6).</summary>
    [YamlMember(Alias = "incoming_root")]
    public string IncomingRoot { get; set; } = "/srv/secondbrain-incoming";
}

/// <summary>Only a reference is retained here; the resolved bearer link never belongs to configuration snapshots.</summary>
public sealed class CalendarSubscriptionOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    [YamlMember(Alias = "url")]
    public string UrlReference { get; set; } = "";
    [YamlMember(Alias = "poll_interval_s")]
    public int PollIntervalSeconds { get; set; } = 3600;
    public bool Enabled { get; set; } = true;

    public static bool IsReference(string? value) => value is { Length: >= 4 } && value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}') &&
        (char.IsAsciiLetter(value[2]) || value[2] == '_') && value[3..^1].All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    public static bool IsId(string? value) => value is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
