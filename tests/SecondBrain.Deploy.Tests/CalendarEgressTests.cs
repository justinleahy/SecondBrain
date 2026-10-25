using System.Net;
using System.Text.Json;
using SecondBrain.Cli;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Provisioning;
using SecondBrain.Core.Configuration;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CalendarEgressTests
{
    [Fact]
    public async Task GeneratorUsesOnlyApprovedOriginsAndKeepsProviderAndSecretSettingsSeparate()
    {
        var options = Options("https://feed.test", "https://feed.test:443", "https://other.test:8443");
        options.Sources.CalendarSubscriptions = [new() { Id = "sample", Name = "Calendar", UrlReference = "private-link-must-not-be-read" }];
        var calls = new List<string>();
        var plan = await new CalendarEgressPlanner((host, _) =>
        {
            calls.Add(host);
            return Task.FromResult(host switch
            {
                "feed.test" => new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:4700:4700::1001") },
                "other.test" => [IPAddress.Parse("8.8.4.4")],
                _ => throw new InvalidOperationException()
            });
        }).BuildAsync(options);
        Assert.Equal(new[] { "feed.test", "other.test" }, calls);
        Assert.Equal(2, plan.Origins.Count); Assert.Equal(3, plan.Destinations.Count);
        Assert.Contains("IPAddressAllow=2606:4700:4700::1001", plan.SystemdDropIn);
        Assert.Contains("[2606:4700:4700::1001]:443", plan.ComposeEnvironment);
        Assert.Contains("8.8.4.4:8443", plan.ComposeEnvironment);
        Assert.DoesNotContain("private-link", JsonSerializer.Serialize(plan)); Assert.Empty(options.Privacy.TrustedServices);
        Assert.DoesNotContain("IPAddressAllow=1.1.1.1", plan.SystemdDropIn);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("ff02::1")]
    [InlineData("1.1.1.1")]
    [InlineData("::ffff:1.1.1.1")]
    public async Task ForbiddenOrCanaryAddressPreventsAnyPlan(string refused)
    {
        var planner = new CalendarEgressPlanner((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse(refused) }));
        await Assert.ThrowsAsync<CliPreconditionException>(() => planner.BuildAsync(Options("https://feed.test")));
    }

    [Theory]
    [InlineData("http://feed.test")]
    [InlineData("https://feed.test/private-link")]
    [InlineData("https://user:private-link@feed.test")]
    public async Task InvalidOriginsDoNotReachDnsOrLeakInput(string origin)
    {
        var planner = new CalendarEgressPlanner((_, _) => throw new InvalidOperationException("DNS must not be called"));
        var error = await Assert.ThrowsAsync<CliPreconditionException>(() => planner.BuildAsync(Options(origin)));
        Assert.DoesNotContain("private-link", error.Message);
    }

    [Fact]
    public async Task CanaryHostnameAliasesAreExcludedAfterResolvingBothNames()
    {
        var options = Options("https://feed.test"); options.Privacy.CanaryTarget = "canary.test:443";
        var planner = new CalendarEgressPlanner((_, _) => Task.FromResult(new[] { IPAddress.Parse("2606:4700:4700::1001") }));
        await Assert.ThrowsAsync<CliPreconditionException>(() => planner.BuildAsync(options));
    }

    [Fact]
    public async Task EmptyPolicyRegenerationRemovesOldPinsWithoutDnsOrChangingOtherFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "calendar-egress-" + Guid.NewGuid().ToString("N"));
        try
        {
            var planner = new CalendarEgressPlanner((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
            await CalendarEgressPlanner.WriteAsync(await planner.BuildAsync(Options("https://feed.test")), directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "operator.conf"), "preserve");
            var empty = await new CalendarEgressPlanner((_, _) => throw new InvalidOperationException("No DNS for empty policy")).BuildAsync(Options());
            await CalendarEgressPlanner.WriteAsync(empty, directory);
            Assert.DoesNotContain("IPAddressAllow=", await File.ReadAllTextAsync(Path.Combine(directory, "40-calendar-egress.conf")));
            Assert.Contains("SECONDBRAIN_CALENDAR_ALLOWLIST=\n", await File.ReadAllTextAsync(Path.Combine(directory, "calendar-egress.env")));
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(directory, "operator.conf")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CliPreparesFilesWithoutDaemonAccess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "calendar-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options("https://feed.test");
            options.DataRoot = Path.Combine(directory, "data"); options.Sources.IncomingRoot = Path.Combine(directory, "incoming");
            options.Server.Listeners = [new() { Bind = "127.0.0.1", Port = 7171 }]; options.Server.Hosts = ["localhost"]; options.Server.Origins = ["http://localhost:7171"];
            var output = new StringWriter();
            var services = new CliServices(false) { ReadConfiguration = (_, _) => options,
                CalendarEgress = new((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })) };
            var result = await SecondBrain.Cli.Program.RunAsync(["maintenance", "calendar-egress", "--output-dir", directory, "--json"], services, output, new StringWriter());
            Assert.Equal(0, result); Assert.True(File.Exists(Path.Combine(directory, "40-calendar-egress.conf")));
            Assert.Contains("installation and service restart", output.ToString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task GeneratedIpv4AndIpv6DropInPassesSystemdSyntaxValidation()
    {
        if (!File.Exists("/usr/bin/systemd-analyze")) return;
        var directory = Path.Combine(Path.GetTempPath(), "calendar-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var plan = await new CalendarEgressPlanner((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:4700:4700::1001") })).BuildAsync(Options("https://feed.test"));
            var unit = Path.Combine(directory, "calendar-fixture.service");
            await File.WriteAllTextAsync(unit, "[Unit]\nDescription=Calendar policy syntax fixture\n[Service]\nExecStart=/bin/true\nIPAddressDeny=any\n" + plan.SystemdDropIn);
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/systemd-analyze") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "verify", "--man=no", unit }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error + await output);
        }
        finally { Directory.Delete(directory, true); }
    }
    private static SecondBrainOptions Options(params string[] origins) => new() { Sources = new() { CalendarAllowedOrigins = origins.ToList() } };
}
