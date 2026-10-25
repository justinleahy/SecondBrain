using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Operations;

namespace SecondBrain.Infrastructure.Security;

public sealed record BackupKeyProvisioned(bool Created, string KeyFile, string RecoveryCopy);

/// <summary>Creates an absent backup key and a separate recovery copy without returning secret bytes.</summary>
public sealed class BackupKeyProvisioner
{
    public async Task<BackupKeyProvisioned> EnsureAsync(SecondBrainOptions options, string recoveryCopy,
        CancellationToken token = default)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new BackupException("backup_key_platform");
        if (!Path.IsPathFullyQualified(options.Backup.KeyFile) || !Path.IsPathFullyQualified(recoveryCopy))
            throw new BackupException("backup_key_recovery_location");
        var keyPath = Path.GetFullPath(options.Backup.KeyFile);
        var copyPath = Path.GetFullPath(recoveryCopy);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DataRoot));
        if (!Path.IsPathFullyQualified(recoveryCopy) || Within(keyPath, root) || Within(copyPath, root)
            || Within(copyPath, Path.GetDirectoryName(keyPath)!) || keyPath == copyPath)
            throw new BackupException("backup_key_recovery_location");
        ValidateParent(keyPath, requirePrivate: true); ValidateParent(copyPath, requirePrivate: false);
        var created = false;
        if (!Path.Exists(keyPath))
        {
            if (Path.Exists(copyPath)) throw new BackupException("backup_key_recovery_exists");
            var bytes = RandomNumberGenerator.GetBytes(32);
            try { await WriteNewAsync(keyPath, bytes, token); created = true; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        using var key = await ReadAsync(keyPath, root, token);
        if (Path.Exists(copyPath))
        {
            using var copy = await ReadAsync(copyPath, root, token);
            if (!CryptographicOperations.FixedTimeEquals(key.Bytes.Span, copy.Bytes.Span)) throw new BackupException("backup_key_recovery_mismatch");
        }
        else await WriteNewAsync(copyPath, key.Bytes, token);
        return new(created, keyPath, copyPath);
    }

    private static ValueTask<BackupKey> ReadAsync(string path, string root, CancellationToken token) =>
        new FileBackupKeyProvider(new ConstantOptions(new SecondBrainOptions { DataRoot = root, Backup = new() { KeyFile = path } }), _ => null).ReadAsync(token);

    private static void ValidateParent(string path, bool requirePrivate)
    {
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent) || UnixPath.Canonicalize(parent) != parent || new DirectoryInfo(parent).LinkTarget is not null)
            throw new BackupException("backup_key_parent");
        if (OperatingSystem.IsWindows()) throw new BackupException("backup_key_platform");
        var mode = File.GetUnixFileMode(parent);
        if (requirePrivate && mode != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
            || (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0
            || RootSecurityValidator.Ownership(parent).Uid != RootSecurityValidator.CurrentUid())
            throw new BackupException("backup_key_parent_permissions");
    }
    private static async Task WriteNewAsync(string path, ReadOnlyMemory<byte> value, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) throw new BackupException("backup_key_platform");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".backup-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { await stream.WriteAsync(value, token); stream.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: false); UnixSecurity.SyncDirectory(Path.GetDirectoryName(path)!);
        }
        finally { File.Delete(temporary); }
    }
    private static bool Within(string path, string root) => path == root || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private sealed class ConstantOptions(SecondBrainOptions options) : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue => options;
        public SecondBrainOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
}
