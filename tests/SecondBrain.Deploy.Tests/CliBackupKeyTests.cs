using System.Security.Cryptography;
using SecondBrain.Cli;
using SecondBrain.Infrastructure.Security;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliBackupKeyTests
{
    [Fact]
    public async Task CommandCreatesRecoveryCopyAndNeverPrintsOrRotatesKey()
    {
        using var root = new CliTempRoot(); root.CreateRoots();
        var secrets = Directory.CreateDirectory(Path.Combine(root.Path, "secrets")).FullName;
        var recovery = Directory.CreateDirectory(Path.Combine(root.Path, "recovery")).FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = root.Options(); options.Backup.KeyFile = Path.Combine(secrets, "backup.key");
        var services = new CliServices(false) { ReadConfiguration = (_, _) => options };
        var copy = Path.Combine(recovery, "copy.key");
        var output = new StringWriter();
        Assert.Equal(0, await Cli.Program.RunAsync(root.Identities(["maintenance", "backup-key", "--recovery-copy", copy, "--json"]), services, output, new StringWriter()));
        var key = await File.ReadAllBytesAsync(options.Backup.KeyFile);
        Assert.Equal(32, key.Length); Assert.Equal(key, await File.ReadAllBytesAsync(copy));
        Assert.DoesNotContain(Convert.ToBase64String(key), output.ToString()); Assert.DoesNotContain(Convert.ToHexString(key), output.ToString());
        var before = File.GetLastWriteTimeUtc(options.Backup.KeyFile);
        Assert.Equal(0, await Cli.Program.RunAsync(root.Identities(["maintenance", "backup-key", "--recovery-copy", copy, "--json"]), services, new StringWriter(), new StringWriter()));
        Assert.Equal(before, File.GetLastWriteTimeUtc(options.Backup.KeyFile));
    }

    [Fact]
    public async Task ActiveDaemonAndInvalidRecoveryLocationPreventCreation()
    {
        using var root = new CliTempRoot(); root.CreateRoots();
        var secrets = Directory.CreateDirectory(Path.Combine(root.Path, "secrets")).FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = root.Options(); options.Backup.KeyFile = Path.Combine(secrets, "backup.key");
        var services = new CliServices(false) { ReadConfiguration = (_, _) => options };
        using (DataRootLock.Acquire(root.Data))
            Assert.Equal(4, await Cli.Program.RunAsync(root.Identities(["maintenance", "backup-key", "--recovery-copy", Path.Combine(root.Path, "copy.key")]), services, new StringWriter(), new StringWriter()));
        Assert.False(File.Exists(options.Backup.KeyFile));
        Assert.Equal(4, await Cli.Program.RunAsync(root.Identities(["maintenance", "backup-key", "--recovery-copy", Path.Combine(root.Data, "copy.key")]), services, new StringWriter(), new StringWriter()));
        Assert.False(File.Exists(options.Backup.KeyFile));
    }
}
