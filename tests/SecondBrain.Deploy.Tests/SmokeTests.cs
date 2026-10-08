using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void DeploymentEntrypointsHaveExpectedAssemblyNames()
    {
        Assert.Equal("SecondBrain.Server", typeof(global::Program).Assembly.GetName().Name);
        Assert.Equal("brain", typeof(Cli.Program).Assembly.GetName().Name);
        Assert.Equal("SecondBrain.Extractor", typeof(Extractor.Program).Assembly.GetName().Name);
    }
}
