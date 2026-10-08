using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>The instance's temporal interpretation settings from spec.md §5.5 and Appendix B.</summary>
public sealed class TimeOptions
{
    /// <summary>The time zone identifier; defaults to the server's local zone (§5.5).</summary>
    [YamlMember(Alias = "zone")]
    public string Zone { get; set; } = TimeZoneInfo.Local.Id;

    /// <summary>The first day used for relative week boundaries; defaults to monday.</summary>
    [YamlMember(Alias = "week_start")]
    public string WeekStart { get; set; } = "monday";
}
