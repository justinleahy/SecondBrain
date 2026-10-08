using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Problems;
using SecondBrain.Core.Providers;
using Xunit;

namespace SecondBrain.Core.Tests.Privacy;

public sealed class PrivacyPolicyTests
{
    [Fact]
    public void RefusesHostedAtStartup()
    {
        var options = PrivacyTestConfiguration.Hosted(localOnly: true);
        var exception = Assert.Throws<PrivacyPolicyException>(() => new PrivacyPolicy(new PrivacyTestOptionsMonitor(options)));
        Assert.Equal(ProblemTypes.PrivacyPolicy, exception.ProblemType);
    }

    [Fact]
    public void RefusesHostedAtReload()
    {
        var original = PrivacyTestConfiguration.Hosted(localOnly: false);
        var monitor = new PrivacyTestOptionsMonitor(original);
        using var policy = new PrivacyPolicy(monitor);
        var exception = Assert.Throws<PrivacyPolicyException>(() => monitor.Reload(PrivacyTestConfiguration.Hosted(localOnly: true)));
        Assert.Equal(ProblemTypes.PrivacyPolicy, exception.ProblemType);
        Assert.False(policy.GetReadiness().IsReady);

        // A loader retains its accepted options when validation rejects the candidate.
        monitor.Reload(original);
        Assert.True(policy.GetReadiness().IsReady);
    }

    [Fact]
    public void RefusesHostedAtRequest()
    {
        var monitor = new PrivacyTestOptionsMonitor(PrivacyTestConfiguration.Trusted());
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver());
        var binding = new PrivacyTestBinding();
        Assert.True(policy.Evaluate(ModelRole.Chat, binding).Allowed);

        // Simulates a binding change after evaluation and before the transport send.
        binding.Endpoint = new Uri("https://provider.example/v1");
        var exception = Assert.Throws<PrivacyPolicyException>(() =>
            policy.ValidateRequest(ModelRole.Chat, binding, new Uri("https://provider.example/v1/chat/completions")));
        Assert.Equal(ProblemTypes.PrivacyPolicy, exception.ProblemType);
    }

    [Fact]
    public void RefusesHostedFallbackAtStartup()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Providers["hosted"] = new ProviderOptions { Kind = "openai_compatible", Endpoint = "https://provider.example/v1" };
        options.Models.Chat!.Fallback = new ModelBindingOptions { Provider = "hosted", Model = "secondary" };
        Assert.Throws<PrivacyPolicyException>(() => new PrivacyPolicy(new PrivacyTestOptionsMonitor(options), new PrivacyTestDnsResolver()));
    }

    [Fact]
    public void LoopbackDoesNotAutomaticallyCountAsLocal()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.TrustedServices.Clear();
        options.Providers["test"].Endpoint = "http://127.0.0.1:8123/v1";
        Assert.Throws<PrivacyPolicyException>(() => new PrivacyPolicy(new PrivacyTestOptionsMonitor(options)));
    }

    [Theory]
    [InlineData("https://model.test:8123/v1")]
    [InlineData("http://other.test:8123/v1")]
    [InlineData("http://model.test:8124/v1")]
    public void LocalityRequiresExactSchemeHostAndPort(string endpoint)
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.LocalOnly = false;
        using var policy = new PrivacyPolicy(new PrivacyTestOptionsMonitor(options), new PrivacyTestDnsResolver());
        Assert.False(policy.IsLocalEndpoint(new Uri(endpoint)));
        Assert.True(policy.IsLocalEndpoint(new Uri("http://model.test:8123/other-path")));
    }

    [Theory]
    [InlineData("http://model.test:8123/v1")]
    [InlineData("http://user:secret@model.test:8123")]
    [InlineData("http://model.test:8123?x=1")]
    [InlineData("http://model.test:8123#part")]
    public void TrustedServicesAcceptOnlyOrigins(string trusted)
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.TrustedServices = [trusted];
        Assert.Throws<PrivacyPolicyException>(() => new PrivacyPolicy(new PrivacyTestOptionsMonitor(options), new PrivacyTestDnsResolver()));
    }

    [Fact]
    public void TrustedDnsIsPinnedUntilSuccessfulReload()
    {
        var monitor = new PrivacyTestOptionsMonitor(PrivacyTestConfiguration.Trusted());
        var resolver = new PrivacyTestDnsResolver();
        using var policy = new PrivacyPolicy(monitor, resolver);
        var endpoint = new Uri("http://model.test:8123/v1");
        resolver.Addresses = [IPAddress.Parse("127.0.0.2")];

        Assert.Throws<PrivacyPolicyException>(() => policy.VerifyResolvedAddresses(endpoint, resolver.Addresses));
        Assert.Throws<PrivacyPolicyException>(() => policy.VerifyConnectionAddresses("model.test", 8123, resolver.Addresses));
        Assert.Equal(IPAddress.Loopback, Assert.Single(policy.GetPinnedAddresses(endpoint)));

        monitor.Reload(PrivacyTestConfiguration.Trusted());
        policy.VerifyResolvedAddresses(endpoint, resolver.Addresses);
        policy.VerifyConnectionAddresses("model.test", 8123, resolver.Addresses);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), Assert.Single(policy.GetPinnedAddresses(endpoint)));
    }

    [Fact]
    public void EveryDnsAddressMustMatchPins()
    {
        using var policy = new PrivacyPolicy(new PrivacyTestOptionsMonitor(PrivacyTestConfiguration.Trusted()), new PrivacyTestDnsResolver());
        var endpoint = new Uri("http://model.test:8123/v1");
        Assert.Throws<PrivacyPolicyException>(() => policy.VerifyResolvedAddresses(endpoint, []));
        Assert.Throws<PrivacyPolicyException>(() => policy.VerifyResolvedAddresses(endpoint, [IPAddress.Loopback, IPAddress.Parse("203.0.113.12")]));
        policy.VerifyResolvedAddresses(endpoint, [IPAddress.Loopback]);
    }

    [Fact]
    public void PinnedAddressObjectsCannotBeMutatedByResolverOrCaller()
    {
        var address = IPAddress.Parse("fe80::1%1");
        var resolver = new PrivacyTestDnsResolver { Addresses = [address] };
        using var policy = new PrivacyPolicy(new PrivacyTestOptionsMonitor(PrivacyTestConfiguration.Trusted()), resolver);
        var endpoint = new Uri("http://model.test:8123/v1");
        address.ScopeId = 2;
        var exposed = Assert.Single(policy.GetPinnedAddresses(endpoint));
        Assert.Equal(1, exposed.ScopeId);
        exposed.ScopeId = 3;
        Assert.Equal(1, Assert.Single(policy.GetPinnedAddresses(endpoint)).ScopeId);
    }

    [Fact]
    public void RequestMustUseConfiguredOrigin()
    {
        using var policy = new PrivacyPolicy(new PrivacyTestOptionsMonitor(PrivacyTestConfiguration.Trusted()), new PrivacyTestDnsResolver());
        var binding = new PrivacyTestBinding();
        Assert.Throws<PrivacyPolicyException>(() => policy.ValidateRequest(ModelRole.Chat, binding,
            new Uri("https://model.test:8123/v1/chat/completions")));
        policy.ValidateRequest(ModelRole.Chat, binding, new Uri("http://model.test:8123/v1/chat/completions"));
    }

    [Fact]
    public void UnvalidatedInPlaceConfigurationChangeIsRefused()
    {
        var options = PrivacyTestConfiguration.Trusted();
        using var policy = new PrivacyPolicy(new PrivacyTestOptionsMonitor(options), new PrivacyTestDnsResolver());
        var binding = new PrivacyTestBinding();
        Assert.True(policy.Evaluate(ModelRole.Chat, binding).Allowed);
        options.Privacy.LocalOnly = false;
        var decision = policy.Evaluate(ModelRole.Chat, binding);
        Assert.False(decision.Allowed);
        Assert.Equal(ProblemTypes.PrivacyPolicy, decision.ProblemType);
    }

    [Fact]
    public void InProcessExecutionRequiresExplicitAdapterKind()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Providers["test"] = new ProviderOptions { Kind = "in_process" };
        options.Privacy.TrustedServices.Clear();
        using var policy = new PrivacyPolicy(new PrivacyTestOptionsMonitor(options));
        Assert.True(policy.Evaluate(ModelRole.Chat, new PrivacyTestBinding { Endpoint = null }).Allowed);
    }

    [Fact]
    public void DiscoveryAllowsConfiguredUnboundLocalProvider()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Models.Chat = null;
        using var policy = new PrivacyPolicy(new PrivacyTestOptionsMonitor(options), new PrivacyTestDnsResolver());
        var binding = new PrivacyTestBinding();
        Assert.False(policy.Evaluate(ModelRole.Chat, binding).Allowed);
        policy.ValidateRequest(ModelRole.Chat, binding, new Uri("http://model.test:8123/v1/models"), discovery: true);
    }

    [Fact]
    public void FailedReloadLeavesOriginalDnsPins()
    {
        var original = PrivacyTestConfiguration.Trusted();
        var monitor = new PrivacyTestOptionsMonitor(original);
        var resolver = new PrivacyTestDnsResolver();
        using var policy = new PrivacyPolicy(monitor, resolver);
        var candidate = PrivacyTestConfiguration.Trusted();
        candidate.Providers["test"].Endpoint = "https://provider.example/v1";
        resolver.Addresses = [IPAddress.Parse("127.0.0.2")];
        Assert.Throws<PrivacyPolicyException>(() => monitor.Reload(candidate));
        monitor.SetWithoutNotification(original);
        Assert.Equal(IPAddress.Loopback, Assert.Single(policy.GetPinnedAddresses(new Uri("http://model.test:8123/v1"))));
    }

    [Fact]
    public void ValidationDoesNotActivateCandidate()
    {
        var monitor = new PrivacyTestOptionsMonitor(PrivacyTestConfiguration.Trusted());
        var resolver = new PrivacyTestDnsResolver();
        using var policy = new PrivacyPolicy(monitor, resolver);
        resolver.Addresses = [IPAddress.Parse("127.0.0.2")];
        policy.ValidateConfiguration(PrivacyTestConfiguration.Trusted());
        Assert.Equal(IPAddress.Loopback, Assert.Single(policy.GetPinnedAddresses(new Uri("http://model.test:8123/v1"))));
    }

    [Fact]
    public void RegisteredValidatorRejectionPreservesPinsAndCanary()
    {
        var original = PrivacyTestConfiguration.Trusted();
        original.Privacy.EgressCanary = true;
        var monitor = new PrivacyTestOptionsMonitor(original);
        var resolver = new PrivacyTestDnsResolver();
        using var policy = new PrivacyPolicy(monitor, resolver);
        policy.RecordCanaryResult(policy.CaptureCanaryConfiguration(), CanaryState.Blocked);
        var previouslyCheckedAt = policy.GetReadiness().LastCheckedAt;
        using var subscription = policy.RegisterConfigurationValidator(candidate =>
        {
            if (candidate.Models.Chat!.Model == "refused-model")
            {
                throw new InvalidOperationException("Candidate model limits are unresolved.");
            }
        });
        var candidate = PrivacyTestConfiguration.Trusted();
        candidate.Models.Chat!.Model = "refused-model";
        candidate.Privacy.EgressCanary = true;
        candidate.Privacy.CanaryTarget = "192.0.2.1:443";
        resolver.Addresses = [IPAddress.Parse("127.0.0.2")];
        Assert.Throws<InvalidOperationException>(() => monitor.Reload(candidate));
        monitor.SetWithoutNotification(original);

        Assert.Equal(1, resolver.Calls);
        Assert.Equal(IPAddress.Loopback, Assert.Single(policy.GetPinnedAddresses(new Uri("http://model.test:8123/v1"))));
        Assert.Equal(CanaryState.Blocked, policy.CanaryState);
        Assert.Equal(previouslyCheckedAt, policy.GetReadiness().LastCheckedAt);
    }

    [Fact]
    public void DisposedValidatorDoesNotRejectLaterReload()
    {
        var monitor = new PrivacyTestOptionsMonitor(PrivacyTestConfiguration.Trusted());
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver());
        var subscription = policy.RegisterConfigurationValidator(_ => throw new InvalidOperationException("Rejected."));
        subscription.Dispose();
        subscription.Dispose();
        monitor.Reload(PrivacyTestConfiguration.Trusted());
        Assert.True(policy.GetReadiness().IsReady);
    }
}

internal static class PrivacyTestConfiguration
{
    public static SecondBrainOptions Trusted()
    {
        var options = Hosted(localOnly: true);
        options.Providers["test"].Endpoint = "http://model.test:8123/v1";
        options.Privacy.TrustedServices = ["http://model.test:8123"];
        return options;
    }

    public static SecondBrainOptions Hosted(bool localOnly) => new()
    {
        Providers = new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["test"] = new() { Kind = "openai_compatible", Endpoint = "https://provider.example/v1" },
        },
        Models = new ModelRolesOptions { Chat = new ModelBindingOptions { Provider = "test", Model = "test-chat" } },
        Privacy = new PrivacyOptions { LocalOnly = localOnly, EgressCanary = false },
    };
}

internal sealed class PrivacyTestDnsResolver : IDnsResolver
{
    public IPAddress[] Addresses { get; set; } = [IPAddress.Loopback];
    public int Calls { get; private set; }
    public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        Calls++;
        return ValueTask.FromResult(Addresses.ToArray());
    }
}

internal sealed class PrivacyTestBinding : IProviderBinding
{
    public string ProviderName => "test";
    public Uri? Endpoint { get; set; } = new("http://model.test:8123/v1");
    public bool IsLocal => true;
    public ValueTask<ResolvedModel> ResolveAsync(string model, ModelLimits? limitsOverrides = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IChatClient? GetChatClient(string model) => null;
    public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string model) => null;
}

internal sealed class PrivacyTestOptionsMonitor(SecondBrainOptions current) : IOptionsMonitor<SecondBrainOptions>
{
    private Action<SecondBrainOptions, string?>? _listeners;
    public SecondBrainOptions CurrentValue { get; private set; } = current;
    public SecondBrainOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<SecondBrainOptions, string?> listener)
    {
        _listeners += listener;
        return new Subscription(() => _listeners -= listener);
    }

    public void Reload(SecondBrainOptions candidate)
    {
        CurrentValue = candidate;
        _listeners?.Invoke(candidate, null);
    }

    public void SetWithoutNotification(SecondBrainOptions candidate) => CurrentValue = candidate;
    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
