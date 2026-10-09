using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Problems;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Configuration;
using SecondBrain.Infrastructure.Security;
using SecondBrain.MockProvider;
using SecondBrain.Providers.OpenAICompatible;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

/// <summary>A reload that repins an origin while its connection is being established or used.</summary>
public sealed class ConnectionPinRaces
{
    // Discovery validations, in order: request before DNS, request after DNS, connect before
    // dialing, connect after dialing, then one per write. A GET writes its headers once.
    private const int BeforeDial = 3;
    private const int AfterDial = 4;

    [Theory]
    [InlineData(BeforeDial)]
    [InlineData(AfterDial)]
    [InlineData(5)]
    public async Task RepinAcceptedDuringConnectionNeverSendsToRemovedAddress(int validation)
    {
        await using var fixture = await PinFixture.StartAsync();
        fixture.Hook.At(validation, () =>
        {
            fixture.Resolver.Addresses = [IPAddress.IPv6Loopback];
            fixture.ReloadPinned();
        });
        var failure = await Assert.ThrowsAsync<PrivacyPolicyException>(async () => await fixture.Binding.ListModelsAsync());
        Assert.Equal(ProblemTypes.PrivacyPolicy, failure.ProblemType);
        Assert.Equal(new[] { IPAddress.IPv6Loopback }, fixture.Context.Privacy.GetPinnedAddresses(fixture.Endpoint));
        Assert.Equal(0, fixture.Context.Mock.TotalRequests);
        Assert.Equal(0, fixture.Ipv6Mock.TotalRequests);
    }

    [Fact]
    public async Task OriginRemovedAfterConnectRefusesBeforeFirstWrite()
    {
        await using var fixture = await PinFixture.StartAsync();
        fixture.Hook.At(AfterDial, () => fixture.Context.Options.Reload(ProviderTestContext.CreateOptions(fixture.Context.Endpoint)));
        var failure = await Assert.ThrowsAsync<PrivacyPolicyException>(async () => await fixture.Binding.ListModelsAsync());
        Assert.Equal(ProblemTypes.PrivacyPolicy, failure.ProblemType);
        Assert.Equal(0, fixture.Context.Mock.TotalRequests);
    }

    [Fact]
    public async Task UnchangedPinReloadDuringConnectionStillSends()
    {
        await using var fixture = await PinFixture.StartAsync();
        fixture.Hook.At(AfterDial, fixture.ReloadPinned);
        Assert.Equal(new[] { "mock-chat", "mock-embed" }, await fixture.Binding.ListModelsAsync());
        Assert.True(fixture.Hook.Validations > AfterDial, "The write-time check did not run.");
        Assert.Equal(1, fixture.Context.Mock.TotalRequests);
        Assert.Equal(0, fixture.Ipv6Mock.TotalRequests);
    }

    [Fact]
    public async Task PolicyTransportSendsExactlyHttp11()
    {
        await using var context = await ProviderTestContext.StartAsync();
        using var client = context.Factory.CreateClient(ModelRole.Enrich, context.Registry.GetProvider("test-local"), discovery: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(context.Endpoint.AbsoluteUri + "/models"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpVersion.Version11, response.Version);
        Assert.Equal(1, context.Mock.TotalRequests);
    }

    private sealed class PinFixture : IAsyncDisposable
    {
        private readonly WebApplication ipv6;
        private readonly PolicyHttpClientFactory factory;

        private PinFixture(ProviderTestContext context, WebApplication ipv6, PinResolver resolver)
        {
            Context = context;
            this.ipv6 = ipv6;
            Resolver = resolver;
            Endpoint = new Uri($"http://pin-fixture.invalid:{context.Endpoint.Port}/v1");
            ReloadPinned();
            Hook = new(context.Privacy);
            factory = new(Hook, resolver);
            Binding = new("test-local", Endpoint, ModelRole.Enrich, factory, Hook, context.Catalog);
        }

        public ProviderTestContext Context { get; }
        public PinResolver Resolver { get; }
        public Uri Endpoint { get; }
        public ValidationHook Hook { get; }
        public OpenAICompatibleBinding Binding { get; }
        public MockProviderState Ipv6Mock => ipv6.Services.GetRequiredService<MockProviderState>();

        public static async Task<PinFixture> StartAsync()
        {
            var resolver = new PinResolver();
            var context = await ProviderTestContext.StartAsync(resolver);
            var ipv6 = MockProviderApplication.Build([], builder => builder.WebHost.UseUrls($"http://[::1]:{context.Endpoint.Port}"));
            await ipv6.StartAsync();
            return new(context, ipv6, resolver);
        }

        public void ReloadPinned() => Context.Options.Reload(ProviderTestContext.CreateOptions(Endpoint));

        public async ValueTask DisposeAsync()
        {
            Binding.Dispose();
            factory.Dispose();
            await ipv6.StopAsync();
            await ipv6.DisposeAsync();
            await Context.DisposeAsync();
        }
    }

    private sealed class PinResolver : IDnsResolver
    {
        public IPAddress[] Addresses { get; set; } = [IPAddress.Loopback];
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Addresses);
    }

    private sealed class ValidationHook(PrivacyPolicy policy) : IProviderEgressPolicy
    {
        private int validations;
        private int triggerAt;
        private Action? trigger;

        public int Validations => Volatile.Read(ref validations);
        public void At(int validation, Action action) => (triggerAt, trigger) = (validation, action);

        public ValueTask AdmitWrite(ModelRole role, IProviderBinding binding, Uri requestUri, IPAddress address,
            bool discovery, Func<ValueTask> initiateWrite)
        {
            ValidateRequest(role, binding, requestUri, discovery);
            return policy.AdmitWrite(role, binding, requestUri, address, discovery, initiateWrite);
        }
        public bool IsLocalEndpoint(Uri? endpoint) => policy.IsLocalEndpoint(endpoint);
        public void ValidateRequest(ModelRole role, IProviderBinding binding, Uri requestUri, bool discovery = false)
        {
            policy.ValidateRequest(role, binding, requestUri, discovery);
            if (Interlocked.Increment(ref validations) == triggerAt) trigger?.Invoke();
        }
        public void VerifyResolvedAddresses(Uri endpoint, IReadOnlyList<IPAddress> addresses) => policy.VerifyResolvedAddresses(endpoint, addresses);
        public void VerifyConnectionAddresses(string host, int port, IReadOnlyList<IPAddress> addresses) => policy.VerifyConnectionAddresses(host, port, addresses);
        public IDisposable RegisterConfigurationValidator(Action<SecondBrainOptions> validator) => policy.RegisterConfigurationValidator(validator);
    }
}

/// <summary>Reviewed capability declarations for models outside the adapter catalog, loaded from YAML.</summary>
public sealed class ConfiguredModelCapabilities : IDisposable
{
    private const string ServedChatModel = "Qwen/Qwen3-8B";
    private readonly string root = Path.Combine(UnixPath.Canonicalize(Path.GetTempPath()), "secondbrain-capabilities-" + Guid.NewGuid().ToString("N"));

    public ConfiguredModelCapabilities()
    {
        Directory.CreateDirectory(Path.Combine(root, "incoming"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private SecondBrainOptions Load(Uri endpoint, string chat, string? embed = null) =>
        new YamlConfigurationLoader(environment: _ => null).LoadText($$"""
            data_root: {{Path.Combine(root, "data")}}
            providers:
              vllm: { kind: openai_compatible, endpoint: '{{endpoint.AbsoluteUri}}' }
              other: { kind: openai_compatible, endpoint: '{{endpoint.AbsoluteUri}}' }
            models:
              chat: {{chat}}
              enrich: { provider: vllm, model: mock-chat, limits: { context_tokens: 8192, max_output_tokens: 1024 } }
              embed: {{embed ?? "{ provider: vllm, model: mock-embed, limits: { embed_dimensions: 4, embed_max_input_tokens: 8192, embed_batch_max: 32 } }"}}
            privacy:
              local_only: true
              egress_canary: false
              trusted_services: ['{{endpoint.GetLeftPart(UriPartial.Authority)}}']
            server:
              listeners: [{ scheme: http, bind: 127.0.0.1, port: 7171 }]
              hosts: [127.0.0.1]
              origins: [http://127.0.0.1:7171]
            sources:
              allowed_roots: [{{Path.Combine(root, "incoming")}}]
              incoming_root: {{Path.Combine(root, "incoming")}}
            """);

    private static string Chat(string model, string? capabilities) =>
        $"{{ provider: vllm, model: '{model}', limits: {{ context_tokens: 8192, max_output_tokens: 1024 }}" +
        (capabilities is null ? "" : $", capabilities: {capabilities}") + " }";

    [Fact]
    public async Task UnknownChatModelWithDeclaredToolsIsAcceptedThroughConfiguration()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var options = Load(context.Endpoint, Chat(ServedChatModel, "{ tools: true, streaming: true }"));
        context.Options.Reload(options);
        var chat = context.Registry.GetRole(ModelRole.Chat);
        Assert.Equal(ServedChatModel, chat.Model.Model);
        Assert.True(chat.Model.Capabilities.Tools);
        Assert.True(chat.Model.Capabilities.Streaming);
        Assert.False(chat.Model.Capabilities.StructuredOutput);
        Assert.Equal(8192, chat.Model.Limits.ContextTokens);
        Assert.NotNull(chat.Provider.GetChatClient(ServedChatModel));
        Assert.True((await chat.Provider.ResolveAsync(ServedChatModel)).Capabilities.Tools);
        // The declaration belongs to the configured model, never to other IDs on the endpoint.
        Assert.False((await chat.Provider.ResolveAsync("another-served-model")).Capabilities.Tools);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ streaming: true }")]
    [InlineData("{ tools: false, streaming: true }")]
    public async Task UnknownChatModelWithoutDeclaredToolsIsRejectedAndReloadKeepsClients(string? capabilities)
    {
        await using var context = await ProviderTestContext.StartAsync();
        var previous = context.Registry.GetRole(ModelRole.Chat);
        var options = Load(context.Endpoint, Chat(ServedChatModel, capabilities));
        Assert.Throws<InvalidOperationException>(() => ProviderRegistry.ValidateConfiguration(options, context.Catalog));
        Assert.Throws<InvalidOperationException>(() => context.Options.Reload(options));
        Assert.Same(previous, context.Registry.GetRole(ModelRole.Chat));
        Assert.True((await previous.Provider.HealthCheckAsync()).IsHealthy);
    }

    [Theory]
    [InlineData("mock-chat", "{ tools: false }")]
    [InlineData("gpt-4o", "{ structured_output: false }")]
    public async Task DeclarationsContradictingTheCatalogAreRejected(string model, string capabilities)
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Catalog.Register("vllm", model, new() { Tools = true, Streaming = true, StructuredOutput = true },
            new() { ContextTokens = 8192, MaxOutputTokens = 1024 });
        var options = Load(context.Endpoint, Chat(model, capabilities));
        Assert.Throws<InvalidOperationException>(() => ProviderRegistry.ValidateConfiguration(options, context.Catalog));
    }

    [Fact]
    public async Task DeclarationsAgreeingWithTheCatalogAreAccepted()
    {
        await using var context = await ProviderTestContext.StartAsync();
        ProviderRegistry.ValidateConfiguration(Load(context.Endpoint, Chat("gpt-4o", "{ tools: true, streaming: true, structured_output: true }")), context.Catalog);
        ProviderRegistry.ValidateConfiguration(Load(context.Endpoint, Chat("mock-chat", "{ tools: true }")), context.Catalog);
    }

    [Fact]
    public async Task EmbeddingDeclarationsDoNotReplaceEmbeddingLimits()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var chat = Chat("mock-chat", "{ tools: true }");
        Assert.Throws<InvalidOperationException>(() => ProviderRegistry.ValidateConfiguration(
            Load(context.Endpoint, chat, "{ provider: vllm, model: served-embed, capabilities: { tools: true } }"), context.Catalog));
        ProviderRegistry.ValidateConfiguration(Load(context.Endpoint, chat,
            "{ provider: vllm, model: served-embed, limits: { embed_dimensions: 4, embed_max_input_tokens: 512, embed_batch_max: 8 }, capabilities: { tools: true } }"),
            context.Catalog);
        Assert.Throws<InvalidOperationException>(() => ProviderRegistry.ValidateConfiguration(
            Load(context.Endpoint, chat, "{ provider: vllm, model: text-embedding-3-small, capabilities: { tools: true } }"), context.Catalog));
    }

    [Fact]
    public async Task FallbackCarriesItsOwnDeclaration()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var undeclared = "{ provider: vllm, model: mock-chat, capabilities: { tools: true }, limits: { context_tokens: 8192, max_output_tokens: 1024 }, fallback: { provider: vllm, model: served-fallback, limits: { context_tokens: 4096, max_output_tokens: 256 } } }";
        Assert.Throws<InvalidOperationException>(() => ProviderRegistry.ValidateConfiguration(Load(context.Endpoint, undeclared), context.Catalog));
        var declared = undeclared.Replace("max_output_tokens: 256 }", "max_output_tokens: 256 }, capabilities: { tools: true }", StringComparison.Ordinal);
        context.Options.Reload(Load(context.Endpoint, declared));
        Assert.True(context.Registry.GetRole(ModelRole.Chat).Fallback!.Model.Capabilities.Tools);
    }

    [Fact]
    public async Task SameFamiliarModelHasIndependentYamlDeclarationsAcrossProvidersAndFallbacks()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var chat = "{ provider: vllm, model: gpt-4o, capabilities: { tools: true, streaming: true, structured_output: true }, limits: { context_tokens: 8192, max_output_tokens: 1024 }, fallback: { provider: other, model: gpt-4o, capabilities: { tools: true, streaming: false, structured_output: false }, limits: { context_tokens: 4096, max_output_tokens: 256 } } }";
        context.Options.Reload(Load(context.Endpoint, chat));
        var primary = context.Registry.GetRole(ModelRole.Chat);
        Assert.True(primary.Model.Capabilities.StructuredOutput);
        Assert.False(primary.Fallback!.Model.Capabilities.StructuredOutput);
        Assert.False(primary.Fallback.Model.Capabilities.Streaming);
        Assert.Equal(8192, primary.Model.Limits.ContextTokens);
        Assert.Equal(4096, primary.Fallback.Model.Limits.ContextTokens);
        Assert.Equal(256, primary.Fallback.Model.Limits.MaxOutputTokens);
        var pins = context.Privacy.GetPinnedAddresses(context.Endpoint);
        var invalid = Load(context.Endpoint, chat.Replace("tools: true, streaming: false", "tools: false, streaming: false", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => context.Options.Reload(invalid));
        Assert.Same(primary, context.Registry.GetRole(ModelRole.Chat));
        Assert.Equal(pins, context.Privacy.GetPinnedAddresses(context.Endpoint));
        Assert.True(context.Privacy.GetReadiness().IsReady);
    }

    [Fact]
    public async Task NullCapabilitiesAreRejectedByConfigurationValidation()
    {
        await using var context = await ProviderTestContext.StartAsync();
        Assert.Throws<ConfigurationException>(() => Load(context.Endpoint,
            $"{{ provider: vllm, model: '{ServedChatModel}', capabilities: null }}"));
    }
}

/// <summary>Model discovery consumes a bounded, deadline-limited provider response.</summary>
public sealed class BoundedDiscovery
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedModelListIsTypedMalformed(bool declaredLength)
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Mock.ModelsResponse = Respond(PaddedModelList(OpenAICompatibleBinding.MaxDiscoveryResponseBytes + 1), declaredLength);
        await AssertMalformedAsync(context);
    }

    [Fact]
    public async Task ModelListAtTheByteLimitIsAccepted()
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Mock.ModelsResponse = Respond(PaddedModelList(OpenAICompatibleBinding.MaxDiscoveryResponseBytes), declaredLength: false);
        Assert.Equal(new[] { "mock-chat" }, await context.Registry.GetProvider("test-local").ListModelsAsync());
    }

    [Theory]
    [InlineData(OpenAICompatibleBinding.MaxDiscoveredModels, true)]
    [InlineData(OpenAICompatibleBinding.MaxDiscoveredModels + 1, false)]
    public async Task ModelCountIsBounded(int count, bool accepted)
    {
        await using var context = await ProviderTestContext.StartAsync();
        var ids = Enumerable.Range(0, count).Select(index => "model-" + index).ToArray();
        context.Mock.ModelsResponse = Respond(ModelList(ids), declaredLength: true);
        if (accepted) Assert.Equal(ids, await context.Registry.GetProvider("test-local").ListModelsAsync());
        else await AssertMalformedAsync(context);
    }

    [Theory]
    [InlineData(OpenAICompatibleBinding.MaxModelIdLength, true)]
    [InlineData(OpenAICompatibleBinding.MaxModelIdLength + 1, false)]
    public async Task ModelIdLengthIsBounded(int length, bool accepted)
    {
        await using var context = await ProviderTestContext.StartAsync();
        var id = new string('m', length);
        context.Mock.ModelsResponse = Respond(ModelList(["mock-chat", id]), declaredLength: false);
        if (accepted) Assert.Equal(new[] { "mock-chat", id }, await context.Registry.GetProvider("test-local").ListModelsAsync());
        else await AssertMalformedAsync(context);
    }

    [Fact]
    public async Task SlowBodyAfterHeadersIsTypedTimeout()
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Mock.ModelsResponse = async http =>
        {
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync("{\"data\":[", http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);
            try { await Task.Delay(Timeout.Infinite, http.RequestAborted); }
            catch (OperationCanceledException) { }
        };
        using var binding = new OpenAICompatibleBinding("test-local", context.Endpoint, ModelRole.Enrich,
            new TimeoutFactory(context.Factory, TimeSpan.FromSeconds(1)), context.Privacy, context.Catalog);
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(async () => await binding.ListModelsAsync());
        Assert.EndsWith("provider-timeout", failure.ProblemType);
        Assert.True(failure.Retryable);
        Assert.False((await binding.HealthCheckAsync()).IsHealthy);
    }

    private static async Task AssertMalformedAsync(ProviderTestContext context)
    {
        var binding = context.Registry.GetProvider("test-local");
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(async () => await binding.ListModelsAsync());
        Assert.EndsWith("provider-malformed", failure.ProblemType);
        Assert.False(failure.Retryable);
        var health = await binding.HealthCheckAsync();
        Assert.False(health.IsHealthy);
        Assert.EndsWith("provider-malformed", health.Detail);
    }

    private static byte[] ModelList(IEnumerable<string> ids)
        => Encoding.UTF8.GetBytes("{\"object\":\"list\",\"data\":[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"object\":\"model\"}}")) + "]}");

    // A syntactically valid list, padded to an exact size, so only the byte bound can reject it.
    private static byte[] PaddedModelList(int size)
    {
        var prefix = Encoding.ASCII.GetBytes("{\"data\":[{\"id\":\"mock-chat\"}],\"padding\":\"");
        var suffix = Encoding.ASCII.GetBytes("\"}");
        var body = new byte[size];
        prefix.CopyTo(body, 0);
        body.AsSpan(prefix.Length, size - prefix.Length - suffix.Length).Fill((byte)'x');
        suffix.CopyTo(body, size - suffix.Length);
        return body;
    }

    // Without a declared length, flushed writes make Kestrel use chunked transfer encoding.
    private static Func<HttpContext, Task> Respond(byte[] body, bool declaredLength) => async http =>
    {
        http.Response.ContentType = "application/json";
        if (declaredLength) http.Response.ContentLength = body.Length;
        try
        {
            for (var offset = 0; offset < body.Length; offset += 64 * 1024)
            {
                await http.Response.Body.WriteAsync(body.AsMemory(offset, Math.Min(64 * 1024, body.Length - offset)), http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The client stops reading once a bound is exceeded.
        }
    };

    private sealed class TimeoutFactory(IPolicyHttpClientFactory inner, TimeSpan timeout) : IPolicyHttpClientFactory
    {
        public HttpClient CreateClient(ModelRole role, IProviderBinding binding, bool discovery = false)
        {
            var client = inner.CreateClient(role, binding, discovery);
            client.Timeout = timeout;
            return client;
        }
    }
}
