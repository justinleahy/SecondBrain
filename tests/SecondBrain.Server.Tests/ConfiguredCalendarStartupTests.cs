using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Sources;
using SecondBrain.Server.Sources;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ConfiguredCalendarStartupTests
{
    [Fact]
    public async Task ConfiguredSourceIsRegisteredBeforePollingAndUsesRealPurposeProtection()
    {
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Sources.CalendarAllowedOrigins = ["https://feed.test"];
            options.Sources.CalendarSubscriptions = [new() { Id = "shared", Name = "Shared", UrlReference = "${CALENDAR_TEST}", Enabled = false }];
        }, configurationEnvironment: name => name == "CALENDAR_TEST" ? "webcal://feed.test/private-test-token" : null);
        var repository = factory.Services.GetRequiredService<ICalendarSubscriptionRepository>();
        var source = (await repository.GetAsync("shared"))!;
        Assert.Equal("paused", source.Freshness); Assert.NotNull(source.ContainerId);
        Assert.Empty(await repository.ClaimDueAsync());
        var privateRow = Assert.Single(await repository.ListConfiguredAsync());
        Assert.DoesNotContain("private-test-token", privateRow.ProtectedUrl);
        Assert.Equal("https://feed.test/private-test-token", factory.Services.GetRequiredService<ICalendarSubscriptionSecrets>().Unprotect(privateRow.ProtectedUrl));
        Assert.DoesNotContain("private-test-token", JsonSerializer.Serialize(factory.Configuration.CurrentValue));
        Assert.DoesNotContain("private-test-token", await File.ReadAllTextAsync(factory.ConfigPath));
        Assert.DoesNotContain("private-test-token", JsonSerializer.Serialize(await repository.ListAsync()));
    }

    [Fact]
    public async Task ChangedResolvedLinkIsRejectedBeforeNewSourceRegistration()
    {
        var resolved = "https://feed.test/private-test-token";
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Sources.CalendarAllowedOrigins = ["https://feed.test"];
            options.Sources.CalendarSubscriptions = [new() { Id = "shared", Name = "Shared", UrlReference = "${CALENDAR_TEST}", Enabled = false }];
        }, configurationEnvironment: name => name == "CALENDAR_TEST" ? resolved : null);
        var configured = factory.Services.GetRequiredService<ConfiguredCalendarSubscriptions>();
        var repository = factory.Services.GetRequiredService<ICalendarSubscriptionRepository>();
        resolved = "https://feed.test/changed-private-test-token";
        Assert.False(factory.Configuration.TryReload());
        Assert.Equal("paused", (await repository.GetAsync("shared"))!.Freshness);
        factory.Options.CurrentValue.Sources.CalendarSubscriptions.Insert(0, new() { Id = "new", Name = "New", UrlReference = "${CALENDAR_TEST}", Enabled = false });
        var error = await Assert.ThrowsAsync<ConfigurationException>(async () => await configured.InitializeAsync());
        Assert.DoesNotContain("private-test-token", error.Message); Assert.Null(await repository.GetAsync("new"));
        Assert.Equal("paused", (await repository.GetAsync("shared"))!.Freshness);
        await factory.Services.GetRequiredService<CalendarSubscriptionOrchestrator>().StopAsync(default);
    }
}
