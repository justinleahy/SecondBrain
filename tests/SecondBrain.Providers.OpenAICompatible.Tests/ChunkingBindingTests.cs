using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Providers;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

public sealed class ChunkingBindingTests
{
    [Fact]
    public async Task ChunkSpecificSpaceRetainsProviderIdentityAndRejectsOversizedInputBounds()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var role = context.Registry.GetRole(ModelRole.Embed);
        var fingerprint = EmbeddingFingerprint.Create(role.Provider.ProviderName, role.Provider.Endpoint!.AbsoluteUri,
            role.Model.Model, role.Model.Limits.EmbedDimensions!.Value, new(64, 128, 8));
        Assert.Same(role.Provider, context.Registry.GetEmbeddingProvider(role.Provider.ProviderName, role.Model.Model, role.Model.Limits.EmbedDimensions.Value, fingerprint));
        Assert.Throws<InvalidOperationException>(() => context.Registry.GetEmbeddingProvider(role.Provider.ProviderName, role.Model.Model, role.Model.Limits.EmbedDimensions.Value, new string('f', 64) + ":128-64-8"));
        var invalid = ProviderTestContext.CreateOptions(context.Endpoint);
        var embed = invalid.Models.Embed!;
        embed.Limits = embed.Limits with { EmbedMaxInputTokens = 128 };
        Assert.Throws<InvalidOperationException>(() => context.Options.Reload(invalid));
        invalid.Chunking = new ChunkingOptions { MaxTokens = 128, TargetTokens = 64, OverlapTokens = 8 };
        context.Options.Reload(invalid);
        Assert.Equal(128, context.Registry.GetRole(ModelRole.Embed).Model.Limits.EmbedMaxInputTokens);
    }
}
