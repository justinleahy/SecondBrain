namespace SecondBrain.Core.Privacy;

/// <summary>Privacy contribution shared by /ready and brain doctor.</summary>
public interface IPrivacyReadiness
{
    PrivacyReadiness GetReadiness();
}

/// <summary>A snapshot that contains no endpoint secrets or provider content.</summary>
public sealed record PrivacyReadiness(
    bool IsReady,
    bool LocalOnly,
    bool CanaryEnabled,
    CanaryState CanaryState,
    DateTimeOffset? LastCheckedAt,
    string? ProblemType,
    string? Detail);

/// <summary>A canary configuration generation; results from superseded targets cannot publish.</summary>
public sealed record EgressCanaryConfiguration(bool Enabled, string Target, long Generation);
