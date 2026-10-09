using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace SecondBrain.Storage.Durability;

/// <summary>Descriptor-relative writes keep every journal open beneath a retained managed root.</summary>
internal sealed class ManagedMutationFiles
{
    private readonly string _root;

    internal ManagedMutationFiles(string dataRoot)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Mutation durability requires macOS or Linux filesystem primitives.");
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Mutation metadata inspection requires the x64 or arm64 stat ABI.");
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _root = Path.GetFullPath(dataRoot);
    }

    internal string NormalizeDestination(string destination)
    {
        if (!Path.IsPathFullyQualified(destination))
            throw new ArgumentException("Mutation destinations must be absolute.", nameof(destination));
        var full = Path.GetFullPath(destination);
        var relative = Path.GetRelativePath(_root, full);
        if (relative == "." || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException("Mutation destination must be beneath the managed data root.", nameof(destination));
        return full;
    }

    internal DestinationLease Open(string destination, string mutationId)
    {
        var full = NormalizeDestination(destination);
        var segments = Path.GetRelativePath(_root, full).Split(Path.DirectorySeparatorChar);
        var fd = Native.Open(_root, DirectoryFlags);
        if (fd < 0) Throw("open managed root");
        var parent = new SafeFileHandle((nint)fd, ownsHandle: true);
        try
        {
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var next = Native.OpenAt(Descriptor(parent), segments[i], DirectoryFlags, 0);
                if (next < 0) Throw("open managed parent without following symlinks");
                var previous = parent;
                parent = new SafeFileHandle((nint)next, ownsHandle: true);
                previous.Dispose();
            }
            var suffix = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mutationId)));
            return new DestinationLease(parent, full, segments[^1], $".sb-{suffix}.tmp");
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    private static int DirectoryFlags => OperatingSystem.IsMacOS()
        ? 0x100000 | 0x1000000 | 0x100
        : 0x10000 | 0x80000 | 0x20000;
    private static int NoFollowFlags => OperatingSystem.IsMacOS() ? 0x1000000 | 0x100 : 0x80000 | 0x20000;
    private static int CreateFlags => NoFollowFlags | 1 | (OperatingSystem.IsMacOS() ? 0x200 | 0x800 : 0x40 | 0x80);
    private static int Descriptor(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private static void Throw(string operation) => throw new IOException($"Cannot {operation}.", new Win32Exception(Marshal.GetLastPInvokeError()));

    internal sealed class DestinationLease(SafeFileHandle parent, string fullPath, string name, string tempName) : IDisposable
    {
        internal string TempPath => Path.Combine(Path.GetDirectoryName(fullPath)!, tempName);

        internal async ValueTask<string?> HashDestinationAsync(CancellationToken cancellationToken) =>
            await HashAsync(name, cancellationToken).ConfigureAwait(false);

        internal async ValueTask<string?> HashTempAsync(CancellationToken cancellationToken) =>
            await HashAsync(tempName, cancellationToken).ConfigureAwait(false);

        internal enum EntryKind { Missing, Regular, Nonregular }
        internal sealed record Observation(EntryKind Kind, string? Hash);
        internal ValueTask<Observation> ObserveDestinationAsync(CancellationToken cancellationToken) => ObserveAsync(name, cancellationToken);

        private async ValueTask<string?> HashAsync(string fileName, CancellationToken cancellationToken) =>
            (await ObserveAsync(fileName, cancellationToken).ConfigureAwait(false)).Hash;

        private static bool IsRegular(byte[] metadata)
        {
            // Darwin st_mode is ushort at 4; Linux x64 uses uint at 24, arm64 uint at 16.
            var mode = OperatingSystem.IsMacOS() ? BitConverter.ToUInt16(metadata, 4)
                : BitConverter.ToUInt32(metadata, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 16 : 24);
            return (mode & 0xf000) == 0x8000;
        }

        private async ValueTask<Observation> ObserveAsync(string fileName, CancellationToken cancellationToken)
        {
            var fd = Native.OpenAt(Descriptor(parent), fileName, NoFollowFlags | (OperatingSystem.IsMacOS() ? 4 : 0x800), 0);
            if (fd < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 2) return new(EntryKind.Missing, null);
                // Permission denial may hide an unreadable directory/FIFO. Metadata inspection needs only
                // the retained parent's search permission; regular-file open errors still propagate.
                if (error is 40 or 62 or 21 or 6 or 13)
                {
                    var metadata = new byte[512];
                    if (Native.StatAt(Descriptor(parent), fileName, metadata, OperatingSystem.IsMacOS() ? 0x20 : 0x100) != 0)
                    {
                        if (Marshal.GetLastPInvokeError() == 2) return new(EntryKind.Missing, null);
                        Throw("inspect mutation entry without following symlinks");
                    }
                    if (!IsRegular(metadata)) return new(EntryKind.Nonregular, null);
                }
                throw new IOException("Cannot open mutation file without following symlinks.", new Win32Exception(error));
            }
            using var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
            var stat = new byte[512];
            if (Native.Stat(fd, stat) != 0) Throw("inspect mutation descriptor");
            if (!IsRegular(stat)) return new(EntryKind.Nonregular, null);
            await using var stream = new FileStream(handle, FileAccess.Read);
            return new(EntryKind.Regular, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
        }

        internal async ValueTask WriteTempAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            DeleteTemp();
            await WriteNewAsync(tempName, bytes, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask WriteNewAsync(string fileName, byte[] bytes, CancellationToken cancellationToken)
        {
            var fd = Native.OpenAt(Descriptor(parent), fileName, CreateFlags, 0x180); // 0600
            if (fd < 0) Throw("create private mutation file");
            await using (var stream = new FileStream(new SafeFileHandle((nint)fd, ownsHandle: true), FileAccess.Write))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            SyncDirectory();
        }

        /// <summary>Atomic no-replace for creates; exchange-and-verify for edits preserves raced content.</summary>
        internal async ValueTask<bool> CommitAsync(string? expectedHash, string newHash, string rescueName, Func<string, CancellationToken, ValueTask> crashPoint, CancellationToken cancellationToken)
        {
            var admitted = await ObserveDestinationAsync(cancellationToken).ConfigureAwait(false);
            if (admitted.Kind == EntryKind.Nonregular || !StringComparer.OrdinalIgnoreCase.Equals(admitted.Hash, expectedHash))
                return false;
            await crashPoint(SecondBrain.Core.Durability.MutationCrashPointNames.BeforeAtomicRename, cancellationToken).ConfigureAwait(false);
            if (expectedHash is null)
            {
                if (!Rename(tempName, name, exclusive: true)) return false;
                SyncDirectory();
                return true;
            }

            if (!Exchange(tempName, name)) return false;
            SyncDirectory();
            await crashPoint(SecondBrain.Core.Durability.MutationCrashPointNames.AfterAtomicExchange, cancellationToken).ConfigureAwait(false);
            // The displaced inode is checked after the atomic exchange, closing the hash-before-rename race.
            var displaced = await ObserveAsync(tempName, cancellationToken).ConfigureAwait(false);
            if (displaced.Kind != EntryKind.Regular || !StringComparer.OrdinalIgnoreCase.Equals(displaced.Hash, expectedHash))
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(await HashDestinationAsync(cancellationToken).ConfigureAwait(false), newHash))
                {
                    if (!Exchange(tempName, name)) throw new IOException("Cannot restore a raced external edit.");
                    SyncDirectory();
                }
                else
                {
                    // An additional external replacement now owns the destination; preserve the displaced edit beside it.
                    if (!Rename(tempName, rescueName, exclusive: true)) throw new IOException("Cannot preserve a displaced external edit.");
                    SyncDirectory();
                }
                return false;
            }
            DeleteTemp();
            return true;
        }

        internal async ValueTask<bool> ReconcileRenameAsync(string? expectedHash, string newHash, string rescueName, CancellationToken cancellationToken)
        {
            var displaced = await ObserveAsync(tempName, cancellationToken).ConfigureAwait(false);
            if (displaced.Kind == EntryKind.Missing || (displaced.Kind == EntryKind.Regular && (displaced.Hash == expectedHash || displaced.Hash == newHash)))
            {
                DeleteTemp();
                return true;
            }
            // A crash may have interrupted exchange-and-verify. Restore the displaced unseen edit before reporting conflict.
            if (await HashDestinationAsync(cancellationToken).ConfigureAwait(false) == newHash)
            {
                if (!Exchange(tempName, name)) throw new IOException("Cannot restore an interrupted external edit.");
                SyncDirectory();
            }
            else
            {
                if (!Rename(tempName, rescueName, exclusive: true)) throw new IOException("Cannot preserve an interrupted external edit.");
                SyncDirectory();
            }
            return false;
        }

        internal async ValueTask<string> PreserveConflictAsync(string conflictName, byte[] bytes, CancellationToken cancellationToken)
        {
            var observed = await ObserveAsync(conflictName, cancellationToken).ConfigureAwait(false);
            if (observed.Kind == EntryKind.Nonregular) throw new IOException("Conflict destination is already occupied.");
            var existing = observed.Hash;
            var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (existing is null)
            {
                var stage = $"{tempName}.conflict";
                Delete(stage);
                await WriteNewAsync(stage, bytes, cancellationToken).ConfigureAwait(false);
                if (!Rename(stage, conflictName, exclusive: true))
                {
                    Delete(stage);
                    existing = await HashAsync(conflictName, cancellationToken).ConfigureAwait(false);
                    if (existing != expected) throw new IOException("Conflict destination is already occupied.");
                }
                SyncDirectory();
            }
            else if (existing != expected)
                throw new IOException("Conflict destination changed externally.");
            DeleteTemp();
            return Path.Combine(Path.GetDirectoryName(fullPath)!, conflictName);
        }

        internal string ConflictName(string timestamp, string mutationId) =>
            $"{Path.GetFileNameWithoutExtension(name)}.conflict-{timestamp}-{Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mutationId)))[..12]}.md";

        internal void DeleteTemp()
        {
            Delete(tempName);
            Delete($"{tempName}.conflict");
        }

        private void Delete(string fileName)
        {
            var metadata = new byte[512];
            if (Native.StatAt(Descriptor(parent), fileName, metadata, OperatingSystem.IsMacOS() ? 0x20 : 0x100) != 0)
            {
                if (Marshal.GetLastPInvokeError() == 2) return;
                Throw("inspect mutation cleanup entry");
            }
            // Preserve unexpected directories and nonregular entries; cleanup owns regular staged bytes only.
            if (!IsRegular(metadata)) return;
            if (Native.UnlinkAt(Descriptor(parent), fileName, 0) < 0 && Marshal.GetLastPInvokeError() != 2)
                Throw("remove mutation temp file");
            SyncDirectory();
        }

        private bool Rename(string from, string to, bool exclusive)
        {
            var result = OperatingSystem.IsMacOS()
                ? Native.RenameAtX(Descriptor(parent), from, Descriptor(parent), to, exclusive ? 4u : 0u)
                : Native.RenameAt2(Descriptor(parent), from, Descriptor(parent), to, exclusive ? 1u : 0u);
            if (result == 0) return true;
            if (Marshal.GetLastPInvokeError() is 2 or 17) return false;
            Throw("atomically rename mutation file");
            return false;
        }

        private bool Exchange(string from, string to)
        {
            var result = OperatingSystem.IsMacOS()
                ? Native.RenameAtX(Descriptor(parent), from, Descriptor(parent), to, 2u)
                : Native.RenameAt2(Descriptor(parent), from, Descriptor(parent), to, 2u);
            if (result == 0) return true;
            if (Marshal.GetLastPInvokeError() == 2) return false;
            Throw("atomically exchange mutation file");
            return false;
        }

        private void SyncDirectory()
        {
            if (Native.Fsync(Descriptor(parent)) != 0) Throw("fsync mutation directory");
        }

        public void Dispose() => parent.Dispose();
    }

    private static class Native
    {
        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        internal static extern int Stat(int fd, [Out] byte[] metadata);
        [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
        internal static extern int StatAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] metadata, int flags);
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        internal static int OpenAt(int parent, string path, int flags, int mode)
        {
            // Darwin ARM64 passes C variadic arguments on the stack. Five padding registers put mode
            // in the first stack slot; declaring openat as four fixed arguments would use x3 instead.
            return OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? OpenAtDarwinArm64(parent, path, flags, 0, 0, 0, 0, 0, mode)
                : OpenAtFixed(parent, path, flags, mode);
        }
        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAtFixed(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAtDarwinArm64(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags,
            nint padding0, nint padding1, nint padding2, nint padding3, nint padding4, int mode);
        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
        internal static extern int UnlinkAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int Fsync(int fd);
        [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
        internal static extern int RenameAtX(int oldParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int newParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
        [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
        internal static extern int RenameAt2(int oldParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int newParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
    }
}
