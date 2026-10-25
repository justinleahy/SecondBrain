using System.Net;
using System.Net.Http.Json;
using System.Text;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using SecondBrain.Storage.Backup;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class OperationCleanupApiTests
{
    [Fact]
    public async Task StartupCleansAbandonedFilesButKeepsAnActiveWorkspace()
    {
        await using var factory = new LaneDWebFactory();
        var staging = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.DataRoot, "staging")).FullName;
        using var active = OperationWorkspace.Create(staging, "export");
        var abandoned = Abandoned(staging);
        using var client = factory.CreatePrivateClient();
        Assert.True(Directory.Exists(active.Path)); Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public async Task ManualAndScheduledCleanupUseTheSameOwnershipAndLockChecks()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        var staging = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.DataRoot, "staging")).FullName;
        using var active = OperationWorkspace.Create(staging, "backup");
        var abandoned = Abandoned(staging);
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); await Csrf(client);
        using var denied = await client.PostAsJsonAsync("/maintenance/retention", new { }); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(Directory.Exists(abandoned));
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); await Csrf(client);
        using var result = await client.PostAsJsonAsync("/maintenance/retention", new { }); result.EnsureSuccessStatusCode();
        Assert.Equal(1, (await result.Content.ReadFromJsonAsync<RetentionResult>())!.Operations!.Workspaces);
        Assert.True(Directory.Exists(active.Path));
        var later = Abandoned(staging);
        factory.Clock.Advance(TimeSpan.FromHours(1));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(later) && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.False(Directory.Exists(later)); Assert.True(Directory.Exists(active.Path));
    }
    private static string Abandoned(string parent)
    {
        var id = Guid.NewGuid().ToString("N");
        var path = Directory.CreateDirectory(Path.Combine(parent, "export-" + id)).FullName;
        File.WriteAllText(Path.Combine(path, OperationWorkspace.MarkerName), "SecondBrain workspace v1\nexport\n" + id + "\n", Encoding.ASCII);
        return path;
    }
    private static async Task Csrf(HttpClient client)
    {
        var csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Remove(csrf.HeaderName); client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
    }
}
