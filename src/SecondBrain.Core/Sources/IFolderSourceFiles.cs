namespace SecondBrain.Core.Sources;

/// <summary>
/// A present source entry. Unreadable marks an entry that exists but could not be opened, stat'd or hashed;
/// it (and anything beneath it) is neither imported nor treated as absent (U17: absence is disappearance).
/// Rejection names the staging policy a present entry deterministically fails (ING-7); its content is never read.
/// </summary>
public sealed record SourceFileEntry(string RelativePath, long Length, DateTimeOffset ModifiedAt, string? Sha256, bool Unreadable = false, string? Rejection = null)
{
    public static SourceFileEntry UnreadableEntry(string relativePath) => new(relativePath, 0, DateTimeOffset.UnixEpoch, null, Unreadable: true);
}

public interface IFolderSourceFiles
{
    IAsyncEnumerable<string> WatchAsync(FolderSourceConfiguration configuration, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SourceFileEntry> ScanAsync(FolderSourceConfiguration configuration, bool deep, CancellationToken cancellationToken = default);
}
