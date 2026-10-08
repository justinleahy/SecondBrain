using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Configuration;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class SmokeTests
{
    [Fact]
    public async Task DaemonHealthReturnsOk()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MockProviderHostHealthReturnsOk()
    {
        await using var factory = new WebApplicationFactory<MockProvider.Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
