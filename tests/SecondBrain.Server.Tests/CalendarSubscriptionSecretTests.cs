using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Server.Tests.Support;
using SecondBrain.Storage;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class CalendarSubscriptionSecretTests
{
    [Fact]
    public async Task RegistrationProtectsTheCompleteBearerUrlWithADedicatedPurpose()
    {
        await using var factory = new LaneDWebFactory(options => options.Sources.CalendarAllowedOrigins = ["https://feed.test"]);
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        var service = factory.Services.GetRequiredService<CalendarSubscriptionService>();
        const string url = "webcal://feed.test/published/private-synthetic-token?key=secret-value";
        var result = await service.CreateAsync("Private fixture", url, 3600, true, new(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "test", "api_key"));
        Assert.DoesNotContain("private-synthetic-token", JsonSerializer.Serialize(result));
        await using var lease = await factory.Services.GetRequiredService<IStateStore>().OpenReadConnectionAsync();
        await using var read = lease.Connection.CreateCommand(); read.CommandText = "SELECT protected_url FROM calendar_subscriptions";
        var envelope = (string)(await read.ExecuteScalarAsync())!;
        Assert.DoesNotContain("private-synthetic-token", envelope); Assert.DoesNotContain("secret-value", envelope);
        var secrets = factory.Services.GetRequiredService<ICalendarSubscriptionSecrets>();
        Assert.Equal("https://feed.test/published/private-synthetic-token?key=secret-value", secrets.Unprotect(envelope));
        Assert.Throws<CryptographicException>(() => new KeyRingPayloadProtector(factory.KeyRing).Unprotect("secondbrain.calendar-subscription-url.v1", envelope));
    }
}
