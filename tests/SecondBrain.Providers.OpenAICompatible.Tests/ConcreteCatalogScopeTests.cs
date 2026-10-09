using SecondBrain.Core.Providers;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

public sealed class ConcreteCatalogScopeTests
{
    [Theory]
    [InlineData("openai", "gpt-4o")]
    [InlineData("openai_compatible", "gpt-4o")]
    [InlineData("test-local", "mock-chat")]
    [InlineData("other", "text-embedding-3-small")]
    public void FamiliarNamesHaveNoImplicitDeclarations(string provider, string model)
    {
        var resolved = new ModelCatalog().Resolve(provider, model);
        Assert.False(resolved.Capabilities.Tools);
        Assert.Null(resolved.Limits.ContextTokens);
        Assert.Null(resolved.Limits.EmbedDimensions);
        Assert.Throws<InvalidOperationException>(() => ModelCatalog.ValidateRole(ModelRole.Chat, resolved));
    }

    [Fact]
    public void SameModelOnDifferentProvidersHasIndependentReviewedDeclarations()
    {
        var catalog = new ModelCatalog();
        catalog.Register("reviewed-a", "gpt-4o", new() { Tools = true }, new() { ContextTokens = 100, MaxOutputTokens = 20 });
        catalog.Register("reviewed-b", "gpt-4o", new() { Streaming = true }, new() { ContextTokens = 300, MaxOutputTokens = 50 });
        var a = catalog.Resolve("reviewed-a", "gpt-4o");
        var b = catalog.Resolve("reviewed-b", "gpt-4o", declaredCapabilities: new() { Tools = false });
        Assert.True(a.Capabilities.Tools);
        Assert.False(b.Capabilities.Tools);
        Assert.True(b.Capabilities.Streaming);
        Assert.Equal(100, a.Limits.ContextTokens);
        Assert.Equal(300, b.Limits.ContextTokens);
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve("reviewed-a", "gpt-4o", declaredCapabilities: new() { Tools = false }));
        var other = catalog.Resolve("other", "gpt-4o", new() { ContextTokens = 600, MaxOutputTokens = 90 }, new() { Tools = false });
        Assert.False(other.Capabilities.Tools);
        Assert.Equal(600, other.Limits.ContextTokens);
    }

    [Fact]
    public async Task DiscoveryCannotBorrowAnotherProvidersReviewedModel()
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Catalog.Register("another-provider", "discovered", new() { Tools = true }, new() { ContextTokens = 100, MaxOutputTokens = 20 });
        var model = await context.Registry.GetProvider("test-local").ResolveAsync("discovered");
        Assert.False(model.Capabilities.Tools);
        // This binding's own explicit limits are allowed; the other provider's limits are not.
        Assert.Equal(8192, model.Limits.ContextTokens);
    }
}
