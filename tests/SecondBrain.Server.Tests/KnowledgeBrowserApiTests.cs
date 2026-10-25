using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class KnowledgeBrowserApiTests
{
    [Theory]
    [InlineData("/trash")]
    [InlineData("/suppressions")]
    [InlineData("/timeline")]
    [InlineData("/tags")]
    [InlineData("/types")]
    [InlineData("/browse/sources")]
    [InlineData("/documents/absent/related")]
    public async Task CatalogRoutesRequireAuthentication(string path)
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TrashIsMetadataOnlyAndItsCursorCannotChangeCredentialsOrCatalogs()
    {
        await using var factory = new LaneDWebFactory();
        var authority = await AuthorityAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        for (var i = 0; i < 3; i++)
        {
            var note = await documents.CaptureAsync(new("PRIVATE body " + i, "Note " + i, Tags: ["private-tag"]), authority);
            await documents.MutateAsync(note.Document.Id, 1, i == 2 ? "purge" : "delete", JsonSerializer.SerializeToElement(new { }), authority);
        }
        using var client = await KeyClientAsync(factory, Scope.Read, Scope.Write);
        using var firstResponse = await client.GetAsync("/trash?limit=1");
        var text = await firstResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PRIVATE", text); Assert.DoesNotContain("private-tag", text);
        Assert.DoesNotContain("content_hash", text); Assert.DoesNotContain("path", text);
        var first = JsonSerializer.Deserialize<CatalogPage<TrashedDocument>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(first.Items); Assert.NotNull(first.NextCursor);
        var next = await client.GetFromJsonAsync<CatalogPage<TrashedDocument>>("/trash?limit=1&cursor=" + Uri.EscapeDataString(first.NextCursor));
        Assert.Single(next!.Items); Assert.NotEqual(first.Items[0].Id, next.Items[0].Id); Assert.Null(next.NextCursor);
        using var other = await KeyClientAsync(factory, Scope.Read, Scope.Write);
        using var crossCredential = await other.GetAsync("/trash?cursor=" + Uri.EscapeDataString(first.NextCursor));
        Assert.Equal(HttpStatusCode.BadRequest, crossCredential.StatusCode);
        using var crossCatalog = await client.GetAsync("/suppressions?cursor=" + Uri.EscapeDataString(first.NextCursor));
        Assert.Equal(HttpStatusCode.BadRequest, crossCatalog.StatusCode);
    }

    [Fact]
    public async Task SuppressionLiftRequiresAnInteractiveSessionAndDoesNotRestoreTheDocument()
    {
        await using var factory = new LaneDWebFactory();
        var authority = await AuthorityAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var note = await documents.CaptureAsync(new("Hidden original", "Deleted note"), authority);
        await documents.MutateAsync(note.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), authority);
        using var keyClient = await KeyClientAsync(factory, Scope.Read, Scope.Write);
        var suppression = Assert.Single((await keyClient.GetFromJsonAsync<CatalogPage<SuppressionRecord>>("/suppressions"))!.Items);
        using var keyLift = await keyClient.DeleteAsync("/suppressions/" + Uri.EscapeDataString(suppression.Id));
        Assert.Equal(HttpStatusCode.Forbidden, keyLift.StatusCode);
        using var readOnly = await KeyClientAsync(factory, Scope.Read);
        using var denied = await readOnly.GetAsync("/suppressions");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await factory.SeedAccountAsync();
        using var session = factory.CreatePrivateClient();
        session.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await session.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var missingCsrf = await session.DeleteAsync("/suppressions/" + Uri.EscapeDataString(suppression.Id));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        var csrf = (await session.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        session.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        using var lifted = await session.DeleteAsync("/suppressions/" + Uri.EscapeDataString(suppression.Id));
        Assert.Equal(HttpStatusCode.NoContent, lifted.StatusCode);
        Assert.Equal("deleted", (await documents.GetAsync(note.Document.Id))!.Status);
        using var hidden = await keyClient.GetAsync("/documents/" + note.Document.Id + "?include=text");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var restore = new HttpRequestMessage(HttpMethod.Post, "/documents/" + note.Document.Id + "/restore");
        restore.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        using var restored = await session.SendAsync(restore);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Contains("Hidden original", (await restored.Content.ReadFromJsonAsync<CaptureResult>())!.Document.Text);
    }

    [Fact]
    public async Task RevokedAuthorityCannotLiftASuppressionDirectly()
    {
        await using var factory = new LaneDWebFactory();
        var authority = await AuthorityAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var note = await documents.CaptureAsync(new("hidden", "Note"), authority);
        await documents.MutateAsync(note.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), authority);
        var catalog = factory.Services.GetRequiredService<IDocumentCatalog>();
        var suppression = Assert.Single((await catalog.SuppressionsAsync(authority)).Items);
        await factory.Services.GetRequiredService<IAuthRepository>().RevokeAsync(authority.CredentialId);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => catalog.LiftSuppressionAsync(suppression.Id, authority).AsTask());
    }

    [Fact]
    public async Task TimelineGroupsTheFullFallBackDayAndExcludesTheNextMidnight()
    {
        await using var factory = new LaneDWebFactory(options => options.Time.Zone = "America/New_York");
        var authority = await AuthorityAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        async Task<CaptureResult> Article(string title, string instant) => await documents.CaptureAsync(new("Dated text", title, "article",
            JsonSerializer.SerializeToElement(new { published_at = instant, author = "Maya Chen" }), ["dated"]), authority);
        await Article("Start", "2026-11-01T04:00:00Z");
        await Article("End", "2026-11-02T04:59:59Z");
        await Article("Outside", "2026-11-02T05:00:00Z");
        await documents.CaptureAsync(new("Undated text", "Undated note"), authority);
        using var client = await KeyClientAsync(factory, Scope.Read);
        const string path = "/timeline?from=2026-11-01&to=2026-11-02&type=article&people=Maya%20Chen&limit=1";
        var first = (await client.GetFromJsonAsync<TimelinePage>(path))!;
        Assert.Equal("America/New_York", first.TimeZone);
        Assert.Equal("2026-11-01", Assert.Single(first.Days).Day);
        Assert.Equal("End", Assert.Single(first.Days[0].Documents).Title);
        Assert.Contains("dated", first.Days[0].Documents[0].Tags!);
        Assert.NotNull(first.NextCursor);
        var next = (await client.GetFromJsonAsync<TimelinePage>(path + "&cursor=" + Uri.EscapeDataString(first.NextCursor)))!;
        Assert.Equal("Start", Assert.Single(Assert.Single(next.Days).Documents).Title);
        Assert.Null(next.NextCursor);
        using var wrongFilter = await client.GetAsync(path.Replace("Maya%20Chen", "Someone") + "&cursor=" + Uri.EscapeDataString(first.NextCursor));
        Assert.Equal(HttpStatusCode.BadRequest, wrongFilter.StatusCode);
    }

    [Fact]
    public async Task FacetsCountMatchingDocumentsBeyondTheResultLimitAndExcludeDeletedNotes()
    {
        await using var factory = new LaneDWebFactory();
        var authority = await AuthorityAsync(factory);
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        for (var index = 0; index < 55; index++)
        {
            var note = await documents.CaptureAsync(new("shared match", "Note " + index, Tags: ["included"]), authority);
            if (index == 0) await documents.MutateAsync(note.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), authority);
        }
        await documents.CaptureAsync(new("shared match", "Article", "article", Tags: ["included"]), authority);
        await documents.CaptureAsync(new("shared match", "Wrong tag", Tags: ["excluded"]), authority);
        await documents.CaptureAsync(new("unrelated content", "Wrong words", Tags: ["included"]), authority);
        using var client = await KeyClientAsync(factory, Scope.Read);
        using var response = await client.PostAsJsonAsync("/search/facets", new SecondBrain.Core.Retrieval.FacetRequest("shared", new(Type: "note", Tags: ["included"])));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var facets = (await response.Content.ReadFromJsonAsync<SecondBrain.Core.Retrieval.TypeCount[]>())!;
        Assert.Equal(54, facets.Single(item => item.Type == "note").Count);
        Assert.Equal(1, facets.Single(item => item.Type == "article").Count);
        Assert.Equal(0, facets.Single(item => item.Type == "file").Count);
        var sources = await client.GetStringAsync("/browse/sources");
        Assert.DoesNotContain("config", sources);
        Assert.DoesNotContain("path", sources);
    }

    private static async Task<HttpClient> KeyClientAsync(LaneDWebFactory factory, params Scope[] scopes)
    {
        var key = await factory.CreateKeyAsync(scopes.ToHashSet());
        var client = factory.CreatePrivateClient(false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        return client;
    }

    private static async Task<IngestionAuthority> AuthorityAsync(LaneDWebFactory factory)
    {
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var session = factory.Services.GetRequiredService<CredentialFactory>().CreateSession("catalog fixture", false, await auth.GetEpochAsync(), 1, 1);
        await auth.AddAsync(session.Record);
        return new(session.Record.Id, session.Record.Generation, session.Record.AccountEpoch, "catalog fixture", "session");
    }
}
