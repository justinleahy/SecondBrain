using SecondBrain.Core.Limits;
using SecondBrain.Infrastructure.Security;

namespace SecondBrain.Infrastructure.FileSystem;

/// <summary>Free space of the filesystem physically mounted at a path, such as a separately mounted data root.</summary>
public sealed class DiskCapacity : IDiskCapacity
{
    // macOS getmntinfo returns a process-wide buffer; concurrent enumeration can observe corrupted mount names.
    private static readonly Lock MountTable = new();

    /// <summary>Returns null when no mounted filesystem can be identified; callers must fail closed.</summary>
    public long? AvailableBytes(string path)
    {
        try
        {
            DriveInfo[] drives;
            lock (MountTable)
            {
                drives = DriveInfo.GetDrives().Where(drive => drive.IsReady).ToArray();
            }
            var mount = SelectMount(path, drives.Select(drive => drive.RootDirectory.FullName));
            // A later entry for the same mount point shadows the earlier one, as in the kernel's mount table.
            return mount is null ? null : drives.Last(drive => drive.RootDirectory.FullName == mount).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Chooses the longest mount root containing the path after resolving symlinks in its existing ancestors,
    /// so a symlinked data root selects its target's filesystem. Matching respects path-segment boundaries.
    /// </summary>
    public static string? SelectMount(string path, IEnumerable<string> mountRoots)
    {
        ArgumentNullException.ThrowIfNull(mountRoots);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A disk capacity path must be absolute.", nameof(path));
        }

        var physical = UnixPath.Canonicalize(path);
        return mountRoots
            .Where(root => Path.IsPathFullyQualified(root) && SourcePathValidator.AtOrBelow(physical, root))
            .OrderByDescending(root => Path.TrimEndingDirectorySeparator(root).Length)
            .FirstOrDefault();
    }
}
