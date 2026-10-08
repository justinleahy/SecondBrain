namespace SecondBrain.Core.Storage;

/// <summary>Durable, idempotent migrations of state and index; the caller must hold the data-root lock.</summary>
public interface IMigrationRunner
{
    /// <summary>Migrates both stores before any requests are served and reports interrupted work recovered.</summary>
    ValueTask<MigrationReport> MigrateAsync(CancellationToken cancellationToken = default);
}

/// <summary>The resulting store versions and explicit startup recovery report.</summary>
public sealed record MigrationReport(int StateVersion, int IndexVersion, IReadOnlyList<MigrationRecovery> Recoveries);

/// <summary>Recovery performed for one interrupted migration.</summary>
public sealed record MigrationRecovery(string Store, int Version, MigrationRecoveryMethod Method, string Detail);

/// <summary>The durable recovery route selected at startup.</summary>
public enum MigrationRecoveryMethod
{
    /// <summary>The applying migration was safe to re-run in a transaction.</summary>
    ReranIdempotently,
    /// <summary>The verified pre-migration state snapshot was restored before re-running.</summary>
    RestoredSnapshot
}

/// <summary>Migration-specific crash boundaries, separate from the frozen publication/journal contract.</summary>
public static class MigrationCrashPointNames
{
    /// <summary>State migration committed; index migration has not started (M0 G9).</summary>
    public const string AfterStateMigration = "after_state_migration";
    /// <summary>The durable state migration marker is applying, before DDL.</summary>
    public const string AfterStateApplying = "after_state_migration_applying";
    /// <summary>The durable index migration marker is applying, before DDL.</summary>
    public const string AfterIndexApplying = "after_index_migration_applying";
}
