using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Operations;

namespace SecondBrain.Infrastructure.Security;

/// <summary>Reads a raw 32-byte key from a private external secret file, or a base64 environment secret.</summary>
public sealed class FileBackupKeyProvider(IOptionsMonitor<SecondBrainOptions> options, Func<string, string?>? environment = null) : IBackupKeyProvider
{
    public async ValueTask<BackupKey> ReadAsync(CancellationToken token = default)
    {
        byte[]? bytes = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var value = (environment ?? Environment.GetEnvironmentVariable)("SECONDBRAIN_BACKUP_KEY");
            if (value is not null)
            {
                if (value.Length > 128) throw new BackupException("backup_key_invalid");
                bytes = Convert.FromBase64String(value);
            }
            else
            {
                if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new BackupException("backup_key_platform");
                var path = Path.GetFullPath(options.CurrentValue.Backup.KeyFile);
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.CurrentValue.DataRoot));
                if (path == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || UnixPath.Canonicalize(path) != path)
                    throw new BackupException("backup_key_location");
                var flags = OperatingSystem.IsMacOS() ? 0x1000000 | 0x100 | 4 : 0x80000 | 0x20000 | 0x800;
                var fd = Open(path, flags);
                if (fd < 0) throw new BackupException("backup_key_unavailable");
                using var handle = new SafeFileHandle((nint)fd, true);
                var before = Inspect(handle);
                bytes = new byte[32];
                await using (var stream = new FileStream(handle, FileAccess.Read))
                {
                    await stream.ReadExactlyAsync(bytes, token);
                    var after = Inspect(handle);
                    if (!before.AsSpan().SequenceEqual(after)) throw new BackupException("backup_key_changed");
                }
            }
            if (bytes.Length != 32) throw new BackupException("backup_key_invalid");
            return new BackupKey(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        { throw new BackupException("backup_key_unavailable"); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Inspect(SafeFileHandle handle)
    {
        var stat = new byte[512]; if (Fstat(checked((int)handle.DangerousGetHandle()), stat) != 0) throw new BackupException("backup_key_unavailable");
        var mac = OperatingSystem.IsMacOS(); var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var mode = mac ? BitConverter.ToUInt16(stat, 4) : BitConverter.ToUInt32(stat, arm ? 16 : 24);
        var owner = BitConverter.ToUInt32(stat, mac ? 16 : arm ? 24 : 28);
        var links = mac ? BitConverter.ToUInt16(stat, 6) : arm ? BitConverter.ToUInt32(stat, 20) : BitConverter.ToUInt64(stat, 16);
        var length = BitConverter.ToInt64(stat, mac ? 96 : 48);
        if ((mode & 0xf000) != 0x8000 || (mode & 0x1ff) != 0x180 || links != 1 || owner != 0 && owner != RootSecurityValidator.CurrentUid() || length != 32)
            throw new BackupException("backup_key_permissions_or_size");
        // Ignore access-time changes caused by this read; retain inode, size, modification and change times.
        return mac ? [..stat.AsSpan(0,24),..stat.AsSpan(48,32),..stat.AsSpan(96,8)] : [..stat.AsSpan(0,56),..stat.AsSpan(88,32)];
    }
    [DllImport("libc", EntryPoint="open", SetLastError=true)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path,int flags);
    [DllImport("libc", EntryPoint="fstat", SetLastError=true)] private static extern int Fstat(int fd,[Out] byte[] stat);
}
