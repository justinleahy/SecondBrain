using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Http;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class MetadataRetentionApiTests
{
    [Fact]
    public async Task CombinedCleanupPurgesOnlyExpiredTrashAfterStepUp()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); await Csrf(client);
        var ids = new List<string>();
        foreach (var title in new[] { "Expired API note", "Recent API note" })
        {
            using var created = await client.PostAsJsonAsync("/documents", new CaptureRequest("Private fixture content", title));
            var document = (await created.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
            ids.Add(document.Id);
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/documents/" + document.Id);
            request.Headers.TryAddWithoutValidation("If-Match", "\"" + document.Revision + "\"");
            using var removed = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        }
        await factory.Store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("UPDATE documents SET deleted_at=@old WHERE id=@id",
                new { old = factory.Clock.GetUtcNow().AddDays(-31).ToString("O"), id = ids[0] }, transaction, cancellationToken: token));
            return 0;
        });
        using var denied = await client.PostAsJsonAsync("/maintenance/retention", new { }); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); await Csrf(client);
        using var response = await client.PostAsJsonAsync("/v1/maintenance/retention", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<RetentionResult>())!.Documents.Purged);
        var trash = await client.GetFromJsonAsync<CatalogPage<TrashedDocument>>("/trash");
        Assert.Equal(ids[1], Assert.Single(trash!.Items).Id);
    }

    [Fact]
    public async Task ScheduledCleanupWaitsForItsHourlyRunAndRemovesOnlyExpiredOrphans()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); await Csrf(client);
        using var created = await client.PostAsJsonAsync("/documents", new CaptureRequest("Scheduled expiry note", "Scheduled expiry"));
        var document = (await created.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
        using var remove = new HttpRequestMessage(HttpMethod.Delete, "/documents/" + document.Id);
        remove.Headers.TryAddWithoutValidation("If-Match", "\"" + document.Revision + "\"");
        using var removed = await client.SendAsync(remove); Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        await factory.Store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO payloads(id,sha256,bytes,created_at) VALUES('old-orphan','hash',X'74657374',@old),('recent-orphan','hash',X'74657374',@now)",
                new { old = factory.Clock.GetUtcNow().AddDays(-60).ToString("O"), now = factory.Clock.GetUtcNow().ToString("O") }, transaction, cancellationToken: token));
            await connection.ExecuteAsync(new CommandDefinition("UPDATE documents SET deleted_at=@old WHERE id=@id",
                new { id = document.Id, old = factory.Clock.GetUtcNow().AddDays(-31).ToString("O") }, transaction, cancellationToken: token));
            return 0;
        });
        factory.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(1, await Count("old-orphan"));
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await Count("old-orphan") != 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.Equal(0, await Count("old-orphan")); Assert.Equal(1, await Count("recent-orphan"));
        string? status;
        do
        {
            await using var read = await factory.Store.OpenReadConnectionAsync();
            status = await read.Connection.ExecuteScalarAsync<string>("SELECT status FROM documents WHERE id=@id", new { id = document.Id });
            if (status == "purged") break;
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal("purged", status);
        async Task<int> Count(string id)
        {
            await using var read = await factory.Store.OpenReadConnectionAsync();
            return await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payloads WHERE id=@id", new { id });
        }
    }

    [Fact]
    public async Task ManualMetadataCleanupRequiresAdminStepUpAndUsesReloadedPolicy()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var reader = factory.CreatePrivateClient(false);
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        reader.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        using var forbidden = await reader.PostAsJsonAsync("/maintenance/retention/metadata", new { });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await factory.Store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO payloads(id,sha256,bytes,created_at) VALUES('expired-test-payload','hash',X'74657374',@old)",
                new { old = factory.Clock.GetUtcNow().AddDays(-60).ToString("O") }, transaction, cancellationToken: token));
            return 0;
        });
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        await Csrf(client);
        using var noStep = await client.PostAsJsonAsync("/maintenance/retention/metadata", new { });
        Assert.Equal(HttpStatusCode.Forbidden, noStep.StatusCode);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); await Csrf(client);
        var settings = (await client.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!;
        settings.Retention.PayloadDays = 90;
        using var changed = await client.PutAsJsonAsync("/configuration", new SettingsChange(settings.Version, "retention", JsonSerializer.SerializeToElement(settings.Retention)));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var updated = (await changed.Content.ReadFromJsonAsync<SettingsSnapshot>())!;
        using var retained = await client.PostAsJsonAsync("/v1/maintenance/retention/metadata", new { });
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        Assert.Equal(0, (await retained.Content.ReadFromJsonAsync<MetadataRetentionResult>())!.OrphanPayloads);
        updated.Retention.PayloadDays = 0;
        using var invalid = await client.PutAsJsonAsync("/configuration", new SettingsChange(updated.Version, "retention", JsonSerializer.SerializeToElement(updated.Retention)));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        updated.Retention.PayloadDays = 30;
        using var restored = await client.PutAsJsonAsync("/configuration", new SettingsChange(updated.Version, "retention", JsonSerializer.SerializeToElement(updated.Retention)));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using var purged = await client.PostAsJsonAsync("/maintenance/retention/metadata", new { });
        Assert.Equal(1, (await purged.Content.ReadFromJsonAsync<MetadataRetentionResult>())!.OrphanPayloads);
    }

    private static async Task Csrf(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Remove(csrf.HeaderName); client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
    }
}
