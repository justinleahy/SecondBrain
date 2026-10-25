using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4RetentionQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_RETENTION_HARNESS"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_RETENTION_OUTPUT")!;
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_RETENTION_PORT") ?? "18311", System.Globalization.CultureInfo.InvariantCulture);
        var origin = "http://127.0.0.1:" + port; Directory.CreateDirectory(output);
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Server.Listeners = [new() { Bind = "127.0.0.1", Port = port }];
            options.Server.Hosts = ["127.0.0.1:" + port]; options.Server.Origins = [origin]; options.Time.Zone = "UTC";
        });
        factory.UseKestrel(_ => { }); factory.StartServer(); await factory.SeedAccountAsync();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new Uri(origin) };
        client.DefaultRequestHeaders.Add("Origin", origin);
        await Csrf(client);
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); login.EnsureSuccessStatusCode();
        await Csrf(client);
        foreach (var (title, days) in new[] { ("Expired 1280", 31d), ("Expired 390", 10d), ("Recent note", 0.25d) })
        {
            using var captured = await client.PostAsJsonAsync("/documents", new CaptureRequest("Synthetic retained note", title)); captured.EnsureSuccessStatusCode();
            var document = (await captured.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/documents/" + document.Id);
            request.Headers.TryAddWithoutValidation("If-Match", "\"" + document.Revision + "\"");
            using var deleted = await client.SendAsync(request); deleted.EnsureSuccessStatusCode();
            await factory.Store.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await connection.ExecuteAsync(new CommandDefinition("UPDATE documents SET deleted_at=@date WHERE id=@id", new { id = document.Id, date = factory.Clock.GetUtcNow().AddDays(-days).ToString("O") }, transaction, cancellationToken: token));
                return 0;
            });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { origin, syntheticData = true, realManagedFiles = true }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, timeout.Token);
    }

    private static async Task Csrf(HttpClient client)
    {
        var value = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Remove(value.HeaderName); client.DefaultRequestHeaders.Add(value.HeaderName, value.Token);
    }
}
