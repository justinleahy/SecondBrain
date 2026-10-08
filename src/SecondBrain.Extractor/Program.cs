using Microsoft.Extensions.Hosting;

namespace SecondBrain.Extractor;

/// <summary>The worker host scaffold; lane C adds the socket stub in M0 item 14 (spec §15.7).</summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        using var host = builder.Build();
        await host.RunAsync();
    }
}
