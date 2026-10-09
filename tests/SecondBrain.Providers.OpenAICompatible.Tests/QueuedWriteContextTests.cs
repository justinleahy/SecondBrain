using System.Net;
using System.Reflection;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

public sealed class QueuedWriteContextTests
{
    [Fact]
    public async Task ConnectionCreatedForCancelledDiscoveryCannotAuthorizeQueuedInference()
    {
        await using var context = await ProviderTestContext.StartAsync();
        var dns = new PausedConnectDns();
        var policy = new ObservedPolicy(context.Privacy);
        using var factory = new PolicyHttpClientFactory(policy, dns);
        // Force the production shared pool to queue the second request behind the
        // first connection attempt, without changing the adapter's public configuration.
        var sockets = (SocketsHttpHandler)typeof(PolicyHttpClientFactory)
            .GetField("sockets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(factory)!;
        sockets.MaxConnectionsPerServer = 1;
        using var discovery = factory.CreateClient(ModelRole.Enrich, context.Registry.GetProvider("test-local"), discovery: true);
        using var inference = factory.CreateClient(ModelRole.Chat, context.Registry.GetRole(ModelRole.Chat).Provider);
        using var cancellation = new CancellationTokenSource();
        var first = discovery.GetAsync(context.Endpoint.AbsoluteUri + "/models", cancellation.Token);
        await dns.Connecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Post, context.Endpoint.AbsoluteUri + "/chat/completions") { Content = new StringContent("{}") };
        var second = inference.SendAsync(request);
        await policy.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var candidate = ProviderTestContext.CreateOptions(context.Endpoint);
        candidate.Providers["other"] = new() { Kind = "openai_compatible", Endpoint = context.Endpoint.AbsoluteUri };
        candidate.Models.Chat!.Provider = "other";
        context.Options.Reload(candidate);
        cancellation.Cancel();
        dns.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAsync<PrivacyPolicyException>(() => second);
        Assert.Equal(0, context.Mock.TotalRequests);
    }

    private sealed class PausedConnectDns : IDnsResolver
    {
        private int calls;
        public TaskCompletionSource Connecting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                Connecting.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return [IPAddress.Loopback];
        }
    }

    private sealed class ObservedPolicy(PrivacyPolicy inner) : IProviderEgressPolicy
    {
        private int chatValidations;
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ValidateRequest(ModelRole role, IProviderBinding binding, Uri uri, bool discovery = false)
        {
            inner.ValidateRequest(role, binding, uri, discovery);
            if (role == ModelRole.Chat && Interlocked.Increment(ref chatValidations) == 2) Queued.TrySetResult();
        }
        public ValueTask AdmitWrite(ModelRole role, IProviderBinding binding, Uri uri, IPAddress address, bool discovery, Func<ValueTask> initiate)
            => inner.AdmitWrite(role, binding, uri, address, discovery, initiate);
        public bool IsLocalEndpoint(Uri? endpoint) => inner.IsLocalEndpoint(endpoint);
        public void VerifyResolvedAddresses(Uri endpoint, IReadOnlyList<IPAddress> addresses) => inner.VerifyResolvedAddresses(endpoint, addresses);
        public void VerifyConnectionAddresses(string host, int port, IReadOnlyList<IPAddress> addresses) => inner.VerifyConnectionAddresses(host, port, addresses);
        public IDisposable RegisterConfigurationValidator(Action<SecondBrainOptions> validator) => inner.RegisterConfigurationValidator(validator);
    }
}
