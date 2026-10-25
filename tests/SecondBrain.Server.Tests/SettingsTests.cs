using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Auth;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Http;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class SettingsTests
{
    [Fact]
    public async Task ChangedFileCapacityIsEnforcedWithoutRestartingTheDaemon()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        await CsrfAsync(client);
        using var capture = await client.PostAsJsonAsync("/documents", new SecondBrain.Core.Ingestion.CaptureRequest("One", "Capacity fixture"));
        Assert.Equal(HttpStatusCode.Created, capture.StatusCode);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password"));
        await CsrfAsync(client);
        var settings = (await client.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!;
        settings.Limits.Global.Files = 1;
        using var save = await client.PutAsJsonAsync("/configuration", new SettingsChange(settings.Version, "limits", JsonSerializer.SerializeToElement(settings.Limits)));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var rejected = await client.PostAsJsonAsync("/documents", new SecondBrain.Core.Ingestion.CaptureRequest("Two", "Over capacity"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Contains("capacity", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChunkingChangesReprocessExistingTextAndApplyToNewCaptures()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        await CsrfAsync(client);
        var text = string.Join(' ', Enumerable.Repeat("Nebula astronomy observation", 150));
        using var capture = await client.PostAsJsonAsync("/documents", new SecondBrain.Core.Ingestion.CaptureRequest(text, "Chunking fixture"));
        var original = (await capture.Content.ReadFromJsonAsync<SecondBrain.Core.Ingestion.CaptureResult>())!.Document;
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password"));
        await CsrfAsync(client);
        var settings = (await client.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!;
        var desired = new ChunkingOptions { TargetTokens = 64, MaxTokens = 128, OverlapTokens = 8 };
        using var save = await client.PutAsJsonAsync("/configuration", new SettingsChange(settings.Version, "chunking", JsonSerializer.SerializeToElement(desired)));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saved = (await save.Content.ReadFromJsonAsync<SettingsSnapshot>())!;
        Assert.Equal(64, saved.Chunking.TargetTokens); Assert.Equal(64, factory.Configuration.CurrentValue.Chunking.TargetTokens);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        SecondBrain.Core.Ingestion.DocumentRecord current;
        do
        {
            current = (await client.GetFromJsonAsync<SecondBrain.Core.Ingestion.DocumentRecord>("/documents/" + original.Id + "?include=text,chunks"))!;
            if (current.Chunks!.Count > original.Chunks!.Count) break;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        Assert.True(current.Chunks!.Count > original.Chunks!.Count);
        Assert.Equal(original.Revision, current.Revision); Assert.Equal(text, current.Text);
        using var newer = await client.PostAsJsonAsync("/documents", new SecondBrain.Core.Ingestion.CaptureRequest(text, "New chunking fixture"));
        var next = (await newer.Content.ReadFromJsonAsync<SecondBrain.Core.Ingestion.CaptureResult>())!;
        Assert.Equal(current.Chunks.Count, next.Document.Chunks!.Count);
        using var invalid = await client.PutAsJsonAsync("/configuration", new SettingsChange(saved.Version, "chunking", JsonSerializer.SerializeToElement(new ChunkingOptions { TargetTokens = 64, MaxTokens = 32, OverlapTokens = 64 })));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(64, factory.Configuration.CurrentValue.Chunking.TargetTokens);
    }

    [Fact]
    public async Task SettingsRequireAdminAndStepUpAndRejectStaleWrites()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var anonymous = await client.GetAsync("/configuration"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var page = await client.GetAsync("/settings"); Assert.Contains("text/html", page.Content.Headers.ContentType!.MediaType!);
        var adminKey = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        using var keyClient = factory.CreatePrivateClient(false); keyClient.DefaultRequestHeaders.Authorization = new("Bearer", adminKey.Plaintext);
        var keySettings = (await keyClient.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!;
        Assert.False(keySettings.CanEdit);
        var change = new SettingsChange(keySettings.Version, "budgets", JsonSerializer.SerializeToElement(new BudgetOptions { DailyTokens = 12345 }));
        using var keyWrite = await keyClient.PutAsJsonAsync("/configuration", change); Assert.Equal(HttpStatusCode.Forbidden, keyWrite.StatusCode);
        var readerKey = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        keyClient.DefaultRequestHeaders.Authorization = new("Bearer", readerKey.Plaintext);
        using var denied = await keyClient.GetAsync("/configuration"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await CsrfAsync(client);
        using var beforeStepUp = await client.PutAsJsonAsync("/configuration", change); Assert.Equal(HttpStatusCode.Forbidden, beforeStepUp.StatusCode);
        using var stepped = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); Assert.Equal(HttpStatusCode.OK, stepped.StatusCode);
        await CsrfAsync(client);
        using var saved = await client.PutAsJsonAsync("/configuration", change); Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var updated = (await saved.Content.ReadFromJsonAsync<SettingsSnapshot>())!;
        Assert.True(updated.CanEdit); Assert.Equal(12345, factory.Options.CurrentValue.Budgets.DailyTokens);
        Assert.NotEqual(change.Version, updated.Version);
        using var stale = await client.PutAsJsonAsync("/configuration", change); Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var invalid = await client.PutAsJsonAsync("/configuration", change with { Version = updated.Version, Section = "server" }); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(12345, factory.Options.CurrentValue.Budgets.DailyTokens);
        var reloaded = factory.Configuration.CurrentValue;
        Assert.Equal(12345, reloaded.Budgets.DailyTokens);
    }

    [Fact]
    public async Task RevocationDuringValidationPreventsTheConfigurationWrite()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        await CsrfAsync(client);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password"));
        await CsrfAsync(client);
        var settings = (await client.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!;
        var identity = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        var id = identity.GetProperty("id").GetString()!;
        var original = await File.ReadAllTextAsync(factory.ConfigPath);
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        factory.Configuration.Validating += _ => auth.RevokeAsync(id, "session").GetAwaiter().GetResult();
        using var rejected = await client.PutAsJsonAsync("/configuration", new SettingsChange(settings.Version, "budgets", JsonSerializer.SerializeToElement(new BudgetOptions { DailyTokens = 123 })));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(original, await File.ReadAllTextAsync(factory.ConfigPath));
        Assert.NotEqual(123, factory.Configuration.CurrentValue.Budgets.DailyTokens);
    }

    [Fact]
    public void SettingsYamlEditsPreserveDeferredSectionsAndSecretReferences()
    {
        const string original = "providers:\n  local:\n    kind: openai_compatible\n    api_key: '${MODEL_KEY}'\nprivacy:\n  local_only: true\n  egress_canary: true\n  trusted_services: []\nretention:\n  days: 30\nbackup:\n  key: '${BACKUP_KEY}'\ningest:\n  deferred_option: keep\n";
        var value = JsonSerializer.SerializeToElement(new EditablePrivacy(false, ["http://127.0.0.1:9000"]));
        var edited = SettingsEndpoints.Rewrite(original, new("version", "privacy", value));
        Assert.Contains("${MODEL_KEY}", edited); Assert.Contains("${BACKUP_KEY}", edited);
        Assert.Contains("retention:", edited); Assert.Contains("days: 30", edited); Assert.Contains("deferred_option: keep", edited);
        Assert.Contains("egress_canary: true", edited); Assert.Contains("local_only: false", edited);
        var provider = JsonSerializer.SerializeToElement(new ProviderChange("openai_compatible", "http://127.0.0.1:9000/v1", true));
        var next = SettingsEndpoints.Rewrite(edited, new("version", "provider", provider, "local"));
        Assert.Contains("${MODEL_KEY}", next);
    }

    [Fact]
    public async Task InvalidPrivacyAndInlineSecretsDoNotChangeDiskOrRuntime()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        await CsrfAsync(client);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password"));
        await CsrfAsync(client);
        var settings = (await client.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!;
        var original = await File.ReadAllTextAsync(factory.ConfigPath);
        using var privacy = await client.PutAsJsonAsync("/configuration", new SettingsChange(settings.Version, "privacy", JsonSerializer.SerializeToElement(new EditablePrivacy(true, []))));
        Assert.Equal(HttpStatusCode.BadRequest, privacy.StatusCode);
        using var credential = await client.PutAsJsonAsync("/configuration", new SettingsChange(settings.Version, "provider", JsonSerializer.SerializeToElement(new ProviderChange("openai_compatible", "http://127.0.0.1:9000/v1", true, "must-not-be-echoed")), "new"));
        Assert.Equal(HttpStatusCode.BadRequest, credential.StatusCode);
        Assert.DoesNotContain("must-not-be-echoed", await credential.Content.ReadAsStringAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(factory.ConfigPath));
        Assert.Equal(settings.Version, (await client.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!.Version);
    }

    private static async Task CsrfAsync(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Remove(token.HeaderName); client.DefaultRequestHeaders.Add(token.HeaderName, token.Token);
    }
}
