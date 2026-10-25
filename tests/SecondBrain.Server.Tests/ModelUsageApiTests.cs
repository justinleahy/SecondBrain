using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Providers;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ModelUsageApiTests
{
    [Fact]
    public async Task AdministratorCanInspectUsageAndReadKeyCannot()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); login.EnsureSuccessStatusCode();
        var usage = factory.Services.GetRequiredService<IModelUsage>();
        var id = await usage.ReserveAsync(new("api-fixture", "local", "model", "embed", 100));
        await usage.SettleAsync(id, new(12, 0, 4, 12));
        foreach (var prefix in new[] { "", "/v1" })
        {
            using var response = await client.GetAsync(prefix + "/usage"); response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl?.NoStore);
            var report = (await response.Content.ReadFromJsonAsync<ModelUsageReport>())!;
            Assert.Equal(12, report.DailyTokens); Assert.Equal(4, Assert.Single(report.Rows).CachedInputTokens);
        }
        using var bad = await client.GetAsync("/usage?from=2025-01-01T00:00:00Z&to=2026-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        using var reader = factory.CreatePrivateClient(handleCookies: false);
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Plaintext);
        using var denied = await reader.GetAsync("/v1/usage"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task SearchChargesItsInitiatingCredentialAndBudgetExhaustionKeepsKeywordSearch()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); login.EnsureSuccessStatusCode();
        var csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        using var captured = await client.PostAsJsonAsync("/documents?wait=1", new CaptureRequest("An amber telescope for the garden", "Telescope")); captured.EnsureSuccessStatusCode();
        await factory.Services.GetRequiredService<EmbeddingProcessor>().ProcessAsync();
        using var search = await client.PostAsJsonAsync("/search", new { query = "telescope", mode = "hybrid" }); search.EnsureSuccessStatusCode();
        var report = (await client.GetFromJsonAsync<ModelUsageReport>("/usage"))!;
        Assert.Contains(report.Rows, row => row.Role == "embed" && row.CredentialId is not null);
        Assert.True(report.DailyTokens > 0);
        factory.Options.CurrentValue.Budgets.DailyTokens = 0;
        using var limited = await client.PostAsJsonAsync("/search", new { query = "telescope", mode = "hybrid" }); limited.EnsureSuccessStatusCode();
        var result = await limited.Content.ReadAsStringAsync(); Assert.Contains("embed-unavailable", result); Assert.Contains("Telescope", result);
        Assert.Equal(report.DailyTokens, (await client.GetFromJsonAsync<ModelUsageReport>("/usage"))!.DailyTokens);
    }
}
