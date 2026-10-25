using System.Security.Cryptography;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Operations;
using SecondBrain.Infrastructure.Security;
using Xunit;

namespace SecondBrain.Infrastructure.Tests.Security;

public sealed class BackupKeyProvisionerTests
{
    [Fact]
    public async Task CreatesPrivateRandomKeyAndRecoveryCopyWithoutRotatingOnRetry()
    {
        using var fixture = new Fixture();
        var service = new BackupKeyProvisioner();
        var first = await service.EnsureAsync(fixture.Options, fixture.Copy);
        Assert.True(first.Created);
        var bytes = await File.ReadAllBytesAsync(fixture.Key);
        Assert.Equal(32, bytes.Length); Assert.NotEqual(new byte[32], bytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Copy));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Key));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Copy));
        }
        var stamp = File.GetLastWriteTimeUtc(fixture.Key);
        var repeated = await service.EnsureAsync(fixture.Options, fixture.Copy);
        Assert.False(repeated.Created); Assert.Equal(stamp, File.GetLastWriteTimeUtc(fixture.Key));
        Assert.DoesNotContain(Convert.ToBase64String(bytes), first.ToString());
    }

    [Fact]
    public async Task ExistingRecoveryMaterialAndMismatchesAreNeverOverwritten()
    {
        using var fixture = new Fixture();
        var service = new BackupKeyProvisioner();
        var original = RandomNumberGenerator.GetBytes(32); await File.WriteAllBytesAsync(fixture.Copy, original);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixture.Copy, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.ThrowsAsync<BackupException>(() => service.EnsureAsync(fixture.Options, fixture.Copy));
        Assert.False(File.Exists(fixture.Key)); Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Copy));
        File.Delete(fixture.Copy); await service.EnsureAsync(fixture.Options, fixture.Copy);
        var key = await File.ReadAllBytesAsync(fixture.Key); await File.WriteAllBytesAsync(fixture.Copy, original);
        await Assert.ThrowsAsync<BackupException>(() => service.EnsureAsync(fixture.Options, fixture.Copy));
        Assert.Equal(key, await File.ReadAllBytesAsync(fixture.Key)); Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Copy));
    }

    [Fact]
    public async Task RejectsDataRootCopiesLinkedParentsAndPublicKeyDirectories()
    {
        using var fixture = new Fixture();
        var service = new BackupKeyProvisioner();
        await Assert.ThrowsAsync<BackupException>(() => service.EnsureAsync(fixture.Options, Path.Combine(fixture.Options.DataRoot, "copy.key")));
        await Assert.ThrowsAsync<BackupException>(() => service.EnsureAsync(fixture.Options, Path.Combine(Path.GetDirectoryName(fixture.Key)!, "copy.key")));
        var link = Path.Combine(fixture.Root, "link");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(fixture.Copy)!);
        await Assert.ThrowsAsync<BackupException>(() => service.EnsureAsync(fixture.Options, Path.Combine(link, "copy.key")));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.GetDirectoryName(fixture.Key)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            await Assert.ThrowsAsync<BackupException>(() => service.EnsureAsync(fixture.Options, fixture.Copy));
        }
        Assert.False(File.Exists(fixture.Key));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = UnixPath.Canonicalize(Directory.CreateTempSubdirectory("sb-key-provision-").FullName);
        internal string Key => Path.Combine(Root, "secrets", "backup.key");
        internal string Copy => Path.Combine(Root, "recovery", "copy.key");
        internal SecondBrainOptions Options { get; }
        internal Fixture()
        {
            foreach (var name in new[] { "secrets", "recovery", "data" })
            {
                var path = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Options = new() { DataRoot = Path.Combine(Root, "data"), Backup = new() { KeyFile = Key } };
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
