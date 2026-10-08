namespace SecondBrain.Core.Authorization;

/// <summary>
/// Independent credential grants; admin does not implicitly grant other scopes,
/// and no scope grants proposal approval (spec §15.4).
/// </summary>
public enum Scope
{
    /// <summary>Read knowledge and caller-owned resources, with implicit retrieval services (§15.4).</summary>
    Read,

    /// <summary>Create, add metadata, and propose other mutations (§15.4, ING-12).</summary>
    Write,

    /// <summary>Start chat, ask, and enrichment within the credential's cap (§15.4).</summary>
    Infer,

    /// <summary>Manage credentials, sources, maintenance, settings, and limits (§15.4).</summary>
    Admin,
}
