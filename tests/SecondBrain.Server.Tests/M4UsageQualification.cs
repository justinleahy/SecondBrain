using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Providers;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4UsageQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_USAGE_HARNESS"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_USAGE_OUTPUT")!;
        Directory.CreateDirectory(output);
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_USAGE_PORT") ?? "18324", System.Globalization.CultureInfo.InvariantCulture);
        var origin = "http://127.0.0.1:" + port;
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Server.Listeners = [new() { Bind = "127.0.0.1", Port = port }];
            options.Server.Hosts = ["127.0.0.1:" + port]; options.Server.Origins = [origin]; options.Budgets.DailyTokens = 2000;
        });
        factory.UseKestrel(_ => { }); factory.StartServer(); await factory.SeedAccountAsync();
        var usage = factory.Services.GetRequiredService<IModelUsage>();
        var chat = await usage.ReserveAsync(new("synthetic-chat", "Local model", "Conversation", "chat", 400));
        await usage.SettleAsync(chat, new(80, 20, 30, 100));
        var embed = await usage.ReserveAsync(new("synthetic-embed", "Local model", "Search", "embed", 60));
        await usage.SettleAsync(embed, null);
        await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { origin, syntheticData = true, dailyTokens = 160, dailyLimit = 2000 }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, timeout.Token);
    }
}
