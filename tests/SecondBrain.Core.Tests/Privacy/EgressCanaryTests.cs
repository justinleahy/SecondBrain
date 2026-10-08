using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Problems;
using SecondBrain.Core.Providers;
using Xunit;

namespace SecondBrain.Core.Tests.Privacy;

public sealed class EgressCanaryTests
{
    [Fact]
    public async Task CanaryBlocksProviderCallsAndNextFailureClearsState()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.EgressCanary = true;
        options.Privacy.CanaryTarget = $"127.0.0.1:{port}";
        var monitor = new PrivacyTestOptionsMonitor(options);
        var time = new FakeTimeProvider();
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver(), time);
        var canary = new EgressCanary(policy, monitor, time);
        var binding = new PrivacyTestBinding();

        Assert.Equal(ProblemTypes.EgressUnverified, policy.Evaluate(ModelRole.Chat, binding).ProblemType);
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            Assert.Equal(CanaryState.Reachable, await canary.RunOnceAsync());
            using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(5));
            var exception = Assert.Throws<PrivacyPolicyException>(() => policy.ValidateRequest(ModelRole.Chat, binding,
                new Uri("http://model.test:8123/v1/chat/completions")));
            Assert.Equal(ProblemTypes.EgressUnverified, exception.ProblemType);
            Assert.False(policy.GetReadiness().IsReady);
            Assert.Equal(time.GetUtcNow(), policy.GetReadiness().LastCheckedAt);
        }
        finally
        {
            listener.Stop();
        }

        Assert.Equal(CanaryState.Blocked, await canary.RunOnceAsync());
        Assert.True(policy.Evaluate(ModelRole.Chat, binding).Allowed);
        Assert.True(policy.GetReadiness().IsReady);
    }

    [Fact]
    public async Task CanaryTimeoutCountsAsBlockedAfterTwoSeconds()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.EgressCanary = true;
        var monitor = new PrivacyTestOptionsMonitor(options);
        var time = new FakeTimeProvider();
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver(), time);
        var connector = new WaitingConnector();
        var canary = new EgressCanary(policy, monitor, time, connector);
        var check = canary.RunOnceAsync().AsTask();
        await connector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(check.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(CanaryState.Blocked, await check.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(CanaryState.Blocked, policy.CanaryState);
    }

    [Fact]
    public async Task RunLoopChecksAtStartupAndHourly()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.EgressCanary = true;
        var monitor = new PrivacyTestOptionsMonitor(options);
        var time = new FakeTimeProvider();
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver(), time);
        var connector = new SuccessfulConnector();
        var canary = new EgressCanary(policy, monitor, time, connector);
        using var cancellation = new CancellationTokenSource();
        var loop = canary.RunAsync(cancellation.Token);
        Assert.Equal(1, connector.Calls);
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(1, connector.Calls);
        time.Advance(TimeSpan.FromMinutes(1));
        await connector.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, connector.Calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task ReachableCanaryOnlyInformsWhenLocalOnlyIsOff()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.LocalOnly = false;
        options.Privacy.EgressCanary = true;
        var monitor = new PrivacyTestOptionsMonitor(options);
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver());
        var canary = new EgressCanary(policy, monitor, connector: new SuccessfulConnector());
        Assert.Equal(CanaryState.Reachable, await canary.RunOnceAsync());
        Assert.True(policy.Evaluate(ModelRole.Chat, new PrivacyTestBinding()).Allowed);
        Assert.True(policy.GetReadiness().IsReady);
        Assert.Equal(CanaryState.Reachable, policy.GetReadiness().CanaryState);
    }

    [Fact]
    public async Task TargetReloadResetsPreviousCanaryResult()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.EgressCanary = true;
        var monitor = new PrivacyTestOptionsMonitor(options);
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver());
        var canary = new EgressCanary(policy, monitor, connector: new SuccessfulConnector());
        var originalConfiguration = policy.CaptureCanaryConfiguration();
        await canary.RunOnceAsync();
        var changed = PrivacyTestConfiguration.Trusted();
        changed.Privacy.EgressCanary = true;
        changed.Privacy.CanaryTarget = "192.0.2.1:443";
        monitor.Reload(changed);
        Assert.Equal(CanaryState.Unknown, policy.CanaryState);
        Assert.Null(policy.GetReadiness().LastCheckedAt);
        Assert.Equal(ProblemTypes.EgressUnverified, policy.GetReadiness().ProblemType);
        policy.RecordCanaryResult(originalConfiguration, CanaryState.Blocked);
        Assert.Equal(CanaryState.Unknown, policy.CanaryState);
    }

    [Fact]
    public async Task ReturningToPreviousTargetDoesNotPublishOldInFlightCheck()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.EgressCanary = true;
        var monitor = new PrivacyTestOptionsMonitor(options);
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver());
        var connector = new ControlledConnector();
        var canary = new EgressCanary(policy, monitor, connector: connector);
        var check = canary.RunOnceAsync().AsTask();
        await connector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var changed = PrivacyTestConfiguration.Trusted();
        changed.Privacy.EgressCanary = true;
        changed.Privacy.CanaryTarget = "192.0.2.1:443";
        monitor.Reload(changed);
        monitor.Reload(options);
        connector.Complete.TrySetResult();
        Assert.Equal(CanaryState.Reachable, await check.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(CanaryState.Unknown, policy.CanaryState);
        Assert.Null(policy.GetReadiness().LastCheckedAt);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPublishBlockedState()
    {
        var options = PrivacyTestConfiguration.Trusted();
        options.Privacy.EgressCanary = true;
        var monitor = new PrivacyTestOptionsMonitor(options);
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver());
        var connector = new WaitingConnector();
        var canary = new EgressCanary(policy, monitor, connector: connector);
        using var cancellation = new CancellationTokenSource();
        var check = canary.RunOnceAsync(cancellation.Token).AsTask();
        await connector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(CanaryState.Unknown, policy.CanaryState);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("http://localhost:443")]
    [InlineData("localhost:0")]
    [InlineData("localhost:443?x=1")]
    [InlineData("user:secret@localhost:443")]
    public void CanaryRequiresExplicitHostAndPort(string target) =>
        Assert.Throws<PrivacyPolicyException>(() => EgressCanary.ParseTarget(target));

    [Theory]
    [InlineData("1.1.1.1:443", "1.1.1.1", 443)]
    [InlineData("[::1]:8123", "::1", 8123)]
    [InlineData("public.test:443", "public.test", 443)]
    public void CanaryParsesIpv4Ipv6AndHostnames(string target, string host, int port) =>
        Assert.Equal((host, port), EgressCanary.ParseTarget(target));

    private sealed class WaitingConnector : ICanaryConnector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ControlledConnector : ICanaryConnector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Complete.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class SuccessfulConnector : ICanaryConnector
    {
        public int Calls { get; private set; }
        public TaskCompletionSource SecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 2) { SecondCall.TrySetResult(); }
            return ValueTask.CompletedTask;
        }
    }
}
