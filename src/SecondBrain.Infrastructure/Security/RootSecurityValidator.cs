using System.Runtime.InteropServices;

namespace SecondBrain.Infrastructure.Security;

public sealed record RootSecurityCheck(string Path, bool Passed, string Message);

/// <summary>Checks ownership, Unix modes, symlinks and separation of the two provisioned roots.</summary>
public static class RootSecurityValidator
{
    public static uint CurrentUid() => OperatingSystem.IsWindows() ? 0 : GetUid();
    public static uint CurrentGid() => OperatingSystem.IsWindows() ? 0 : GetGid();

    public static void ValidateDaemonIdentities(uint daemonUid, uint syncUid)
    {
        if (daemonUid == 0) throw new UnauthorizedAccessException("The daemon must run as a non-root user.");
        if (syncUid == 0 || syncUid == daemonUid)
            throw new UnauthorizedAccessException("The sync user must have its own non-root identity, separate from the daemon.");
    }

    public static IReadOnlyList<RootSecurityCheck> Validate(string dataRoot, string incomingRoot, uint daemonUid, uint daemonGid, uint syncUid)
    {
        var checks = new List<RootSecurityCheck>();
        Check(dataRoot, daemonUid, null, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, checks);
        Check(incomingRoot, syncUid, daemonGid, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, checks);
        var data = UnixPath.Canonicalize(dataRoot);
        var incoming = UnixPath.Canonicalize(incomingRoot);
        if (IsWithin(data, incoming) || IsWithin(incoming, data))
            checks.Add(new(dataRoot, false, "Data and incoming roots must be separate trees."));
        return checks;
    }

    public static void ValidateOrThrow(string dataRoot, string incomingRoot, uint daemonUid, uint daemonGid, uint syncUid)
    {
        var failed = Validate(dataRoot, incomingRoot, daemonUid, daemonGid, syncUid).Where(check => !check.Passed).ToArray();
        if (failed.Length > 0) throw new UnauthorizedAccessException(string.Join(" ", failed.Select(check => check.Message)));
    }

    private static bool IsWithin(string path, string root) => path == root || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void Check(string path, uint uid, uint? gid, UnixFileMode mode, List<RootSecurityCheck> checks)
    {
        if (!Directory.Exists(path)) { checks.Add(new(path, false, "Provisioned root does not exist.")); return; }
        if (new DirectoryInfo(path).LinkTarget is not null || UnixPath.Canonicalize(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            checks.Add(new(path, false, "Provisioned roots must not traverse symlinks."));
        if (OperatingSystem.IsWindows()) { checks.Add(new(path, false, "Unix root ownership and mode validation is unavailable on Windows.")); return; }
        var actualMode = File.GetUnixFileMode(path);
        checks.Add(new(path, actualMode == mode, actualMode == mode ? "Root mode is correct." : "Root mode is incorrect; run brain init --provision."));
        var (actualUid, actualGid) = Ownership(path);
        checks.Add(new(path, actualUid == uid && (!gid.HasValue || actualGid == gid), actualUid == uid && (!gid.HasValue || actualGid == gid) ? "Root ownership is correct." : "Root ownership is incorrect; run brain init --provision."));
    }

    public static (uint Uid, uint Gid) Ownership(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix ownership is unavailable.");
        // stat layouts differ: these fixed offsets are stable for 64-bit Linux and Darwin.
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (Stat(path, buffer) != 0) throw new IOException("Cannot stat provisioned root.");
            var uidOffset = OperatingSystem.IsMacOS() ? 16 : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 24 : 28;
            return (unchecked((uint)Marshal.ReadInt32(buffer, uidOffset)), unchecked((uint)Marshal.ReadInt32(buffer, uidOffset + 4)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "getgid")]
    private static extern uint GetGid();
}
