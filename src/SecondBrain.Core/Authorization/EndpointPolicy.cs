namespace SecondBrain.Core.Authorization;

/// <summary>Additional endpoint requirements shared with future dispatch surfaces.</summary>
public sealed record EndpointPolicy(string Operation, bool SessionOnly = false, bool StepUp = false, bool BrowserFacing = false);

/// <summary>The shared authorization matrix. Unknown operations fail closed.</summary>
public sealed class M0ScopePolicy : IScopePolicy
{
    public ScopeMatrix Matrix { get; } = new(new Dictionary<string, IReadOnlyCollection<Scope>>
    {
        ["documents.create"] = [Scope.Write], ["documents.mutate"] = [Scope.Write],
        ["documents.read"] = [Scope.Read], ["jobs.read"] = [Scope.Read], ["jobs.retry"] = [Scope.Write],
        ["search"] = [Scope.Read], ["query"] = [Scope.Read],
        ["chat.start"] = [Scope.Read, Scope.Infer], ["chat.read"] = [Scope.Read],
        ["chat.delete"] = [Scope.Write],
        ["approvals.read"] = [Scope.Read], ["approvals.resolve"] = [Scope.Read, Scope.Write],
        ["maintenance.rebuild"] = [Scope.Admin], ["documents.purge"] = [Scope.Admin],
        ["keys.list"] = [Scope.Admin], ["keys.create"] = [Scope.Admin], ["keys.revoke"] = [Scope.Admin],
        ["sources.mutate"] = [Scope.Admin], ["sources.scan"] = [Scope.Admin], ["sources.list"] = [Scope.Admin], ["sources.create"] = [Scope.Admin],
        ["providers.list"] = [Scope.Admin], ["providers.test"] = [Scope.Admin], ["diagnostics.read"] = [Scope.Admin],
        ["sessions.list"] = [], ["sessions.revoke"] = [], ["sessions.logout"] = [],
        ["sessions.logout-all"] = [], ["sessions.step-up"] = [], ["session.current"] = [],
    });
    public bool IsAllowed(string operation, IReadOnlySet<Scope> grantedScopes) =>
        Matrix.Requirements.TryGetValue(operation, out var required) && required.IsSubsetOf(grantedScopes);
}
