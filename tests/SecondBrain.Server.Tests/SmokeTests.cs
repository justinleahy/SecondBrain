using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Configuration;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class SmokeTests
{
    [Fact]
    public async Task DaemonHealthReturnsOk()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "secondbrain-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(host =>
                host.ConfigureServices(services =>
                    services.PostConfigure<SecondBrainOptions>(options => options.DataRoot = dataRoot)));
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
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
