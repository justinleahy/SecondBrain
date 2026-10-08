using System.Collections.Frozen;

namespace SecondBrain.Core.Authorization;

/// <summary>
/// An immutable snapshot of required scopes keyed by logical operation, shared by
/// REST endpoint policy and the future MCP dispatcher (spec §§7.4, 15.4).
/// Every scope in an entry is required; an empty entry still requires a valid
/// credential. Anonymous endpoints are handled outside this matrix.
/// </summary>
public sealed class ScopeMatrix
{
    /// <summary>
    /// Copies the operation keys and scope collections to prevent mutation after
    /// registration; operation names are ordinal and case-sensitive (spec §15.4).
    /// </summary>
    public ScopeMatrix(IEnumerable<KeyValuePair<string, IReadOnlyCollection<Scope>>> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        Requirements = requirements.ToFrozenDictionary(
            entry => entry.Key,
            entry => (IReadOnlySet<Scope>)entry.Value.ToFrozenSet(),
            StringComparer.Ordinal);
    }

    /// <summary>Gets the immutable operation-to-required-scopes map (spec §§7.4, 15.4).</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<Scope>> Requirements { get; }
}
