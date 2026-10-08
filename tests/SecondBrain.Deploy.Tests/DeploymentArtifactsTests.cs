using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class DeploymentArtifactsTests
{
    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "spec.md")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "deploy")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not find deployment artifacts beside spec.md.");
        }
    }

    [Fact]
    public async Task ComposeConfigPassesAndHasPrivateBinding()
    {
        var result = await Run("docker", ["compose", "-f", "deploy/compose.yaml", "config", "--format", "json"],
            new Dictionary<string, string> { ["SECONDBRAIN_PRIVATE_ADDRESS"] = "127.0.0.1" });
        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var services = document.RootElement.GetProperty("services");
        var daemon = services.GetProperty("daemon");
        var extractor = services.GetProperty("extractor");
        var egress = services.GetProperty("egress");
        Assert.Equal("1654:1654", daemon.GetProperty("user").GetString());
        Assert.Equal("1655:1654", extractor.GetProperty("user").GetString());
        Assert.Equal("none", extractor.GetProperty("network_mode").GetString());
        Assert.Equal("service:egress", daemon.GetProperty("network_mode").GetString());
        Assert.Equal("127.0.0.1", egress.GetProperty("ports")[0].GetProperty("host_ip").GetString());
        foreach (var name in new[] { "daemon", "extractor", "egress" })
        {
            var service = services.GetProperty(name);
            Assert.True(service.GetProperty("read_only").GetBoolean());
            Assert.Contains("ALL", service.GetProperty("cap_drop").EnumerateArray().Select(x => x.GetString()));
            Assert.Contains("no-new-privileges:true", service.GetProperty("security_opt").EnumerateArray().Select(x => x.GetString()));
            Assert.True(long.Parse(service.GetProperty("mem_limit").ToString(), System.Globalization.CultureInfo.InvariantCulture) > 0);
            Assert.True(long.Parse(service.GetProperty("pids_limit").ToString(), System.Globalization.CultureInfo.InvariantCulture) > 0);
            var tmpfs = Assert.Single(service.GetProperty("tmpfs").EnumerateArray()).GetString();
            Assert.StartsWith("/", tmpfs, StringComparison.Ordinal);
            Assert.Contains("noexec,nosuid", tmpfs, StringComparison.Ordinal);
        }

        Assert.False(daemon.TryGetProperty("cap_add", out _));
        Assert.Equal("NET_ADMIN", Assert.Single(egress.GetProperty("cap_add").EnumerateArray()).GetString());
        var volumes = daemon.GetProperty("volumes").EnumerateArray().ToArray();
        foreach (var target in new[] { "/etc/secondbrain/config.yaml", "/etc/secondbrain/secrets", "/etc/resolv.conf" })
        {
            Assert.True(volumes.Single(v => v.GetProperty("target").GetString() == target).GetProperty("read_only").GetBoolean());
        }

        Assert.DoesNotContain(extractor.GetProperty("volumes").EnumerateArray(),
            x => x.GetProperty("target").GetString() is "/srv/secondbrain" or "/etc/secondbrain/secrets");
    }

    [Fact]
    public async Task ComposeRequiresAnExplicitHostBind()
    {
        var result = await Run("docker", ["compose", "-f", "deploy/compose.yaml", "config"],
            new Dictionary<string, string> { ["SECONDBRAIN_PRIVATE_ADDRESS"] = string.Empty });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SECONDBRAIN_PRIVATE_ADDRESS", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TunnelSharesLoopbackButHasSeparateIdentityAndReadOnlyCredential()
    {
        var result = await Run("docker", ["compose", "--profile", "tunnel", "-f", "deploy/compose.yaml", "config", "--format", "json"],
            new Dictionary<string, string> { ["SECONDBRAIN_PRIVATE_ADDRESS"] = "100.64.0.2" });
        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var tunnel = document.RootElement.GetProperty("services").GetProperty("cloudflared");
        Assert.Equal("65532:65532", tunnel.GetProperty("user").GetString());
        Assert.Equal("service:egress", tunnel.GetProperty("network_mode").GetString());
        Assert.True(tunnel.GetProperty("read_only").GetBoolean());
        Assert.Contains("@sha256:", tunnel.GetProperty("image").GetString(), StringComparison.Ordinal);
        Assert.Equal("tunnel_credentials", tunnel.GetProperty("secrets")[0].GetProperty("source").GetString());
        Assert.Contains("service: http://127.0.0.1:7171", Read("deploy/provision/cloudflared.yaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDockerBaseIsDigestPinnedAndServingTargetsAreNonRoot()
    {
        foreach (var relative in new[] { "deploy/docker/Dockerfile", "deploy/docker/Egress.Dockerfile" })
        {
            var text = Read(relative);
            foreach (Match match in Regex.Matches(text, @"(?m)^FROM\s+(\S+)", RegexOptions.CultureInvariant))
            {
                var image = match.Groups[1].Value;
                if (image != "runtime-base")
                {
                    Assert.Matches(@"@sha256:[a-f0-9]{64}$", image);
                }
            }
        }

        var dockerfile = Read("deploy/docker/Dockerfile");
        Assert.Contains("USER 1654:1654", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER 1655:1654", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--check --strict", dockerfile, StringComparison.Ordinal);
        Assert.Contains("umask 0077", Read("deploy/docker/entrypoint.sh"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("secondbrain.service", "secondbrain")]
    [InlineData("secondbrain-extractor.service", "secondbrain-extract")]
    public void ServiceUnitsHaveRequiredHardening(string name, string user)
    {
        var unit = ParseUnit(Read("deploy/systemd/" + name));
        AssertDirective(unit, "Service", "User", user);
        AssertDirective(unit, "Service", "NoNewPrivileges", "yes");
        AssertDirective(unit, "Service", "ProtectSystem", "strict");
        AssertDirective(unit, "Service", "ProtectHome", "yes");
        AssertDirective(unit, "Service", "PrivateTmp", "yes");
        AssertDirective(unit, "Service", "CapabilityBoundingSet", string.Empty);
        AssertDirective(unit, "Service", "AmbientCapabilities", string.Empty);
        AssertDirective(unit, "Service", "IPAddressDeny", "any");
        AssertDirective(unit, "Service", "LimitCORE", "0");
        Assert.True(unit[("Service", "ReadWritePaths")].Count > 0);
        Assert.True(unit[("Service", "RestrictAddressFamilies")].Count > 0);
        if (user == "secondbrain")
        {
            AssertDirective(unit, "Service", "UMask", "0077");
            AssertDirective(unit, "Service", "ReadWritePaths", "/srv/secondbrain /srv/secondbrain-incoming /run/secondbrain");
            AssertDirective(unit, "Service", "RestrictAddressFamilies", "AF_UNIX AF_INET AF_INET6");
            foreach (var allowance in new[] { "localhost", "10.8.0.5", "10.8.0.1", "192.0.2.1" })
            {
                AssertDirective(unit, "Service", "IPAddressAllow", allowance);
            }
        }
        else
        {
            AssertDirective(unit, "Service", "RestrictAddressFamilies", "AF_UNIX");
            AssertDirective(unit, "Service", "PrivateNetwork", "yes");
            AssertDirective(unit, "Service", "KillMode", "control-group");
            AssertDirective(unit, "Service", "InaccessiblePaths", "/srv/secondbrain /srv/secondbrain-incoming /etc/secondbrain");
        }
    }

    [Fact]
    public void ExtractorSocketIsGroupReadableAndActivatesOneOfflineService()
    {
        var unit = ParseUnit(Read("deploy/systemd/secondbrain-extractor.socket"));
        AssertDirective(unit, "Socket", "ListenStream", "/run/secondbrain/extractor.sock");
        AssertDirective(unit, "Socket", "SocketUser", "secondbrain-extract");
        AssertDirective(unit, "Socket", "SocketGroup", "secondbrain");
        AssertDirective(unit, "Socket", "SocketMode", "0660");
        AssertDirective(unit, "Socket", "Accept", "no");
        AssertDirective(unit, "Socket", "Service", "secondbrain-extractor.service");
    }

    [Fact]
    public async Task UnitsPassSystemdAnalyzeWhenAvailable()
    {
        var executable = new[] { "/usr/bin/systemd-analyze", "/bin/systemd-analyze" }.FirstOrDefault(File.Exists);
        if (executable is null)
        {
            // Structural assertions above are the supported macOS fallback.
            return;
        }

        using var temporary = new TemporaryDirectory();
        foreach (var name in new[] { "secondbrain.service", "secondbrain-extractor.service", "secondbrain-extractor.socket" })
        {
            // Verify syntax and unit relationships without requiring a host install.
            var text = Regex.Replace(Read("deploy/systemd/" + name), @"(?m)^ExecStart=.*$", "ExecStart=/bin/true");
            File.WriteAllText(Path.Combine(temporary.Path, name), text);
        }

        var result = await Run(executable, ["verify", "--man=no", Path.Combine(temporary.Path, "secondbrain.service"),
            Path.Combine(temporary.Path, "secondbrain-extractor.service"), Path.Combine(temporary.Path, "secondbrain-extractor.socket")]);
        Assert.True(result.ExitCode == 0, result.Error);
    }

    [Theory]
    [InlineData("true", "10.8.0.5:8000", "192.0.2.1:443", "203.0.113.7:443", false)]
    [InlineData("false", "10.8.0.5:8000", "192.0.2.1:443", "203.0.113.7:443", true)]
    public async Task FirewallSeparatesProviderAccessAndTunnelUids(string localOnly, string provider, string access, string hosted, bool includesHosted)
    {
        var result = await RunFirewall(localOnly, provider, access, hosted);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.StartsWith("-P OUTPUT DROP", result.Output, StringComparison.Ordinal);
        Assert.Contains("--uid-owner 1654 -p tcp -d 10.8.0.5 --dport 8000 -j ACCEPT", result.Output, StringComparison.Ordinal);
        Assert.Contains("--uid-owner 1654 -p tcp -d 192.0.2.1 --dport 443 -j ACCEPT", result.Output, StringComparison.Ordinal);
        Assert.Contains("--uid-owner 65532 -p tcp -d 198.41.192.167 --dport 7844 -j ACCEPT", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--uid-owner 1654 -p tcp -d 198.41.", result.Output, StringComparison.Ordinal);
        Assert.Equal(includesHosted, result.Output.Contains("--uid-owner 1654 -p tcp -d 203.0.113.7", StringComparison.Ordinal));
        Assert.DoesNotContain("-d 1.1.1.1 --dport 443 -j ACCEPT", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", "provider.example:443", "192.0.2.1:443")]
    [InlineData("true", "10.8.0.5:70000", "192.0.2.1:443")]
    [InlineData("true", "10.8.0.5/24:8000", "192.0.2.1:443")]
    [InlineData("true", "10.8.0.5:8000", "192.0.2.1:80")]
    [InlineData("true", "10.999.0.5:8000", "192.0.2.1:443")]
    [InlineData("invalid", "10.8.0.5:8000", "192.0.2.1:443")]
    public async Task FirewallRejectsInvalidAllowancesWithoutBecomingReady(string localOnly, string provider, string access)
    {
        var result = await RunFirewall(localOnly, provider, access, string.Empty);
        Assert.NotEqual(0, result.ExitCode);
        Assert.StartsWith("-P OUTPUT DROP", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("READY", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("8.8.8.8")]
    [InlineData("::")]
    public async Task FirewallRefusesWildcardOrPublicHostBind(string privateAddress)
    {
        var result = await RunFirewall("true", "10.8.0.5:8000", "192.0.2.1:443", string.Empty, privateAddress);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("READY", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSocketDirectoryIsRecreatedWithSharedTraversalAfterReboot()
    {
        Assert.Contains("d /run/secondbrain 2770 secondbrain secondbrain -",
            Read("deploy/provision/secondbrain.tmpfiles.conf"), StringComparison.Ordinal);
    }

    private static async Task<CommandResult> RunFirewall(string localOnly, string provider, string access, string hosted, string privateAddress = "127.0.0.1")
    {
        using var temporary = new TemporaryDirectory();
        var log = Path.Combine(temporary.Path, "rules.log");
        foreach (var name in new[] { "iptables", "ip6tables", "sleep" })
        {
            var path = Path.Combine(temporary.Path, name);
            File.WriteAllText(path, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$EGRESS_TEST_LOG\"\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var ready = Path.Combine(temporary.Path, "ready");
        var result = await Run("sh", ["deploy/docker/egress-entrypoint.sh"], new Dictionary<string, string>
        {
            ["PATH"] = temporary.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["EGRESS_TEST_LOG"] = log,
            ["SECONDBRAIN_EGRESS_READY_PATH"] = ready,
            ["SECONDBRAIN_DNS_IP"] = "10.8.0.1",
            ["SECONDBRAIN_PRIVATE_ADDRESS"] = privateAddress,
            ["SECONDBRAIN_LOCAL_ONLY"] = localOnly,
            ["SECONDBRAIN_PROVIDER_ALLOWLIST"] = provider,
            ["SECONDBRAIN_ACCESS_ALLOWLIST"] = access,
            ["SECONDBRAIN_HOSTED_ALLOWLIST"] = hosted,
        });
        return result with { Output = File.ReadAllText(log) + (File.Exists(ready) ? "READY" : string.Empty) };
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(RepositoryRoot, path));

    private static Dictionary<(string Section, string Name), List<string>> ParseUnit(string content)
    {
        var result = new Dictionary<(string, string), List<string>>();
        var section = string.Empty;
        foreach (var source in content.Split('\n'))
        {
            var line = source.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            var equals = line.IndexOf('=');
            Assert.True(equals > 0 && section.Length > 0, "Malformed unit directive: " + line);
            var key = (section, line[..equals]);
            if (!result.TryGetValue(key, out var values))
            {
                result[key] = values = [];
            }

            values.Add(line[(equals + 1)..]);
        }

        return result;
    }

    private static void AssertDirective(Dictionary<(string Section, string Name), List<string>> unit, string section, string name, string value)
    {
        Assert.True(unit.TryGetValue((section, name), out var values), "Missing unit directive: " + name);
        Assert.Contains(value, values);
    }

    private static async Task<CommandResult> Run(string executable, string[] arguments, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch " + executable);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(executable + " exceeded 30 seconds.");
        }

        return new CommandResult(process.ExitCode, await output, await error);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("secondbrain-deployment-").FullName;
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
