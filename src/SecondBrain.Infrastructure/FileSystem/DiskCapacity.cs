using SecondBrain.Core.Limits;

namespace SecondBrain.Infrastructure.FileSystem;

public sealed class DiskCapacity : IDiskCapacity
{
    public long? AvailableBytes(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var volume = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && (fullPath.Equals(drive.RootDirectory.FullName, StringComparison.Ordinal) || fullPath.StartsWith(drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return volume?.AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
