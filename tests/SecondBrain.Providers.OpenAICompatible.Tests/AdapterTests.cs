using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Providers;
using SecondBrain.MockProvider;
using SecondBrain.Providers.OpenAICompatible;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

public sealed class Ready
{
    [Fact]
    public async Task MockProviderGreen()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var health = await context.Registry.TestAsync();
        Assert.Equal(3, health.Count);
        Assert.All(health.Values, result => Assert.True(result.IsHealthy, result.Detail));
        var models = await context.Registry.ListModelsAsync();
        Assert.Equal(new[] { "mock-chat", "mock-embed" }, models["test-local"]);
        Assert.True(context.Privacy.GetReadiness().IsReady);
    }
}

public sealed class AdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclaredNativeToolsWorkThroughNeutralChatClient(bool streaming)
    {
        await using var context = await ProviderTestContext.StartAsync();
        var binding = context.Registry.GetRole(ModelRole.Chat);
        var client = binding.Provider.GetChatClient(binding.Model.Model)!;
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((Func<string>)(() => "unused"), "mock_tool")],
            ToolMode = ChatToolMode.RequireSpecific("mock_tool"),
        };
        var calls = new List<FunctionCallContent>();
        if (streaming)
        {
            await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "call tool")], options))
                calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }
        else
        {
            var result = await client.GetResponseAsync([new(ChatRole.User, "call tool")], options);
            calls.AddRange(result.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        }
        Assert.Equal("mock_tool", Assert.Single(calls).Name);
        Assert.Equal(1, context.Mock.GetRequestCount("/v1/chat/completions"));
    }

    [Fact]
    public async Task ChatAndStreamingUseMicrosoftExtensionsAI()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var binding = context.Registry.GetRole(ModelRole.Chat);
        var client = Assert.IsAssignableFrom<IChatClient>(binding.Provider.GetChatClient(binding.Model.Model));
        var response = await client.GetResponseAsync([new(ChatRole.User, "hello")]);
        Assert.Equal(MockProviderApplication.ChatResponse, response.Text);
        var chunks = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "hello")]))
            chunks.Add(update.Text);
        Assert.Equal(MockProviderApplication.ChatResponse, string.Concat(chunks));
        Assert.True(chunks.Count >= 2);
        Assert.Equal(2, context.Mock.GetRequestCount("/v1/chat/completions"));
    }

    [Fact]
    public async Task EmbeddingsUseMicrosoftExtensionsAIAndConfiguredDimensions()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var candidate = ProviderTestContext.CreateOptions(context.Endpoint);
        candidate.Models.Embed!.Dimensions = 8;
        context.Options.Reload(candidate);
        var binding = context.Registry.GetRole(ModelRole.Embed);
        var generator = Assert.IsAssignableFrom<IEmbeddingGenerator<string, Embedding<float>>>(binding.Provider.GetEmbeddingGenerator(binding.Model.Model));
        var result = await generator.GenerateAsync(["first", "second"]);
        Assert.Equal(2, result.Count);
        Assert.All(result, embedding => Assert.Equal(8, embedding.Vector.Length));
        Assert.Equal(new[] { .1f, .2f, .3f, .4f, .1f, .2f, .3f, .4f }, result[0].Vector.ToArray());
        Assert.Equal(8, binding.Model.Limits.EmbedDimensions);
    }

    [Fact]
    public async Task TwoNamedProvidersUseTheSameNeutralRoleContract()
    {
        await using var context = await ProviderTestContext.StartAsync();
        await using var second = await ProviderTestContext.StartAsync();
        var options = ProviderTestContext.CreateOptions(context.Endpoint);
        options.Privacy.LocalOnly = false;
        options.Providers.Add("second-provider", new() { Kind = "openai_compatible", Endpoint = second.Endpoint.AbsoluteUri });
        options.Models.Enrich!.Provider = "second-provider";
        context.Options.Reload(options);
        Assert.True(context.Registry.GetRole(ModelRole.Chat).Provider.IsLocal);
        Assert.False(context.Registry.GetRole(ModelRole.Enrich).Provider.IsLocal);
        var lists = await context.Registry.ListModelsAsync();
        Assert.Equal(2, lists.Count);
        Assert.Equal(lists["test-local"], lists["second-provider"]);
        Assert.All((await context.Registry.TestAsync()).Values, result => Assert.True(result.IsHealthy));
    }

    [Fact]
    public async Task SuccessfulReloadReplacesClientsAndAppliesLimits()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var previous = context.Registry.GetRole(ModelRole.Chat);
        var options = ProviderTestContext.CreateOptions(context.Endpoint);
        options.Models.Chat!.Limits = new() { ContextTokens = 4096, MaxOutputTokens = 512 };
        context.Options.Reload(options);
        var next = context.Registry.GetRole(ModelRole.Chat);
        Assert.NotSame(previous.Provider, next.Provider);
        Assert.Equal(4096, next.Model.Limits.ContextTokens);
        Assert.Equal(512, next.Model.Limits.MaxOutputTokens);
    }

    [Theory]
    [InlineData(ModelRole.Chat)]
    [InlineData(ModelRole.Enrich)]
    [InlineData(ModelRole.Embed)]
    public void UnresolvedRequiredLimitsAreRejected(ModelRole role)
    {
        var catalog = new ModelCatalog();
        var unresolved = catalog.Resolve("any-provider", "unknown-model");
        Assert.Throws<InvalidOperationException>(() => ModelCatalog.ValidateRole(role, unresolved));
    }

    [Fact]
    public void UnknownEnrichmentModelCanUseExplicitLimitsWithoutInventedCapabilities()
    {
        var catalog = new ModelCatalog();
        var resolved = catalog.Resolve("any-provider", "user-selected-model", new() { ContextTokens = 8192, MaxOutputTokens = 512 });
        ModelCatalog.ValidateRole(ModelRole.Enrich, resolved);
        Assert.False(resolved.Capabilities.Tools);
        Assert.Throws<InvalidOperationException>(() => ModelCatalog.ValidateRole(ModelRole.Chat, resolved));
    }

    [Fact]
    public void InvalidOrMissingRoleIsRejectedBeforeAnyNetworkRequest()
    {
        var options = ProviderTestContext.CreateOptions(new("http://127.0.0.1:1/v1"));
        options.Models.Chat!.Provider = "missing";
        Assert.Throws<InvalidOperationException>(() => ProviderRegistry.ValidateConfiguration(options, new()));
        options.Models.Chat = null;
        Assert.Throws<InvalidOperationException>(() => ProviderRegistry.ValidateConfiguration(options, new()));
    }

    [Fact]
    public async Task BindingDoesNotExposeSdkOrAllowModelOverride()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var binding = context.Registry.GetRole(ModelRole.Chat);
        var client = binding.Provider.GetChatClient(binding.Model.Model)!;
        Assert.Null(client.GetService(typeof(OpenAI.Chat.ChatClient)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new(ChatRole.User, "hello")], new() { ModelId = "different-model" }));
        Assert.Equal(0, context.Mock.TotalRequests);
    }

    [Fact]
    public async Task ResolvedOutputAndBatchLimitsRejectExcessBeforeSend()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var candidate = ProviderTestContext.CreateOptions(context.Endpoint);
        candidate.Models.Chat!.Limits = candidate.Models.Chat.Limits with { MaxOutputTokens = 16 };
        candidate.Models.Embed!.Limits = candidate.Models.Embed.Limits with { EmbedBatchMax = 1 };
        context.Options.Reload(candidate);
        var chat = context.Registry.GetRole(ModelRole.Chat);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Provider.GetChatClient(chat.Model.Model)!
            .GetResponseAsync([new(ChatRole.User, "hello")], new() { MaxOutputTokens = 17 }));
        var embed = context.Registry.GetRole(ModelRole.Embed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => embed.Provider.GetEmbeddingGenerator(embed.Model.Model)!
            .GenerateAsync(["one", "two"]));
        Assert.Equal(0, context.Mock.TotalRequests);
    }

    [Fact]
    public async Task ClientCreationRejectsUnresolvedLimits()
    {
        await using var context = await ProviderTestContext.StartAsync();
        Assert.Throws<InvalidOperationException>(() => context.Registry.GetRole(ModelRole.Chat).Provider.GetChatClient("unresolved"));
        Assert.Throws<InvalidOperationException>(() => context.Registry.GetRole(ModelRole.Embed).Provider.GetEmbeddingGenerator("unresolved"));
        Assert.Equal(0, context.Mock.TotalRequests);
    }

    [Fact]
    public async Task RawSdkOptionsCallbacksAreRefusedWithoutExposingInnerClients()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var called = false;
        var chat = context.Registry.GetRole(ModelRole.Chat);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Provider.GetChatClient(chat.Model.Model)!
            .GetResponseAsync([new(ChatRole.User, "hello")], new()
            {
                RawRepresentationFactory = _ => { called = true; return new OpenAI.Chat.ChatCompletionOptions(); },
            }));
        var embed = context.Registry.GetRole(ModelRole.Embed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => embed.Provider.GetEmbeddingGenerator(embed.Model.Model)!
            .GenerateAsync(["hello"], new()
            {
                RawRepresentationFactory = _ => { called = true; return new OpenAI.Embeddings.EmbeddingGenerationOptions(); },
            }));
        Assert.False(called);
        Assert.Equal(0, context.Mock.TotalRequests);
    }

    [Fact]
    public void PrevalidationRejectsUnusedUnsupportedProvider()
    {
        var options = ProviderTestContext.CreateOptions(new("http://127.0.0.1:1/v1"));
        options.Providers.Add("unused", new() { Kind = "unavailable", Endpoint = "http://127.0.0.1:2/v1" });
        Assert.Throws<InvalidOperationException>(() => ProviderRegistry.ValidateConfiguration(options, new()));
    }

    [Fact]
    public async Task RejectedProviderReloadPreservesApprovedPrivacyAndClients()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var previous = context.Registry.GetRole(ModelRole.Chat);
        var options = ProviderTestContext.CreateOptions(context.Endpoint);
        options.Providers.Add("unused", new() { Kind = "unavailable", Endpoint = "http://127.0.0.1:2/v1" });
        Assert.Throws<InvalidOperationException>(() => context.Options.Reload(options));
        Assert.Same(previous, context.Registry.GetRole(ModelRole.Chat));
        Assert.True(context.Privacy.Evaluate(ModelRole.Chat, previous.Provider).Allowed);
        Assert.True((await previous.Provider.HealthCheckAsync()).IsHealthy);
    }

    [Fact]
    public async Task MockAlsoRunsInWebApplicationFactoryTestServer()
    {
        await using var factory = new WebApplicationFactory<SecondBrain.MockProvider.Program>();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/models");
        response.EnsureSuccessStatusCode();
        Assert.Contains("mock-chat", await response.Content.ReadAsStringAsync());
    }
}
