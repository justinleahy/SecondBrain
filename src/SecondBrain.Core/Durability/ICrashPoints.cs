namespace SecondBrain.Core.Durability;

/// <summary>
/// Injectable crash boundaries for publication and filesystem mutation recovery;
/// test implementations may throw at a named point (spec §8, §20; M0 build plan item 6).
/// </summary>
public interface ICrashPoints
{
    /// <summary>Reaches a named durable boundary without changing its semantics (spec §8, §20).</summary>
    ValueTask HitAsync(string point, CancellationToken cancellationToken = default);
}

/// <summary>Production default with no crash injection; see spec §8 and M0 build plan item 6.</summary>
public sealed class NoOpCrashPoints : ICrashPoints
{
    /// <inheritdoc />
    public ValueTask HitAsync(string point, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>Stable publication and journal crash point names; see spec §8 and M0 build plan item 6.</summary>
public static class CrashPointNames
{
    /// <summary>State committed the publishing revision, before index publication (§8).</summary>
    public const string AfterStatePublishing = "after_state_publishing";

    /// <summary>Index publication committed, before state records publication completion (§8).</summary>
    public const string AfterIndexCommit = "after_index_commit";

    /// <summary>Filesystem apply and its resulting hash were recorded, before finalization (§8).</summary>
    public const string AfterApply = "after_apply";

    /// <summary>Immediately before the journal's finalize state transaction (§8).</summary>
    public const string BeforeFinalize = "before_finalize";
}
