using SecondBrain.Core.Configuration;
using Xunit;

namespace SecondBrain.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void ConfigurationDoesNotChooseAProviderForTheUser()
    {
        var options = new SecondBrainOptions();
        Assert.Empty(options.Providers);
        Assert.Null(options.Models.Chat);
        Assert.Null(options.Models.Embed);
    }
}
