using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>Session and step-up defaults from spec.md §15.3 SEC-8/SEC-10 and Appendix B.</summary>
public sealed class AuthOptions
{
    /// <summary>Browser session idle expiry, in hours.</summary>
    [YamlMember(Alias = "session_idle_hours")]
    public int SessionIdleHours { get; set; } = 12;

    /// <summary>Browser session absolute lifetime, in days.</summary>
    [YamlMember(Alias = "session_absolute_days")]
    public int SessionAbsoluteDays { get; set; } = 30;

    /// <summary>Paired CLI session idle expiry, in hours.</summary>
    [YamlMember(Alias = "cli_session_idle_hours")]
    public int CliSessionIdleHours { get; set; } = 8;

    /// <summary>Paired CLI session absolute lifetime, in hours.</summary>
    [YamlMember(Alias = "cli_session_absolute_hours")]
    public int CliSessionAbsoluteHours { get; set; } = 24;

    /// <summary>The validity window for a fresh re-authentication, in minutes.</summary>
    [YamlMember(Alias = "step_up_minutes")]
    public int StepUpMinutes { get; set; } = 10;

    /// <summary>Whether passkeys may be offered; secure-origin enforcement remains mandatory.</summary>
    [YamlMember(Alias = "passkeys")]
    public bool Passkeys { get; set; } = true;
}
