namespace SecondBrain.Storage.Migrations;

/// <summary>Disk safety margin for migration DDL, WAL, and the retained state snapshot.</summary>
public sealed class MigrationOptions
{
    /// <summary>Minimum available disk after accounting for three times the current stores' on-disk size.</summary>
    public long MinimumFreeBytes { get; init; } = 16 * 1024 * 1024;
}
