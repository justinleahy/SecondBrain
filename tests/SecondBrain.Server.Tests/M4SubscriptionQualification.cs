using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4SubscriptionQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_SUBSCRIPTION_HARNESS"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_SUBSCRIPTION_OUTPUT") ?? throw new InvalidOperationException("Set the harness output directory.");
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_SUBSCRIPTION_PORT") ?? "18307", System.Globalization.CultureInfo.InvariantCulture);
        var origin = "http://127.0.0.1:" + port; Directory.CreateDirectory(output);
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Server.Listeners = [new ListenerOptions { Bind = "127.0.0.1", Port = port }];
            options.Server.Hosts = ["127.0.0.1:" + port]; options.Server.Origins = [origin];
            options.Time.Zone = "UTC"; options.Sources.CalendarAllowedOrigins = ["https://feed.test"];
            options.Calendar.PastMonths = 120; options.Calendar.FutureMonths = 120;
        }, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
                new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath)));
            services.Replace(ServiceDescriptor.Singleton<ICalendarDownloadTransport, Transport>());
        });
        factory.UseKestrel(_ => { }); factory.StartServer(); await factory.SeedAccountAsync();
        await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { origin, temporaryTestAccount = true, realExtractor = true, controlledTransport = true }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, deadline.Token);
    }
    private sealed class Transport(IInputStager staging) : ICalendarDownloadTransport
    {
        private readonly ConcurrentDictionary<string, int> calls = new();
        public async ValueTask<CalendarDownloadResult> DownloadAsync(CalendarDownloadRequest request, CancellationToken cancellationToken = default)
        {
            var count = calls.AddOrUpdate(request.SourceId, 1, (_, previous) => previous + 1);
            if (count == 2) return new("failed", Error: "calendar_unavailable");
            var title = count < 3 ? "Shared calendar planning" : "Updated shared planning";
            var calendar = "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:planning\nDTSTAMP:20261001T000000Z\nDTSTART:20261025T090000Z\nDTEND:20261025T100000Z\nSUMMARY:" + title + "\nEND:VEVENT\nEND:VCALENDAR\n";
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(calendar));
            return new("modified", await staging.StageAsync(bytes, "subscription.ics", cancellationToken), "\"fixture-" + Math.Min(count, 3) + "\"");
        }
    }
}
