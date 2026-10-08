using SecondBrain.Core.Configuration;
using Xunit;

namespace SecondBrain.Server.Tests.Support;

public sealed class FactoryLifecycleTests
{
    [Fact]
    public async Task ConstructorFailureStopsListenersAndRemovesTemporaryDirectories()
    {
        string? dataDirectory = null;
        string? socketPath = null;
        Uri? providerEndpoint = null;
        var expected = new InvalidOperationException("Deliberate fixture configuration failure.");

        var actual = Assert.Throws<InvalidOperationException>(() => new LaneDWebFactory(options =>
        {
            dataDirectory = Path.GetDirectoryName(options.DataRoot);
            socketPath = options.Extractor.SocketPath;
            providerEndpoint = new Uri(options.Providers["mock"].Endpoint!);
            Assert.True(File.Exists(socketPath));
            throw expected;
        }));

        Assert.Same(expected, actual);
        Assert.False(actual.Data.Contains("FixtureCleanupFailure"));
        Assert.NotNull(dataDirectory);
        Assert.NotNull(socketPath);
        Assert.NotNull(providerEndpoint);
        Assert.False(Directory.Exists(dataDirectory));
        Assert.False(Directory.Exists(Path.GetDirectoryName(socketPath)));
        await AssertProviderStoppedAsync(providerEndpoint);
    }

    [Fact]
    public async Task FaultedExtractorStillDisposesOtherResources()
    {
        var factory = new LaneDWebFactory();
        var dataDirectory = factory.Store.DirectoryPath;
        var socketDirectory = Path.GetDirectoryName(factory.ExtractorSocket)!;
        var providerEndpoint = new Uri(factory.MockProvider.Urls.Single());
        try
        {
            // The worker's shutdown unlink fails on a directory, producing a real task fault.
            File.Delete(factory.ExtractorSocket);
            Directory.CreateDirectory(factory.ExtractorSocket);

            var failure = await Record.ExceptionAsync(() => factory.DisposeAsync().AsTask());
            Assert.NotNull(failure);
            Assert.False(Directory.Exists(dataDirectory));
            Assert.False(Directory.Exists(socketDirectory));
            await AssertProviderStoppedAsync(providerEndpoint);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    private static async Task AssertProviderStoppedAsync(Uri endpoint)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri(endpoint, "/health")));
    }
}
