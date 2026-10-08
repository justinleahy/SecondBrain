using SecondBrain.Core.Providers;

namespace SecondBrain.Core.Privacy;

/// <summary>
/// Evaluates provider-data egress at startup, reload and immediately before each send;
/// see spec §15.6 (SEC-16–SEC-18). Control-plane egress is a separate allowance.
/// </summary>
public interface IPrivacyPolicy
{
    /// <summary>Gets the latest egress canary outcome reported by readiness (§15.6, SEC-17).</summary>
    CanaryState CanaryState { get; }

    /// <summary>
    /// Evaluates the current policy for the role and concrete binding; a denied request
    /// must fail explicitly rather than silently fall back (§15.6, SEC-16).
    /// </summary>
    PrivacyDecision Evaluate(ModelRole role, IProviderBinding binding);
}

/// <summary>The public TCP canary outcome; see spec §15.6 (SEC-17) and M0 build plan §5.</summary>
public enum CanaryState
{
    /// <summary>No check has completed, so the host egress backstop is unverified (§15.6).</summary>
    Unknown,

    /// <summary>The connection could not complete, including timeout: the canary is blocked (§15.6).</summary>
    Blocked,

    /// <summary>The connection succeeded: provider calls must fail under local_only (§15.6).</summary>
    Reachable,
}

/// <summary>A provider-data egress decision; see spec §15.6.</summary>
/// <param name="Allowed">Whether this provider call may proceed under the current policy.</param>
/// <param name="ProblemType">A stable problem type URI when denied, or null when allowed.</param>
/// <param name="Reason">An optional redacted explanation of the decision.</param>
public sealed record PrivacyDecision(bool Allowed, string? ProblemType = null, string? Reason = null);
