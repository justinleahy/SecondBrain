using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Problems;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Network;
using SecondBrain.MockProvider;
using SecondBrain.Providers.OpenAICompatible;
using Xunit;

namespace SecondBrain.Core.Tests.Providers;

public sealed class Transport
{
    [Fact]
    public async Task DiscoveryClientCannotBeUsedForInference()
    {
        await using var context = await ProviderTestContext.StartAsync();
        using var client = context.Factory.CreateClient(ModelRole.Chat, context.Registry.GetProvider("test-local"), discovery: true);
        using var content = new StringContent("{}");
        var failure = await Assert.ThrowsAsync<PrivacyPolicyException>(() => client.PostAsync(
            new Uri(context.Endpoint.AbsoluteUri + "/chat/completions"), content));
        Assert.Equal(ProblemTypes.PrivacyPolicy, failure.ProblemType);
        Assert.Equal(0, context.Mock.TotalRequests);
    }

    [Fact]
    public async Task RedirectIsFailure()
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Mock.Fault = MockFault.Redirect;
        context.Mock.RedirectTarget = new("https://example.com/must-not-receive-provider-data");
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(async () =>
            await context.Registry.GetProvider("test-local").ListModelsAsync());
        Assert.EndsWith("provider-redirect", failure.ProblemType);
        Assert.False(failure.Retryable);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, failure.StatusCode);
        Assert.Equal(1, context.Mock.TotalRequests);
    }

    [Fact]
    public async Task RedirectFromChatSdkIsAlsoTypedAndNotRetried()
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Mock.Fault = MockFault.Redirect;
        var binding = context.Registry.GetRole(ModelRole.Chat);
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            binding.Provider.GetChatClient(binding.Model.Model)!.GetResponseAsync([new(ChatRole.User, "hello")]));
        Assert.EndsWith("provider-redirect", failure.ProblemType);
        Assert.Equal(1, context.Mock.GetRequestCount("/v1/chat/completions"));
    }

    [Fact]
    public async Task RedirectNeverContactsEvenAnAllowedSecondListener()
    {
        await using var context = await ProviderTestContext.StartAsync();
        await using var second = await ProviderTestContext.StartAsync();
        context.Mock.Fault = MockFault.Redirect;
        context.Mock.RedirectTarget = new(second.Endpoint.AbsoluteUri + "/models");
        await Assert.ThrowsAsync<ProviderRequestException>(async () => await context.Registry.GetProvider("test-local").ListModelsAsync());
        Assert.Equal(1, context.Mock.TotalRequests);
        Assert.Equal(0, second.Mock.TotalRequests);
    }

    [Fact]
    public async Task RequestPolicyDetectsBindingChangeDuringDnsAwaitBeforeSend()
    {
        var resolver = new SwitchableResolver();
        await using var context = await ProviderTestContext.StartAsync(resolver);
        var binding = new MutableBinding(context.Endpoint);
        Assert.True(context.Privacy.Evaluate(ModelRole.Chat, binding).Allowed);
        resolver.BeforeResolve = () => binding.Endpoint = new("https://example.com/v1");
        using var client = context.Factory.CreateClient(ModelRole.Chat, binding);
        var failure = await Assert.ThrowsAsync<PrivacyPolicyException>(() => client.GetAsync(new Uri(context.Endpoint.AbsoluteUri + "/models")));
        Assert.Equal(ProblemTypes.PrivacyPolicy, failure.ProblemType);
        Assert.Equal(0, context.Mock.TotalRequests);
    }

    [Fact]
    public async Task ChangedDnsIsRejectedOnPooledConnections()
    {
        var resolver = new SwitchableResolver();
        await using var context = await ProviderTestContext.StartAsync(resolver);
        var binding = context.Registry.GetProvider("test-local");
        await binding.ListModelsAsync();
        resolver.Addresses = [IPAddress.Parse("203.0.113.7")];
        var failure = await Assert.ThrowsAsync<PrivacyPolicyException>(async () => await binding.ListModelsAsync());
        Assert.Equal(ProblemTypes.PrivacyPolicy, failure.ProblemType);
        Assert.Equal(1, context.Mock.TotalRequests);
    }

    [Fact]
    public async Task AcceptedRepinningCannotReuseSocketToPreviousAddress()
    {
        var resolver = new SwitchableResolver();
        await using var context = await ProviderTestContext.StartAsync(resolver);
        await using var second = MockProviderApplication.Build([], builder => builder.WebHost.UseUrls($"http://[::1]:{context.Endpoint.Port}"));
        await second.StartAsync();
        var endpoint = new Uri($"http://pin-fixture.invalid:{context.Endpoint.Port}/v1");
        context.Options.Reload(ProviderTestContext.CreateOptions(endpoint));
        var previous = context.Registry.GetProvider("test-local");
        await previous.ListModelsAsync();
        Assert.Equal(1, context.Mock.TotalRequests);
        resolver.Addresses = [IPAddress.IPv6Loopback];
        context.Options.Reload(ProviderTestContext.CreateOptions(endpoint));
        // A retained turn client shares the same daemon handler and origin pool as new clients.
        await previous.ListModelsAsync();
        Assert.Equal(1, context.Mock.TotalRequests);
        Assert.Equal(1, second.Services.GetRequiredService<MockProviderState>().TotalRequests);
        await second.StopAsync();
    }

    [Fact]
    public async Task ConnectCallbackRechecksDnsAfterRequestCheck()
    {
        var resolver = new SwitchableResolver();
        await using var context = await ProviderTestContext.StartAsync(resolver);
        var sends = 0;
        resolver.BeforeResolve = () =>
        {
            if (++sends == 2) resolver.Addresses = [IPAddress.Parse("203.0.113.8")];
        };
        var failure = await Assert.ThrowsAsync<PrivacyPolicyException>(async () => await context.Registry.GetProvider("test-local").ListModelsAsync());
        Assert.Equal(ProblemTypes.PrivacyPolicy, failure.ProblemType);
        Assert.Equal(0, context.Mock.TotalRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectCallbackReevaluatesBindingAndCanaryAfterItsOwnDnsAwait(bool canaryChange)
    {
        var resolver = new SwitchableResolver();
        await using var context = await ProviderTestContext.StartAsync(resolver);
        var binding = new MutableBinding(context.Endpoint);
        if (canaryChange)
        {
            var options = ProviderTestContext.CreateOptions(context.Endpoint);
            options.Privacy.EgressCanary = true;
            context.Options.Reload(options);
            context.Privacy.RecordCanaryResult(context.Privacy.CaptureCanaryConfiguration(), CanaryState.Blocked);
        }
        var sends = 0;
        resolver.BeforeResolve = () =>
        {
            if (++sends != 2) return;
            if (canaryChange) context.Privacy.RecordCanaryResult(context.Privacy.CaptureCanaryConfiguration(), CanaryState.Reachable);
            else binding.Endpoint = new("https://example.com/v1");
        };
        using var client = context.Factory.CreateClient(ModelRole.Chat, binding);
        var failure = await Assert.ThrowsAsync<PrivacyPolicyException>(() => client.GetAsync(new Uri(context.Endpoint.AbsoluteUri + "/models")));
        Assert.Equal(canaryChange ? ProblemTypes.EgressUnverified : ProblemTypes.PrivacyPolicy, failure.ProblemType);
        Assert.Equal(0, context.Mock.TotalRequests);
    }

    [Fact]
    public async Task RateLimitIsTypedRetryableAndSdkMakesOnlyOneAttempt()
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Mock.Fault = MockFault.RateLimit;
        var binding = context.Registry.GetRole(ModelRole.Chat);
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(() => binding.Provider.GetChatClient(binding.Model.Model)!
            .GetResponseAsync([new(ChatRole.User, "hello")]));
        Assert.True(failure.Retryable);
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal(1, context.Mock.TotalRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedDiscoveryAndChatAreTypedFailures(bool chat)
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Mock.Fault = MockFault.Malformed;
        var binding = context.Registry.GetRole(ModelRole.Chat);
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(async () =>
        {
            if (chat) await binding.Provider.GetChatClient(binding.Model.Model)!.GetResponseAsync([new(ChatRole.User, "hello")]);
            else await binding.Provider.ListModelsAsync();
        });
        Assert.EndsWith("provider-malformed", failure.ProblemType);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public async Task TimeoutIsTypedRetryable()
    {
        await using var context = await ProviderTestContext.StartAsync();
        context.Mock.Fault = MockFault.Timeout;
        using var binding = new OpenAICompatibleBinding("test-local", context.Endpoint, ModelRole.Chat,
            new ShortTimeoutFactory(context.Factory), context.Privacy, context.Catalog);
        var failure = await Assert.ThrowsAsync<ProviderRequestException>(async () => await binding.ListModelsAsync());
        Assert.EndsWith("provider-timeout", failure.ProblemType);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public async Task SuccessfulCanaryBlocksActualProviderTransportAndLaterFailureClearsIt()
    {
        await using var context = await ProviderTestContext.StartAsync();
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var target = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var options = ProviderTestContext.CreateOptions(context.Endpoint);
        options.Privacy.EgressCanary = true;
        options.Privacy.CanaryTarget = target;
        context.Options.Reload(options);
        var canary = new EgressCanary(context.Privacy, context.Options, connector: new TcpCanaryConnector());
        Assert.Equal(CanaryState.Reachable, await canary.RunOnceAsync());
        var failure = await Assert.ThrowsAsync<PrivacyPolicyException>(async () => await context.Registry.GetProvider("test-local").ListModelsAsync());
        Assert.Equal(ProblemTypes.EgressUnverified, failure.ProblemType);
        Assert.False(context.Privacy.GetReadiness().IsReady);
        Assert.Equal(0, context.Mock.TotalRequests);
        listener.Stop();
        Assert.Equal(CanaryState.Blocked, await canary.RunOnceAsync());
        await context.Registry.GetProvider("test-local").ListModelsAsync();
        Assert.True(context.Privacy.GetReadiness().IsReady);
        Assert.Equal(1, context.Mock.TotalRequests);
    }

    private sealed class ShortTimeoutFactory(IPolicyHttpClientFactory inner) : IPolicyHttpClientFactory
    {
        public HttpClient CreateClient(ModelRole role, IProviderBinding binding, bool discovery = false)
        {
            var client = inner.CreateClient(role, binding, discovery);
            client.Timeout = TimeSpan.FromMilliseconds(100);
            return client;
        }
    }

    private sealed class SwitchableResolver : IDnsResolver
    {
        public IPAddress[] Addresses { get; set; } = [IPAddress.Loopback];
        public Action? BeforeResolve { get; set; }
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            BeforeResolve?.Invoke();
            return ValueTask.FromResult(Addresses);
        }
    }

    private sealed class MutableBinding(Uri endpoint) : IProviderBinding
    {
        public string ProviderName => "test-local";
        public Uri? Endpoint { get; set; } = endpoint;
        public bool IsLocal => true;
        public ValueTask<ResolvedModel> ResolveAsync(string model, ModelLimits? limitsOverrides = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IChatClient? GetChatClient(string model) => null;
        public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string model) => null;
    }
}
