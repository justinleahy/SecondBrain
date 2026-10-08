using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Provisioning;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliProvisionTests
{
    [Fact]
    public void Provision_IsIdempotentAndPreservesAdministratorConfiguration()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new CliTempRoot();
        var platform = new FakePlatform();
        var service = new ProvisioningService(platform);
        var options = Options(root);
        var first = service.Provision(options);
        var config = Path.Combine(options.ConfigDirectory, "config.yaml");
        File.AppendAllText(config, "# administrator edit\n");
        var preserved = File.ReadAllText(config);
        var second = service.Provision(options);
        Assert.Equal(first.Actions, second.Actions);
        Assert.Equal(preserved, File.ReadAllText(config));
        Assert.Contains((options.DataRoot, options.DaemonUid, options.DaemonGid), platform.Owners);
        Assert.Contains((options.IncomingRoot, options.SyncUid, options.DaemonGid), platform.Owners);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(options.DataRoot));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(options.IncomingRoot));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup,
            File.GetUnixFileMode(options.RuntimeDirectory));
    }

    [Fact]
    public void Provision_DryRunNeedsNoRootAndPerformsNoPrivilegedActions()
    {
        using var root = new CliTempRoot();
        var platform = new FakePlatform { IsRoot = false };
        var result = new ProvisioningService(platform).Provision(Options(root) with { DryRun = true });
        Assert.True(result.DryRun);
        Assert.Empty(platform.Owners);
        Assert.Empty(platform.Users);
        Assert.False(Directory.Exists(root.Data));
    }

    [Fact]
    public void Provision_RejectsNonRootExecution()
    {
        using var root = new CliTempRoot();
        Assert.Throws<CliPreconditionException>(() => new ProvisioningService(new FakePlatform { IsRoot = false }).Provision(Options(root)));
    }

    [Fact]
    public void Provision_RejectsSharedAndNestedRoots()
    {
        using var root = new CliTempRoot();
        var service = new ProvisioningService(new FakePlatform());
        Assert.Throws<CliUsageException>(() => service.Provision(Options(root) with { IncomingRoot = root.Data }));
        Assert.Throws<CliUsageException>(() => service.Provision(Options(root) with { IncomingRoot = Path.Combine(root.Data, "incoming") }));
    }

    [Theory]
    [InlineData(0U, 1655U, 1656U)]
    [InlineData(1654U, 1654U, 1656U)]
    [InlineData(1654U, 1655U, 1654U)]
    public void Provision_RejectsRootOrSharedServiceIdentities(uint daemon, uint extractor, uint sync)
    {
        using var root = new CliTempRoot();
        Assert.Throws<CliUsageException>(() => new ProvisioningService(new FakePlatform()).Provision(Options(root) with { DaemonUid = daemon, ExtractorUid = extractor, SyncUid = sync }));
    }

    [Fact]
    public void Provision_RejectsSymlinkedTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new CliTempRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "actual"));
        Directory.CreateSymbolicLink(root.Data, Path.Combine(root.Path, "actual"));
        Assert.Throws<CliPreconditionException>(() => new ProvisioningService(new FakePlatform()).Provision(Options(root)));
    }

    [Fact]
    public void Provision_InstallsSystemdUnitsAndRuntimeTmpfilesWithOverrides()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new CliTempRoot();
        var options = Options(root) with { Deployment = "systemd" };
        Directory.CreateDirectory(Path.Combine(options.TemplatesDirectory, "systemd"));
        Directory.CreateDirectory(Path.Combine(options.TemplatesDirectory, "provision"));
        foreach (var name in new[] { "secondbrain.service", "secondbrain-extractor.socket", "secondbrain-extractor.service" })
            File.WriteAllText(Path.Combine(options.TemplatesDirectory, "systemd", name), "ReadWritePaths=/srv/secondbrain /srv/secondbrain-incoming /run/secondbrain\n");
        File.WriteAllText(Path.Combine(options.TemplatesDirectory, "provision", "secondbrain.tmpfiles.conf"), "d /run/secondbrain 2770 secondbrain secondbrain -\n");
        new ProvisioningService(new FakePlatform()).Provision(options);
        Assert.Contains(options.DataRoot, File.ReadAllText(Path.Combine(options.UnitsDirectory, "secondbrain.service")));
        Assert.Contains(options.RuntimeDirectory, File.ReadAllText(Path.Combine(options.TmpFilesDirectory, "secondbrain.conf")));
    }

    [Fact]
    public void Provision_SelectsComposeConfigAndInstallsSupportingFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new CliTempRoot();
        var options = Options(root) with { Deployment = "compose" };
        Directory.CreateDirectory(Path.Combine(options.TemplatesDirectory, "docker", "egress"));
        Directory.CreateDirectory(Path.Combine(options.TemplatesDirectory, "provision"));
        File.WriteAllText(Path.Combine(options.TemplatesDirectory, "docker", "compose.yaml"), "x-hardened: &hardened\n  read_only: true\nservices:\n  daemon:\n    <<: *hardened\n    image: secondbrain:local\n    build:\n      context: ..\n");
        File.WriteAllText(Path.Combine(options.TemplatesDirectory, "docker", "egress", "rules.conf"), "allow provider\n");
        File.WriteAllText(Path.Combine(options.TemplatesDirectory, "provision", "config.yaml"), "# systemd\n");
        File.WriteAllText(Path.Combine(options.TemplatesDirectory, "provision", "config.compose.yaml"), "# compose\ndata_root: /srv/secondbrain\n");
        File.WriteAllText(Path.Combine(options.TemplatesDirectory, "provision", "compose.env.example"), "SECONDBRAIN_PRIVATE_ADDRESS=127.0.0.1\n");
        File.WriteAllText(Path.Combine(options.TemplatesDirectory, "provision", "cloudflared.yaml"), "ingress: []\n");
        new ProvisioningService(new FakePlatform()).Provision(options);
        Assert.Contains("# compose", File.ReadAllText(Path.Combine(options.ConfigDirectory, "config.yaml")));
        Assert.Contains(options.DataRoot, File.ReadAllText(Path.Combine(options.ConfigDirectory, "config.yaml")));
        Assert.True(File.Exists(Path.Combine(options.ComposeDirectory, "egress", "rules.conf")));
        var compose = File.ReadAllText(Path.Combine(options.ComposeDirectory, "compose.yaml"));
        Assert.DoesNotContain("build:", compose);
        Assert.Contains("image: secondbrain:local", compose);
        Assert.Contains("*hardened", compose);
        Assert.True(File.Exists(Path.Combine(options.ComposeDirectory, ".env.example")));
        Assert.True(File.Exists(Path.Combine(options.ComposeDirectory, "cloudflared.yaml")));
    }

    private static ProvisionOptions Options(CliTempRoot root) => new()
    {
        DataRoot = root.Data, IncomingRoot = root.Incoming, ConfigDirectory = Path.Combine(root.Path, "etc"), RuntimeDirectory = Path.Combine(root.Path, "run"),
        UnitsDirectory = Path.Combine(root.Path, "units"), TmpFilesDirectory = Path.Combine(root.Path, "tmpfiles"), ComposeDirectory = Path.Combine(root.Path, "compose"),
        TemplatesDirectory = Path.Combine(root.Path, "templates"), Deployment = "none"
    };
    private sealed class FakePlatform : IProvisionPlatform
    {
        public bool IsRoot { get; init; } = true;
        public List<(string Path, uint Uid, uint Gid)> Owners { get; } = [];
        public List<(string Name, uint Uid)> Users { get; } = [];
        public void EnsureGroup(string name, uint gid) { }
        public void EnsureUser(string name, uint uid, string group) => Users.Add((name, uid));
        public void SetOwner(string path, uint uid, uint gid) => Owners.Add((path, uid, gid));
    }
}
