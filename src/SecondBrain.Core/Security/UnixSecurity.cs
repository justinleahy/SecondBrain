using System.Runtime.InteropServices;

namespace SecondBrain.Core.Security;

public static class UnixSecurity
{
    /// <summary>Set once before services start, so Data Protection and all daemon files inherit 0600.</summary>
    public static void SetPrivateUmask() { if (!OperatingSystem.IsWindows()) _ = Umask(0x3f); }

    public static void SyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var fd = Open(path, 0);
        if (fd < 0) throw new IOException("Cannot open key ring directory for durability sync.");
        try { if (Fsync(fd) != 0) throw new IOException("Cannot sync key ring directory."); }
        finally { _ = Close(fd); }
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
}
