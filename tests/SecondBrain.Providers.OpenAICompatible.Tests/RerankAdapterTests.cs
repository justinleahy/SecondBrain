using System.Text.Json;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using SecondBrain.MockProvider;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

public sealed class RerankAdapterTests
{
    [Fact]
    public async Task ConfiguredRerankerUsesPolicyTransportAndRejectsStaleBinding()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var options = ProviderTestContext.CreateOptions(context.Endpoint);
        options.Models.Rerank = new() { Provider = "test-local", Model = "mock-rerank", Limits = new() { ContextTokens = 8192, MaxOutputTokens = 1024 } };
        context.Options.Reload(options);
        var role = context.Registry.GetRole(ModelRole.Rerank);
        var provider = Assert.IsAssignableFrom<IRerankProvider>(role.Provider.GetRerankProvider(role.Model.Model));
        var result = await provider.RerankAsync(role.Model.Model, "question", ["first", "second"]);
        Assert.Equal(new[] { new RerankScore(0, 0), new RerankScore(1, 1) }, result);
        Assert.Equal(1, context.Mock.GetRequestCount("/v1/rerank"));
        context.Options.Reload(ProviderTestContext.CreateOptions(context.Endpoint));
        await Assert.ThrowsAsync<PrivacyPolicyException>(() => provider.RerankAsync(role.Model.Model, "question", ["first"]).AsTask());
        Assert.Equal(1, context.Mock.GetRequestCount("/v1/rerank"));
    }

    [Theory]
    [InlineData(MockFault.Malformed)]
    [InlineData(MockFault.Redirect)]
    public async Task MalformedOrRedirectResponseDoesNotProduceScores(MockFault fault)
    {
        await using var context = await ProviderTestContext.StartAsync();
        var options = ProviderTestContext.CreateOptions(context.Endpoint);
        options.Models.Rerank = new() { Provider = "test-local", Model = "mock-rerank", Limits = new() { ContextTokens = 8192, MaxOutputTokens = 1024 } };
        context.Options.Reload(options);
        var role = context.Registry.GetRole(ModelRole.Rerank);
        context.Mock.Fault = fault;
        var exception = await Record.ExceptionAsync(() => role.Provider.GetRerankProvider(role.Model.Model)!.RerankAsync(role.Model.Model, "question", ["first"]).AsTask());
        Assert.True(exception is JsonException or HttpRequestException or PrivacyPolicyException);
        Assert.Equal(1, context.Mock.GetRequestCount("/v1/rerank"));
    }
}
