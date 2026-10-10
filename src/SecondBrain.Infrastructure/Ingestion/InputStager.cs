using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Infrastructure.Ingestion;

/// <summary>Linux descriptor-relative source containment and private, unlinked immutable staging.</summary>
public sealed class InputStager(string stagingDirectory) : IInputStager, IStagingRecovery, IDisposable
{
    public const long MaximumInputBytes = 50L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, SafeFileHandle> roots = new(StringComparer.Ordinal);

    public async ValueTask<StagedInput> StageAsync(Stream input, string filename, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var name = Sanitize(filename); var path = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            long length = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                var buffer = new byte[64 * 1024]; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    length += read;
                    if (length > MaximumInputBytes) throw new InvalidDataException("input_bytes");
                    hash.AppendData(buffer.AsSpan(0, read)); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken); output.Flush(flushToDisk: true);
            }
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            var stream = File.OpenRead(path);
            File.Delete(path); // The read-only descriptor is the sole remaining staging reference.
            return new PrivateStage(stream, name, Convert.ToHexStringLower(hash.GetHashAndReset()), length);
        }
        finally { File.Delete(path); }
    }

    public ValueTask<int> CleanStaleAsync(CancellationToken cancellationToken = default)
    {
        EnsureLinux();
        SafeFileHandle directory;
        try { directory = OpenRoot(Path.GetFullPath(stagingDirectory)); }
        catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) { return ValueTask.FromResult(0); }
        using (directory)
        {
            var removed = 0; var owner = NativeGetEuid();
            foreach (var candidate in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{directory.DangerousGetHandle()}"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(candidate);
                // StageAsync names every file with Guid "N"; nothing else in the directory is ours to remove.
                if (name.Length != 32 || !name.All(char.IsAsciiHexDigitLower)) continue;
                SafeFileHandle file;
                try { file = OpenAt(directory, name, 0x800); } // O_NOFOLLOW (added by OpenAt) rejects symlinks.
                catch (IOException) { continue; }
                using (file)
                {
                    if (!IsOwnedRegularSingleLink(file, owner)) continue;
                    if (NativeUnlinkAt(checked((int)directory.DangerousGetHandle()), name, 0) == 0) removed++;
                }
            }
            return ValueTask.FromResult(removed);
        }
    }

    private static bool IsOwnedRegularSingleLink(SafeFileHandle file, uint owner)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (NativeStat(file, buffer) != 0) return false;
            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var mode = unchecked((uint)Marshal.ReadInt32(buffer, arm ? 16 : 24));
            var links = arm ? unchecked((uint)Marshal.ReadInt32(buffer, 20)) : unchecked((ulong)Marshal.ReadInt64(buffer, 16));
            var uid = unchecked((uint)Marshal.ReadInt32(buffer, arm ? 24 : 28));
            return (mode & 0xf000) == 0x8000 && links == 1 && uid == owner;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void RequireOwnedDirectory(SafeFileHandle directory, bool privateOnly)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (NativeStat(directory, buffer) != 0) throw new IOException("directory_stat_failed");
            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var mode = unchecked((uint)Marshal.ReadInt32(buffer, arm ? 16 : 24));
            var uid = unchecked((uint)Marshal.ReadInt32(buffer, arm ? 24 : 28));
            if ((mode & 0xf000) != 0x4000 || uid != NativeGetEuid() || (mode & 2) != 0 || privateOnly && (mode & 63) != 0)
                throw new InvalidDataException("inbox_directory_ownership");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public async ValueTask<StagedInput> OpenSourceAsync(string sourceRoot, string relativePath, CancellationToken cancellationToken = default)
    {
        EnsureLinux();
        if (!Path.IsPathFullyQualified(sourceRoot)) throw new ArgumentException("Source root must be absolute.", nameof(sourceRoot));
        var root = RetainRoot(sourceRoot);
        // A legal but unstageable name is this entry's deterministic rejection, never a scan-wide failure.
        if (NameRejection(relativePath) is { } invalid) throw new InvalidDataException(invalid);
        using var source = OpenChild(root, relativePath);
        RequireRegularSingleLink(source);
        await using var input = new FileStream(source, FileAccess.Read);
        return await StageAsync(input, Path.GetFileName(relativePath), cancellationToken);
    }

    internal SafeFileHandle RetainRoot(string sourceRoot)
    {
        EnsureLinux();
        lock (roots)
        {
            if (!roots.TryGetValue(sourceRoot, out var root)) { root = OpenRoot(sourceRoot); roots[sourceRoot] = root; }
            return root;
        }
    }

    internal void RetainOwnedRoot(string sourceRoot, SafeFileHandle opened)
    {
        // Duplicate the validated inode through its descriptor, never by reopening
        // a pathname that the sync identity can replace between these operations.
        lock (roots)
        {
            RequireOwnedDirectory(opened, privateOnly: false);
            if (roots.TryGetValue(sourceRoot, out var retained)) RequireOwnedDirectory(retained, privateOnly: false);
            else roots[sourceRoot] = OpenAt(opened, ".", DirectoryFlag);
        }
    }

    internal static void EnsurePrivateDirectory(string path)
    {
        EnsureLinux();
        using var root = OpenAt(-100, "/", DirectoryFlag);
        SafeFileHandle? current = null;
        try
        {
            foreach (var component in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (component is "." or "..") throw new InvalidDataException("managed_path");
                var parent = current ?? root;
                var fd = checked((int)parent.DangerousGetHandle());
                if (NativeMkdirAt(fd, component, 448) != 0 && Marshal.GetLastPInvokeError() != 17) throw new IOException("managed_directory_failed");
                var next = OpenAt(parent, component, DirectoryFlag); current?.Dispose(); current = next;
            }
            if (current is null || NativeChmod(current, 448) != 0) throw new IOException("managed_directory_permissions");
        }
        finally { current?.Dispose(); }
    }

    private static SafeFileHandle OpenRoot(string path)
    {
        var root = OpenAt(-100, "/", DirectoryFlag);
        try
        {
            foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (component is "." or "..") throw new InvalidDataException("source_path");
                var next = OpenAt(root, component, DirectoryFlag); root.Dispose(); root = next;
            }
            return root;
        }
        catch { root.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenChild(SafeFileHandle root, string path)
    {
        if (Path.IsPathRooted(path)) throw new InvalidDataException("source_path");
        var parts = path.Split('/');
        if (parts.Length == 0 || parts.Any(part => part is "" or "." or "..")) throw new InvalidDataException("source_path");
        SafeFileHandle? parent = null;
        try
        {
            for (var index = 0; index < parts.Length - 1; index++)
            {
                var next = OpenAt(parent ?? root, parts[index], DirectoryFlag); parent?.Dispose(); parent = next;
            }
            return OpenAt(parent ?? root, parts[^1], 0x800); // O_NONBLOCK avoids blocking on a FIFO before fstat.
        }
        finally { parent?.Dispose(); }
    }

    private const int DirectoryFlag = 0x10000;
    internal static SafeFileHandle OpenAt(SafeFileHandle parent, string name, int flags)
    {
        var retained = false;
        try { parent.DangerousAddRef(ref retained); return OpenAt(checked((int)parent.DangerousGetHandle()), name, flags); }
        finally { if (retained) parent.DangerousRelease(); }
    }
    private static SafeFileHandle OpenAt(int parent, string name, int flags)
    {
        var fd = NativeOpenAt(parent, name, flags | 0x20000 | 0x80000); // O_NOFOLLOW | O_CLOEXEC, read only.
        if (fd < 0) throw new IOException("source_open_failed", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        return new SafeFileHandle(fd, ownsHandle: true);
    }
    internal static void RequireRegularSingleLink(SafeFileHandle file)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (NativeStat(file, buffer) != 0) throw new IOException("source_stat_failed");
            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var mode = unchecked((uint)Marshal.ReadInt32(buffer, arm ? 16 : 24));
            var links = arm ? unchecked((uint)Marshal.ReadInt32(buffer, 20)) : unchecked((ulong)Marshal.ReadInt64(buffer, 16));
            var length = Marshal.ReadInt64(buffer, 48); // st_size on both x64 and arm64.
            var reason = (mode & 0xf000) != 0x8000 ? "source_not_regular" : links != 1 ? "source_multiple_links" : length > MaximumInputBytes ? "input_bytes" : null;
            // The stat evidence lets a recorded rejection stand until the entry itself changes.
            if (reason is not null) throw SourceEntryPolicy.Rejected(reason, length, StatModifiedAt(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    /// <summary>The file's own st_mtim from an fstat buffer (same offset on x64 and arm64), not a procfs link's time.</summary>
    internal static DateTimeOffset StatModifiedAt(IntPtr stat) =>
        DateTimeOffset.FromUnixTimeSeconds(Marshal.ReadInt64(stat, 88)).AddTicks(Marshal.ReadInt64(stat, 96) / 100);
    internal static string? NameRejection(string relativePath)
    {
        try { Sanitize(Path.GetFileName(relativePath)); return null; }
        catch (ArgumentException) { return "source_name"; }
    }
    private static string Sanitize(string filename)
    {
        var result = Path.GetFileName(filename.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(result) || result is "." or ".." || result.Length > 255 || result.Any(char.IsControl)) throw new ArgumentException("Invalid filename.", nameof(filename));
        return result;
    }
    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) throw new PlatformNotSupportedException("Source staging requires Linux x64/arm64.");
    }
    public void Dispose() { foreach (var root in roots.Values) root.Dispose(); roots.Clear(); }
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int NativeMkdirAt(int directory, string name, uint mode);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)] private static extern int NativeChmod(SafeFileHandle file, uint mode);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int NativeOpenAt(int directory, string name, int flags);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int NativeStat(SafeFileHandle file, IntPtr stat);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] private static extern int NativeUnlinkAt(int directory, string name, int flags);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint NativeGetEuid();
    private sealed class PrivateStage(FileStream content, string filename, string sha256, long length) : StagedInput
    {
        public override Stream Content => content;
        public override string Filename => filename;
        public override string Sha256 => sha256;
        public override long Length => length;
        public override ValueTask DisposeAsync() => content.DisposeAsync();
    }
}
