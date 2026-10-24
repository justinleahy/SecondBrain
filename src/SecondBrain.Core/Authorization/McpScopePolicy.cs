namespace SecondBrain.Core.Authorization;

/// <summary>Authoritative tool requirements, independent of discovery hints and transport versions.</summary>
public static class McpScopePolicy
{
    public static IReadOnlyDictionary<string, Scope[]> Tools { get; } = new Dictionary<string, Scope[]>(StringComparer.Ordinal)
    {
        ["brain_search"] = [Scope.Read], ["brain_query"] = [Scope.Read], ["brain_get"] = [Scope.Read],
        ["brain_list_recent"] = [Scope.Read], ["brain_tags"] = [Scope.Read],
        ["brain_ask"] = [Scope.Read, Scope.Infer], ["brain_remember"] = [Scope.Write], ["brain_update"] = [Scope.Write]
    };
}
