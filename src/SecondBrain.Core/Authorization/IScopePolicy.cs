namespace SecondBrain.Core.Authorization;

/// <summary>
/// The shared REST/MCP scope policy; session, step-up, ownership, generation and
/// epoch checks remain additional authorization requirements (spec §§7.4, 15.3–15.4).
/// </summary>
public interface IScopePolicy
{
    /// <summary>Gets the policy's immutable scope requirements (spec §15.4).</summary>
    ScopeMatrix Matrix { get; }

    /// <summary>
    /// Checks that a valid credential holds every scope required for the logical
    /// operation; unregistered operations must be denied (spec §§7.4, 15.4).
    /// A true result alone never permits a key to approve a proposal.
    /// </summary>
    bool IsAllowed(string operation, IReadOnlySet<Scope> grantedScopes);
}
