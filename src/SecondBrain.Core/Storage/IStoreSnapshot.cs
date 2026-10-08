namespace SecondBrain.Core.Storage;

/// <summary>Online snapshots of a store, used under the exclusive data-root lock during migrations.</summary>
public interface IStoreSnapshot
{
    /// <summary>Creates a consistent SQLite online-backup snapshot at a new destination.</summary>
    ValueTask CreateSnapshotAsync(string destination, CancellationToken cancellationToken = default);

    /// <summary>Restores a previously verified SQLite snapshot while serialized with the store writer.</summary>
    ValueTask RestoreSnapshotAsync(string snapshotPath, CancellationToken cancellationToken = default);
}
