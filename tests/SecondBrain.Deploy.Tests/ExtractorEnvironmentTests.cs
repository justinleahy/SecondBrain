using SecondBrain.Extractor;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class ExtractorEnvironmentTests
{
    [Fact]
    public void ScrubPreservesOnlyExplicitServiceAndRuntimeAllowlist()
    {
        var inherited = new Dictionary<string, string?>
        {
            ["LISTEN_PID"] = "4242",
            ["LISTEN_FDS"] = "1",
            ["NOTIFY_SOCKET"] = "/run/systemd/notify",
            ["TMPDIR"] = "/private/scratch",
            ["PATH"] = "/usr/bin:/bin",
            ["DOTNET_EnableDiagnostics"] = "0",
            ["PROVIDER_API_KEY"] = "secret-value",
            ["BOOTSTRAP_PASSWORD"] = "secret-value",
            ["BACKUP_KEY"] = "secret-value",
            ["HOME"] = "/srv/secondbrain",
            ["LD_PRELOAD"] = "/untrusted/library.so",
            ["HTTP_PROXY"] = "http://untrusted/proxy",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["LISTEN_FDNAMES"] = "discarded-extra",
            ["path"] = "discarded-case-variant"
        };

        var scrubbed = ExtractorEnvironment.Scrub(inherited);

        Assert.Equal(6, scrubbed.Count);
        foreach (var name in new[] { "LISTEN_PID", "LISTEN_FDS", "NOTIFY_SOCKET", "TMPDIR", "PATH", "DOTNET_EnableDiagnostics" })
        {
            Assert.Equal(inherited[name], scrubbed[name]);
        }
        Assert.DoesNotContain("secret-value", scrubbed.Values);
        Assert.Equal("secret-value", inherited["PROVIDER_API_KEY"]); // Pure transformation leaves input untouched.
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("true")]
    public void ScrubAlwaysDisablesDiagnostics(string? inheritedValue)
    {
        var scrubbed = ExtractorEnvironment.Scrub(new Dictionary<string, string?>
        {
            ["DOTNET_EnableDiagnostics"] = inheritedValue
        });

        Assert.Single(scrubbed);
        Assert.Equal("0", scrubbed["DOTNET_EnableDiagnostics"]);
    }

    [Fact]
    public void ScrubDropsNullServiceValues()
    {
        var scrubbed = ExtractorEnvironment.Scrub(new Dictionary<string, string?>
        {
            ["NOTIFY_SOCKET"] = null,
            ["TMPDIR"] = null
        });

        Assert.Single(scrubbed);
        Assert.Equal("0", scrubbed["DOTNET_EnableDiagnostics"]);
    }
}
