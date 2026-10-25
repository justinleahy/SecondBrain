using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class FileConflictTests
{
    [Fact]
    public async Task DiskConflictPreservesDraftIndexesExternalRevisionAndPurgesItsDownload()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await LoginAsync(factory);
        using var capture = await client.PostAsJsonAsync("/documents", new CaptureRequest("Original text", "Conflict fixture"));
        var created = (await capture.Content.ReadFromJsonAsync<CaptureResult>())!;
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var original = (await documents.GetAsync(created.Document.Id, true))!;
        await File.WriteAllTextAsync(original.Path!, "External revision telescope");
        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/documents/" + original.Id) { Content = JsonContent.Create(new { content = "Draft preserved nebula" }) };
        patch.Headers.TryAddWithoutValidation("If-Match", "1");
        using var conflict = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadAsStringAsync();
        Assert.DoesNotContain(factory.Store.DirectoryPath, body);
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.EndsWith("/file-conflict", problem.GetProperty("type").GetString());
        var mutation = problem.GetProperty("conflict_id").GetString()!;
        var name = problem.GetProperty("conflict_file").GetString()!;
        Assert.Contains(".conflict-", name);
        Assert.Equal("External revision telescope", await File.ReadAllTextAsync(original.Path!));
        var download = "/documents/" + original.Id + "/conflicts/" + mutation;
        using var copy = await client.GetAsync(download);
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.Equal("application/octet-stream", copy.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Draft preserved nebula", await copy.Content.ReadAsStringAsync());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        DocumentRecord? current;
        do
        {
            current = await documents.GetAsync(original.Id, true);
            if (current?.Text == "External revision telescope") break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal(2, current!.Revision); Assert.Equal("External revision telescope", current.Text);
        using var otherCapture = await client.PostAsJsonAsync("/documents", new CaptureRequest("Other", "Other"));
        var other = (await otherCapture.Content.ReadFromJsonAsync<CaptureResult>())!;
        using var wrong = await client.GetAsync("/documents/" + other.Document.Id + "/conflicts/" + mutation);
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        await CsrfAsync(client);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        await CsrfAsync(client);
        using var purge = new HttpRequestMessage(HttpMethod.Post, "/documents/" + original.Id + "/purge"); purge.Headers.TryAddWithoutValidation("If-Match", "2");
        using var purged = await client.SendAsync(purge); Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        using var gone = await client.GetAsync(download); Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(original.Path)!, name)));
    }

    [Fact]
    public async Task NonregularDestinationIsNotReplacedAndStillPreservesTheDraft()
    {
        await using var factory = new LaneDWebFactory(); using var client = await LoginAsync(factory);
        using var capture = await client.PostAsJsonAsync("/documents", new CaptureRequest("Original", "Directory conflict"));
        var result = (await capture.Content.ReadFromJsonAsync<CaptureResult>())!;
        var original = (await factory.Services.GetRequiredService<IDocumentRepository>().GetAsync(result.Document.Id, true))!;
        File.Delete(original.Path!); Directory.CreateDirectory(original.Path!);
        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/documents/" + original.Id) { Content = JsonContent.Create(new { content = "Saved draft" }) };
        patch.Headers.TryAddWithoutValidation("If-Match", "1");
        using var response = await client.SendAsync(patch); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var download = await client.GetAsync("/documents/" + original.Id + "/conflicts/" + problem.GetProperty("conflict_id").GetString());
        Assert.Equal("Saved draft", await download.Content.ReadAsStringAsync());
        Assert.True(Directory.Exists(original.Path));
        Assert.Equal(1, (await factory.Services.GetRequiredService<IDocumentRepository>().GetAsync(original.Id))!.Revision);
    }

    private static async Task<HttpClient> LoginAsync(LaneDWebFactory factory)
    {
        await factory.SeedAccountAsync(); var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await CsrfAsync(client); return client;
    }
    private static async Task CsrfAsync(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Remove(csrf.HeaderName); client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
    }
}
