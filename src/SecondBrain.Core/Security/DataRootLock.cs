using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SecondBrain.Core.Security;

public sealed class DataRootLockedException : IOException
{
    public DataRootLockedException() : base("The data root is already locked by another SecondBrain process. Stop that instance before continuing.") { }
}

/// <summary>Retains an exclusive non-blocking flock until disposal; the lock file is never removed.</summary>
public sealed class DataRootLock : IDisposable
{
    private readonly SafeFileHandle handle;
    private DataRootLock(SafeFileHandle handle) => this.handle = handle;

    public static DataRootLock Acquire(string dataRoot)
    {
        var path = Path.Combine(dataRoot, ".lock");
        if (!Directory.Exists(dataRoot)) throw new IOException("Data root does not exist; provision it first.");
        if (OperatingSystem.IsWindows())
        {
            try { return new DataRootLock(File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException) { throw new DataRootLockedException(); }
        }
        // Let the runtime handle variadic open(mode) on Darwin arm64. CreateNew is atomic,
        // then O_NOFOLLOW opens the same permanent lock path without following a link.
        try
        {
            using var created = new FileStream(path, new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        }
        catch (IOException) when (File.Exists(path)) { }
        var flags = 2 | (OperatingSystem.IsMacOS() ? 0x100 | 0x1000000 : 0x20000 | 0x80000);
        var fd = Open(path, flags);
        if (fd < 0) throw new IOException("Cannot open the data-root lock.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var handle = new SafeFileHandle((IntPtr)fd, true);
        if (Flock(fd, 2 | 4) == 0) return new DataRootLock(handle);
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error is 11 or 35) throw new DataRootLockedException();
        throw new IOException("Cannot acquire the data-root lock.", new Win32Exception(error));
    }

    public void Dispose() => handle.Dispose();

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);
}
