using System.Runtime.InteropServices;

namespace SecondBrain.Core.Security;

public static class UnixAccounts
{
    private static readonly object Gate = new();
    public static uint? UserId(string name)
    {
        if (OperatingSystem.IsWindows()) return null;
        // getpwnam uses static storage; serialize reads and copy the id before releasing it.
        lock (Gate)
        {
            var entry = GetPwNam(name);
            if (entry == IntPtr.Zero) return null;
            return unchecked((uint)Marshal.ReadInt32(entry, 2 * IntPtr.Size));
        }
    }
    [DllImport("libc", EntryPoint = "getpwnam")]
    private static extern IntPtr GetPwNam([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
