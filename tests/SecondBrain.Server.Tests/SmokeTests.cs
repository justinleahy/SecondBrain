using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class SmokeTests
{
    [Fact]
    public async Task DaemonHealthReturnsOk()
    {
        await using var factory = new WebApplicationFactory<global::Program>();
        using var client = factory.CreateClient();
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
