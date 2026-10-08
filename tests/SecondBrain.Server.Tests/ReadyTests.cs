using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.Configuration;
using SecondBrain.Core.Providers;
using SecondBrain.MockProvider;
using SecondBrain.Providers.OpenAICompatible;
using SecondBrain.Server.Tests.Support;
using SecondBrain.Server.Http;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class Ready
{
    [Fact]
    public async Task MockProviderGreen()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var response = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("ready").GetBoolean());
        var components = json.GetProperty("components");
        foreach (var name in new[] { "migrations", "stores", "lock", "canary", "extractor", "provider:chat", "provider:enrich", "provider:embed" })
            Assert.True(components.GetProperty(name).GetProperty("ready").GetBoolean(), name);
        Assert.All(components.EnumerateObject(), component => Assert.True(component.Value.GetProperty("ready").GetBoolean(), component.Name));
        Assert.Contains("disabled", components.GetProperty("canary").GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(factory.MockProvider.Services.GetRequiredService<MockProviderState>().GetRequestCount("/v1/models") >= 3);
        Assert.True(File.Exists(factory.ExtractorSocket));
        Assert.True(factory.Services.GetRequiredService<SecondBrain.Core.Storage.IStorageStatus>().Ready);
        Assert.True(File.Exists(factory.ConfigPath));
    }

    [Fact]
    public async Task ProviderChecksRefreshAtFiveMinutesAndFailClosed()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var first = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var mock = factory.MockProvider.Services.GetRequiredService<MockProviderState>();
        var requests = mock.TotalRequests;
        using var cached = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.Equal(requests, mock.TotalRequests);
        await factory.MockProvider.StopAsync();
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        using var expired = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, expired.StatusCode);
        var json = await expired.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("components").GetProperty("provider:chat").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task InvalidModelReloadKeepsLastValidatedConfiguration()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var initial = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var previous = factory.Configuration.CurrentValue;
        var yaml = await File.ReadAllTextAsync(factory.ConfigPath);
        await File.WriteAllTextAsync(factory.ConfigPath, yaml.Replace("mock-chat", "unknown-model-without-capabilities", StringComparison.Ordinal));
        Assert.False(factory.Configuration.TryReload());
        Assert.Same(previous, factory.Configuration.CurrentValue);
        Assert.Same(previous, factory.Options.CurrentValue);
        using var current = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task ReloadDuringProviderProbeDoesNotCachePreviousBindingHealth()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        var registry = factory.Services.GetRequiredService<IProviderRegistry>();
        var delayed = new DelayedRegistry(registry);
        using var probe = new ProviderReadinessProbe(delayed, factory.Options, factory.Clock);
        var pending = probe.CheckAsync(ModelRole.Chat, CancellationToken.None).AsTask();
        await delayed.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = new YamlConfigurationLoader(environment: _ => null).Load(factory.ConfigPath);
        factory.Options.Update(next);
        delayed.Release.TrySetResult();
        Assert.False((await pending).Ready);
        Assert.True((await probe.CheckAsync(ModelRole.Chat, CancellationToken.None)).Ready);
    }

    [Fact]
    public async Task OptionsPublicationBeforeRegistryAcceptanceCannotServeCachedHealth()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        var registry = factory.Services.GetRequiredService<IProviderRegistry>();
        var delayed = new DelayedRegistry(registry);
        delayed.Release.TrySetResult();
        using var probe = new ProviderReadinessProbe(delayed, factory.Options, factory.Clock);
        Assert.True((await probe.CheckAsync(ModelRole.Chat, CancellationToken.None)).Ready);
        delayed.Accepted = false; // The monitor published, but the registry's reload callback is still preparing.
        Assert.False((await probe.CheckAsync(ModelRole.Chat, CancellationToken.None)).Ready);
    }

    private sealed class DelayedRegistry(IProviderRegistry registry) : IProviderRegistry
    {
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Accepted { get; set; } = true;
        public bool IsCurrentConfiguration(SecondBrainOptions options) => Accepted && registry.IsCurrentConfiguration(options);
        public IReadOnlyList<string> ProviderNames => registry.ProviderNames;
        public ProviderRoleBinding GetRole(ModelRole role) => registry.GetRole(role);
        public IProviderBinding GetProvider(string name) => registry.GetProvider(name);
        public ValueTask<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListModelsAsync(CancellationToken cancellationToken = default) => registry.ListModelsAsync(cancellationToken);
        public async ValueTask<IReadOnlyDictionary<ModelRole, ProviderHealthResult>> TestAsync(CancellationToken cancellationToken = default)
        {
            var results = await registry.TestAsync(cancellationToken);
            Observed.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return results;
        }
    }
}

public sealed class Qualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task VllmReady()
    {
        var endpoint = Environment.GetEnvironmentVariable("SECONDBRAIN_QUAL_VLLM_URL");
        if (string.IsNullOrWhiteSpace(endpoint)) return; // External qualification is deliberately opt-in.
        var chat = Environment.GetEnvironmentVariable("SECONDBRAIN_QUAL_VLLM_CHAT_MODEL");
        var embed = Environment.GetEnvironmentVariable("SECONDBRAIN_QUAL_VLLM_EMBED_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(chat), "Set SECONDBRAIN_QUAL_VLLM_CHAT_MODEL to the reviewed native-tools model.");
        Assert.False(string.IsNullOrWhiteSpace(embed), "Set SECONDBRAIN_QUAL_VLLM_EMBED_MODEL to the served embedding model.");
        var enrichSetting = Environment.GetEnvironmentVariable("SECONDBRAIN_QUAL_VLLM_ENRICH_MODEL");
        var enrich = string.IsNullOrWhiteSpace(enrichSetting) ? chat! : enrichSetting;
        static int ReadLimit(string suffix, int fallback) => int.TryParse(Environment.GetEnvironmentVariable("SECONDBRAIN_QUAL_VLLM_" + suffix), out var value) ? value : fallback;
        var origin = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var chatLimits = new ModelLimits { ContextTokens = ReadLimit("CONTEXT_WINDOW", 8192), MaxOutputTokens = ReadLimit("MAX_OUTPUT_TOKENS", 1024) };
        var embedLimits = new ModelLimits { EmbedDimensions = ReadLimit("DIMENSIONS", 4), EmbedMaxInputTokens = ReadLimit("MAX_INPUT_TOKENS", 8192), EmbedBatchMax = 32 };
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Providers = new() { ["vllm"] = new ProviderOptions { Kind = "openai_compatible", Endpoint = endpoint, Trusted = true } };
            options.Models.Chat = new ModelBindingOptions { Provider = "vllm", Model = chat! };
            options.Models.Enrich = new ModelBindingOptions { Provider = "vllm", Model = enrich };
            options.Models.Embed = new ModelBindingOptions { Provider = "vllm", Model = embed! };
            options.Privacy.TrustedServices = [origin];
        }, services =>
        {
            var catalog = new ModelCatalog();
            catalog.Register(chat!, new ModelCapabilities { Streaming = true, Tools = true }, chatLimits);
            catalog.Register(enrich, new ModelCapabilities { Streaming = true, Tools = true }, chatLimits);
            catalog.Register(embed!, new ModelCapabilities(), embedLimits);
            services.AddSingleton(catalog);
        });
        using var client = factory.CreatePrivateClient();
        using var response = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
