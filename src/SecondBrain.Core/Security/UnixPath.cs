using System.Runtime.InteropServices;

namespace SecondBrain.Core.Security;

/// <summary>Canonical paths, including symlinked parent directories, for configuration containment.</summary>
public static class UnixPath
{
    public static string Canonicalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows()) return new DirectoryInfo(full).ResolveLinkTarget(true)?.FullName ?? full;
        var current = full;
        var suffix = new Stack<string>();
        while (!Directory.Exists(current) && !File.Exists(current))
        {
            suffix.Push(Path.GetFileName(current));
            current = Path.GetDirectoryName(current) ?? throw new IOException("Path has no existing ancestor.");
        }
        var ptr = RealPath(current, IntPtr.Zero);
        if (ptr == IntPtr.Zero) throw new IOException("Path cannot be canonicalized.");
        try
        {
            var result = Marshal.PtrToStringUTF8(ptr) ?? throw new IOException("Path cannot be canonicalized.");
            foreach (var part in suffix) result = Path.Combine(result, part);
            return Path.TrimEndingDirectorySeparator(result);
        }
        finally { Free(ptr); }
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
