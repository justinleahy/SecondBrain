using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Retrieval;
using SecondBrain.Core.Sources;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Sources;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class CalendarSourceApiTests
{
    private const string Link = "webcal://feed.test/published/2/synthetic-private-token?key=synthetic-query-secret";

    [Fact]
    public async Task RegistrationListingsAndLifecycleUseAdminAuthorityAndKeepTheUrlPrivate()
    {
        await using var factory = new LaneDWebFactory(options => options.Sources.CalendarAllowedOrigins = ["https://feed.test"]);
        using var admin = await KeyClient(factory, Scope.Admin, Scope.Read, Scope.Write);
        await factory.Services.GetRequiredService<CalendarSubscriptionOrchestrator>().StopAsync(default);
        using var registered = await admin.PostAsJsonAsync("/v1/sources", new { kind = "calendar_subscription", name = "Shared", url = Link });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var source = (await registered.Content.ReadFromJsonAsync<SourceResponse>())!;
        Assert.Equal("never_synced", source.Freshness); Assert.Equal(3600, source.PollIntervalSeconds);
        Assert.Equal("https://feed.test", source.Origin); Assert.NotNull(source.ContainerId);
        foreach (var path in new[] { "/sources", "/v1/sources/" + source.Id })
        {
            var json = await admin.GetStringAsync(path);
            Assert.DoesNotContain("synthetic-private-token", json); Assert.DoesNotContain("synthetic-query-secret", json);
            Assert.DoesNotContain("protected_url", json); Assert.DoesNotContain("\"url\"", json);
        }
        using var queued = await admin.PostAsJsonAsync("/sources/" + source.Id + "/scan?deep=1", new { });
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.True((await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetBoolean());
        using var deniedKeyMutation = await admin.PatchAsJsonAsync("/sources/" + source.Id, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, deniedKeyMutation.StatusCode);

        using var session = await SessionClient(factory);
        using var paused = await session.PatchAsJsonAsync("/sources/" + source.Id, new { enabled = false, name = "Renamed", poll_interval_s = 300 });
        Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
        var current = (await admin.GetFromJsonAsync<SourceResponse>("/sources/" + source.Id))!;
        Assert.Equal("paused", current.Freshness); Assert.Equal("Renamed", current.Name); Assert.Equal(300, current.PollIntervalSeconds);
        using var rejectedScan = await admin.PostAsJsonAsync("/sources/" + source.Id + "/scan", new { });
        Assert.Equal(HttpStatusCode.Conflict, rejectedScan.StatusCode);
        using var immutable = await session.PatchAsJsonAsync("/sources/" + source.Id, new { url = Link, enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, immutable.StatusCode);
        using var resume = await session.PatchAsJsonAsync("/sources/" + source.Id, new { status = "active" });
        Assert.Equal(HttpStatusCode.NoContent, resume.StatusCode);
        using var removed = await session.DeleteAsync("/sources/" + source.Id);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var missing = await admin.GetAsync("/sources/" + source.Id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<ICalendarSubscriptionRepository>().ClaimDueAsync());
    }

    [Fact]
    public async Task ExplicitSourcePurgeRequiresSessionAndIsRetryableAfterRemoval()
    {
        await using var factory = new LaneDWebFactory(options => options.Sources.CalendarAllowedOrigins = ["https://feed.test"]);
        using var admin = await KeyClient(factory, Scope.Admin, Scope.Read, Scope.Write);
        await factory.Services.GetRequiredService<CalendarSubscriptionOrchestrator>().StopAsync(default);
        using var created = await admin.PostAsJsonAsync("/sources", new { kind = "calendar_subscription", name = "Purge", url = Link });
        var id = (await created.Content.ReadFromJsonAsync<SourceResponse>())!.Id;
        using var denied = await admin.DeleteAsync("/sources/" + id + "?purge=1");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var session = await SessionClient(factory);
        using var invalid = await session.DeleteAsync("/sources/" + id + "?purge=yes");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var removed = await session.DeleteAsync("/sources/" + id);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var purged = await session.DeleteAsync("/v1/sources/" + id + "?purge=1");
        Assert.Equal(HttpStatusCode.NoContent, purged.StatusCode);
        using var repeated = await session.DeleteAsync("/sources/" + id + "?purge=1");
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        await using var read = await factory.Store.OpenReadConnectionAsync();
        Assert.Equal("", await read.Connection.ExecuteScalarAsync<string>("SELECT protected_url FROM calendar_subscriptions WHERE source_id=@id", new { id }));
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM calendar_source_purges WHERE source_id=@id AND completed_at IS NOT NULL", new { id }));
    }

    [Fact]
    public async Task DeniesNonadminsAndReportsValidationWithoutEchoingSecrets()
    {
        await using var factory = new LaneDWebFactory();
        using var admin = await KeyClient(factory, Scope.Admin);
        using var reader = await KeyClient(factory, Scope.Read, Scope.Write);
        using var denied = await reader.PostAsJsonAsync("/sources", new { kind = "calendar_subscription", url = Link });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var listing = await reader.GetAsync("/sources"); Assert.Equal(HttpStatusCode.Forbidden, listing.StatusCode);
        foreach (var url in new[] { Link, "https://person:synthetic-private-token@feed.test/calendar", "ftp://feed.test/synthetic-private-token" })
        {
            using var invalid = await admin.PostAsJsonAsync("/sources", new { kind = "calendar_subscription", name = "Shared", url });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain("synthetic-private-token", await invalid.Content.ReadAsStringAsync());
        }
        Assert.Empty(await factory.Services.GetRequiredService<ICalendarSubscriptionRepository>().ListAsync());
    }

    [Theory]
    [InlineData(false, null, null, "never_synced")]
    [InlineData(true, null, null, "paused")]
    [InlineData(false, 0, null, "current")]
    [InlineData(false, 121, null, "stale")]
    [InlineData(false, 0, "calendar_timeout", "failed")]
    [InlineData(true, 0, "calendar_timeout", "paused")]
    public async Task EmptyQueryStillReportsSubscriptionFreshness(bool paused, int? minutesOld, string? error, string expected)
    {
        await using var factory = new LaneDWebFactory(options => options.Sources.CalendarAllowedOrigins = ["https://feed.test"]);
        using var admin = await KeyClient(factory, Scope.Admin, Scope.Read);
        await factory.Services.GetRequiredService<CalendarSubscriptionOrchestrator>().StopAsync(default);
        using var created = await admin.PostAsJsonAsync("/sources", new { kind = "calendar_subscription", name = "Empty", url = Link, enabled = !paused });
        var id = (await created.Content.ReadFromJsonAsync<SourceResponse>())!.Id;
        await factory.Store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("UPDATE calendar_subscriptions SET last_success_at=@success,error_code=@error WHERE source_id=@id",
                new { id, error, success = minutesOld is { } minutes ? AuthTime.Format(factory.Clock.GetUtcNow().AddMinutes(-minutes)) : null }, transaction, cancellationToken: token));
            return 0;
        });
        using var response = await admin.PostAsJsonAsync("/v1/query", new QueryRequest(Type: "calendar_event", Source: id, Predicates: [new("title", "eq", JsonSerializer.SerializeToElement("unmatched"))]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<QueryPage>())!; Assert.Empty(page.Records);
        var coverage = Assert.IsType<JsonElement>(page.Coverage);
        var status = Assert.Single(coverage.GetProperty("subscriptions").EnumerateArray());
        Assert.Equal(id, status.GetProperty("source_id").GetString()); Assert.Equal(expected, status.GetProperty("state").GetString());
        Assert.Equal(expected != "current", status.GetProperty("stale").GetBoolean());
        using var other = await admin.PostAsJsonAsync("/v1/query", new QueryRequest(Type: "calendar_event", Source: "other"));
        Assert.DoesNotContain(id, await other.Content.ReadAsStringAsync());
    }

    private static async Task<HttpClient> KeyClient(LaneDWebFactory factory, params Scope[] scopes)
    {
        var key = await factory.CreateKeyAsync(scopes.ToHashSet()); var client = factory.CreatePrivateClient(false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext); return client;
    }
    private static async Task<HttpClient> SessionClient(LaneDWebFactory factory)
    {
        await factory.SeedAccountAsync(); var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await Csrf(client);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        await Csrf(client); return client;
    }
    private static async Task Csrf(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Remove(csrf.HeaderName); client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
    }
}
