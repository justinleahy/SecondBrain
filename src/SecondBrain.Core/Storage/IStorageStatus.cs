namespace SecondBrain.Core.Storage;

/// <summary>Startup readiness after migrations and durability recovery have completed.</summary>
public interface IStorageStatus
{
    bool Ready { get; }
    MigrationReport? LastMigration { get; }
}
