using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Server.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4CalendarQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_CALENDAR_HARNESS"));
        var output = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_CALENDAR_OUTPUT") ?? throw new InvalidOperationException("Set the harness output directory.");
        var port = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_CALENDAR_PORT") ?? "18306", System.Globalization.CultureInfo.InvariantCulture);
        var origin = "http://127.0.0.1:" + port;
        Directory.CreateDirectory(output);
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Server.Listeners = [new ListenerOptions { Bind = "127.0.0.1", Port = port }];
            options.Server.Hosts = ["127.0.0.1:" + port]; options.Server.Origins = [origin];
            options.Time.Zone = "America/New_York";
        }, services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
            new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
        factory.UseKestrel(_ => { }); factory.StartServer(); await factory.SeedAccountAsync();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Write });
        var documents = factory.Services.GetRequiredService<IDocumentRepository>();
        var authority = new IngestionAuthority(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "M4 calendar fixture", "api_key");
        const string calendar = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//SecondBrain//Synthetic browser fixture//EN
            BEGIN:VEVENT
            UID:planning
            DTSTAMP:20261001T000000Z
            DTSTART;TZID=America/New_York:20261025T090000
            DTEND;TZID=America/New_York:20261025T100000
            RRULE:FREQ=WEEKLY;COUNT=3
            SUMMARY:Calendar browser fixture
            DESCRIPTION:<script>window.calendarExecuted=true</script>
            LOCATION:Planning room
            ORGANIZER;CN=Grace Hopper:mailto:grace@example.com
            ATTENDEE;CN=Ada Lovelace:mailto:ada@example.com
            END:VEVENT
            END:VCALENDAR
            """;
        for (var i = 0; i < 2; i++)
        {
            var imported = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes(calendar), new("", "Calendar " + i), "fixture.ics", authority);
            var files = factory.Services.GetRequiredService<FileProcessingOrchestrator>(); files.Notify();
            imported = await files.WaitAsync(imported, 15, CancellationToken.None);
            Assert.Equal("indexed", imported.Document.Status);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { origin, temporaryTestAccount = true, realExtractor = true }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, deadline.Token);
    }
}
