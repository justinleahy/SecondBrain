using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace SecondBrain.Extractor;

/// <summary>The dedicated Unix-socket extraction worker (M0 stub, spec §15.7).</summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var options = ExtractorServerOptions.Parse(args);
        ExtractorEnvironment.ScrubCurrentProcess();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSystemd();
        builder.Services.AddSingleton(options);
        builder.Services.AddHostedService<ExtractorWorker>();
        using var host = builder.Build();
        await host.RunAsync();
    }
}
