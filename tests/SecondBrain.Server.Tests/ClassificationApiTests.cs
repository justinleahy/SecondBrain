using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Providers;
using SecondBrain.MockProvider;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Http;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class ClassificationApiTests
{
    [Theory]
    [InlineData("transcript.vtt", null, "transcript")]
    [InlineData("plain.txt", "article", "article")]
    public async Task SuppliedTypesAndUnambiguousFormatsDoNotCallTheClassifier(string name, string? declared, string expected)
    {
        await using var factory = new LaneDWebFactory(o => o.Classification.Llm = true);
        var calls = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = _ =>
        { Interlocked.Increment(ref calls); throw new InvalidOperationException("Classifier should not be called."); };
        await factory.SeedAccountAsync(); using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); login.EnsureSuccessStatusCode(); await Csrf(client);
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(name.EndsWith(".vtt", StringComparison.Ordinal)
            ? "WEBVTT\n\n00:00.000 --> 00:01.000\n<v Ada>A statement.</v>\n" : "An ordinary text file.")), "file", name);
        body.Add(new StringContent(JsonSerializer.Serialize(new CaptureRequest("", Type: declared), new JsonSerializerOptions(JsonSerializerDefaults.Web))), "metadata");
        using var response = await client.PostAsync("/documents?wait=20", body); response.EnsureSuccessStatusCode();
        Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<CaptureResult>())!.Document.Type);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AmbiguousFileUsesClassificationAndRetainsOriginalProperties()
    {
        await using var factory = new LaneDWebFactory(o => o.Classification.Llm = true);
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
            await context.Response.WriteAsJsonAsync(new { id = "classification", choices = new[] { new { index = 0, message = new { role = "assistant", content = "{\"type\":\"article\",\"confidence\":0.95}" }, finish_reason = "stop" } }, usage = new { prompt_tokens = 30, completion_tokens = 10, total_tokens = 40 } });
        await factory.SeedAccountAsync(); using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); login.EnsureSuccessStatusCode(); await Csrf(client);
        const string file = "---\nprops:\n  author: Ada\n---\nAn article about sustainable cities and public transport.";
        using var body = new MultipartFormDataContent(); body.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(file)), "file", "article.md");
        using var response = await client.PostAsync("/v1/documents?wait=20", body); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<CaptureResult>())!;
        Assert.Equal("article", result.Document.Type); Assert.Contains("Ada", result.Document.PropsJson);
        await using var read = await factory.Store.OpenReadConnectionAsync();
        var row = await read.Connection.QuerySingleAsync<(string Origin, double Confidence, string Original)>(
            "SELECT type_origin,type_confidence,props_original_json FROM documents WHERE id=@id", new { id = result.Document.Id });
        Assert.Equal("llm", row.Origin); Assert.Equal(.95, row.Confidence); Assert.Contains("Ada", row.Original);
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM jobs WHERE document_id=@id AND json_extract(payload_json,'$.classification.status')='classified'", new { id = result.Document.Id }));
        var report = (await client.GetFromJsonAsync<ModelUsageReport>("/usage"))!;
        Assert.Contains(report.Rows, r => r.Role == "classify" && r.ChargedTokens == 40 && r.CredentialId is not null);
        var details = (await client.GetFromJsonAsync<DocumentDetails>("/documents/" + result.Document.Id + "/metadata"))!;
        Assert.Equal(.95, details.TypeConfidence);
    }

    [Fact]
    public async Task SettingsValidateAndReloadClassificationControls()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); login.EnsureSuccessStatusCode(); await Csrf(client);
        var initial = (await client.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!;
        var value = JsonSerializer.SerializeToElement(new { llm = true, minConfidence = .85 });
        using var denied = await client.PutAsJsonAsync("/configuration", new SettingsChange(initial.Version, "classification", value));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); step.EnsureSuccessStatusCode(); await Csrf(client);
        using var saved = await client.PutAsJsonAsync("/configuration", new SettingsChange(initial.Version, "classification", value)); saved.EnsureSuccessStatusCode();
        var updated = (await saved.Content.ReadFromJsonAsync<SettingsSnapshot>())!;
        Assert.True(updated.Classification.Llm); Assert.Equal(.85, factory.Options.CurrentValue.Classification.MinConfidence);
        using var invalid = await client.PutAsJsonAsync("/configuration", new SettingsChange(updated.Version, "classification", JsonSerializer.SerializeToElement(new { llm = true, minConfidence = 2 })));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static async Task Csrf(HttpClient client)
    { var token = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!; client.DefaultRequestHeaders.Remove(token.HeaderName); client.DefaultRequestHeaders.Add(token.HeaderName, token.Token); }
}
