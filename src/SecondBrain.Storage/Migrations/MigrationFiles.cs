using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SecondBrain.Storage.Migrations;

internal static class MigrationFiles
{
    internal static void SyncDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        var flags = OperatingSystem.IsMacOS() ? 0x100000 | 0x1000000 | 0x100 : 0x10000 | 0x80000 | 0x20000;
        var descriptor = Open(directory, flags);
        if (descriptor < 0) throw new IOException("Cannot open the migration snapshot directory for fsync.", new Win32Exception(Marshal.GetLastPInvokeError()));
        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        if (Fsync(descriptor) != 0) throw new IOException("Cannot fsync the migration snapshot directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
}
