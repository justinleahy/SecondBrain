using SecondBrain.Architecture.Tests.Support;
using Xunit;

namespace SecondBrain.Architecture.Tests;

/// <summary>
/// <c>brain serve</c> maps daemon startup failures to exit codes by matching the daemon's stderr
/// (<c>InstalledDaemonCommands</c>). These rules keep the matched names and texts present in the daemon-side code.
/// </summary>
public sealed class DaemonStderrContractTests
{
    [Fact]
    [Trait("Rule", "AR-17")]
    public void DataRootLockedExceptionExistsInASourceAssembly()
    {
        var matches = Layers.SourceAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name == "DataRootLockedException")
            .ToArray();
        Assert.NotEmpty(matches);
    }

    [Theory]
    [Trait("Rule", "AR-17")]
    [InlineData("data root is already locked", true)]
    [InlineData("Root mode is incorrect", false)]
    [InlineData("Root ownership is incorrect", false)]
    [InlineData("Key ring must be daemon-owned", false)]
    [InlineData("must run as a non-root", false)]
    public void MatchedStderrTextAppearsOutsideTheCli(string literal, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var files = Repository.SourceFiles("src")
            .Where(path => !path.StartsWith("src/SecondBrain.Cli/", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(files);
        Assert.True(files.Any(path => Repository.ReadAllText(path).Contains(literal, comparison)),
            $"No src/**/*.cs file outside src/SecondBrain.Cli contains \"{literal}\", which brain serve matches on daemon stderr.");
    }
}
