using System.Net;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using Xunit;

namespace SecondBrain.Core.Tests.Privacy;

public sealed class WriteAdmissionOrderingTests
{
    private static readonly Uri Request = new("http://model.test:8123/v1/chat/completions");

    [Fact]
    public async Task InitiationPrecedesConcurrentPublicationAndDoesNotHoldGateWhileAwaiting()
    {
        var monitor = new PrivacyTestOptionsMonitor(PrivacyTestConfiguration.Trusted());
        var dns = new PrivacyTestDnsResolver();
        using var policy = new PrivacyPolicy(monitor, dns);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var preparing = new ManualResetEventSlim();
        using var validator = policy.RegisterConfigurationValidator(_ => preparing.Set());
        var pendingSocket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = Task.Run(() => policy.AdmitWrite(ModelRole.Chat, new PrivacyTestBinding(), Request,
            IPAddress.Loopback, false, () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return new(pendingSocket.Task); }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        dns.Addresses = [IPAddress.IPv6Loopback];
        var publication = Task.Run(() => monitor.Reload(PrivacyTestConfiguration.Trusted()));
        // The reload cannot even acquire the publication guard while initiation is running.
        Assert.False(preparing.Wait(TimeSpan.FromMilliseconds(100)));
        release.Set();
        var operation = await admission.WaitAsync(TimeSpan.FromSeconds(5));
        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        Assert.Equal(new[] { IPAddress.IPv6Loopback }, policy.GetPinnedAddresses(Request));
        var initiated = false;
        Assert.Throws<PrivacyPolicyException>(() => policy.AdmitWrite(ModelRole.Chat, new PrivacyTestBinding(), Request,
            IPAddress.Loopback, false, () => { initiated = true; return ValueTask.CompletedTask; }));
        Assert.False(initiated);
        pendingSocket.SetResult();
        await operation;
    }

    [Fact]
    public async Task SameThreadReloadCannotPublishInsideInitiation()
    {
        var original = PrivacyTestConfiguration.Trusted();
        var monitor = new PrivacyTestOptionsMonitor(original);
        var dns = new PrivacyTestDnsResolver();
        using var policy = new PrivacyPolicy(monitor, dns);
        await policy.AdmitWrite(ModelRole.Chat, new PrivacyTestBinding(), Request, IPAddress.Loopback, false, () =>
        {
            dns.Addresses = [IPAddress.IPv6Loopback];
            Assert.Throws<PrivacyPolicyException>(() => monitor.Reload(PrivacyTestConfiguration.Trusted()));
            monitor.SetWithoutNotification(original);
            return ValueTask.CompletedTask;
        });
        Assert.Equal(new[] { IPAddress.Loopback }, policy.GetPinnedAddresses(Request));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("origin")]
    [InlineData("canary")]
    public void PublishedReplacementRefusesLaterInitiation(string change)
    {
        var monitor = new PrivacyTestOptionsMonitor(PrivacyTestConfiguration.Trusted());
        using var policy = new PrivacyPolicy(monitor, new PrivacyTestDnsResolver());
        var replacement = PrivacyTestConfiguration.Trusted();
        if (change == "role") replacement.Models.Chat = null;
        if (change == "origin") replacement.Providers["test"].Endpoint = "http://model.test:8123/changed";
        if (change == "canary") replacement.Privacy.EgressCanary = true;
        monitor.Reload(replacement);
        var initiated = false;
        Assert.Throws<PrivacyPolicyException>(() => policy.AdmitWrite(ModelRole.Chat, new PrivacyTestBinding(), Request,
            IPAddress.Loopback, false, () => { initiated = true; return ValueTask.CompletedTask; }));
        Assert.False(initiated);
    }
}
