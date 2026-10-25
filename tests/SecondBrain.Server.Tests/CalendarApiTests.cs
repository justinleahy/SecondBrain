using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.MockProvider;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class CalendarApiTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(12, 2, false)]
    public async Task CalendarAnswerReceivesVerifiedPassagesWithinItsReadingBudget(int maximumDocuments, int expectedSources, bool truncated)
    {
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Time.Zone = "UTC"; options.Calendar.PastMonths = 120; options.Calendar.FutureMonths = 120;
            options.Assistant.MaxReadDocuments = maximumDocuments;
            options.Models.Chat!.Capabilities = options.Models.Chat.Capabilities with { Streaming = false };
            options.Models.Chat.Limits = options.Models.Chat.Limits with { ContextTokens = 32768 };
        }, services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
            new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
        await factory.SeedAccountAsync();
        using var client = await ClientAsync(factory);
        await Upload(client, Calendar, "first.ics");
        await Upload(client, Calendar, "second.ics");
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            var evidence = request.RootElement.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("content").GetString())
                .Single(text => text?.StartsWith("Retrieved evidence", StringComparison.Ordinal) == true)!;
            Assert.Contains("Prepare the weekly plan.", evidence);
            Assert.DoesNotContain("ical_properties", evidence);
            await context.Response.WriteAsJsonAsync(new { id = "calendar-test", @object = "chat.completion", model = "mock-chat",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "Prepare the weekly plan [1]." }, finish_reason = "stop" } } });
        };
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read, Scope.Infer });
        var actor = new AuthenticatedCredential(key.Record.Id, "api_key", key.Record.Generation, key.Record.AccountEpoch, key.Record.GrantedScopes, null);
        var started = await factory.Services.GetRequiredService<ChatService>().StartAsync(new("List my calendar", Mode: "ask",
            Filters: new(Type: "calendar_event", Occurred: new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z"))), actor);
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        ChatTurn turn;
        do { await Task.Delay(25); turn = await repository.GetTurnAsync(started.Turn.Id, actor); }
        while (turn.Status == "running" && DateTimeOffset.UtcNow < deadline);
        Assert.Equal("complete", turn.Status);
        Assert.Equal(expectedSources, (await repository.SourcesAsync(turn.Id, actor)).Count);
        Assert.Equal(truncated, (turn.Answer!.Warnings ?? []).Contains("calendar_citations_truncated"));
        Assert.Single(turn.Answer.Citations);
        Assert.Equal("current", turn.Answer.Citations[0].State);
    }

    [Fact]
    public async Task UploadedCalendarUsesRealSandboxAndReturnsOccurrencesCancellationCoverageAndDuplicates()
    {
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Time.Zone = "UTC"; options.Calendar.PastMonths = 120; options.Calendar.FutureMonths = 120;
        }, services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
            new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
        using var client = await ClientAsync(factory);
        var first = await Upload(client, Calendar, "calendar.ics");
        Assert.Equal("indexed", first.Document.Status);
        var page = await Query(client, new(Type: "calendar_event", Occurred: new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z"), Direction: "asc"));
        Assert.Equal(2, page.Records.Count);
        Assert.Equal(page.Records[0].Id, page.Records[1].Id);
        Assert.NotEqual(page.Records[0].InstanceId, page.Records[1].InstanceId);
        Assert.All(page.Records, record => Assert.Equal("confirmed", record.Status));
        var all = await Query(client, new(Type: "calendar_event", Occurred: new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z"), Direction: "asc", IncludeCancelled: true));
        Assert.Equal(3, all.Records.Count); Assert.Single(all.Records, record => record.Status == "cancelled");
        var partial = await Query(client, new(Type: "calendar_event", Occurred: new("1900-01-01T00:00:00Z", "2100-01-01T00:00:00Z")));
        Assert.Contains("partial", System.Text.Json.JsonSerializer.Serialize(partial.Coverage));
        await Upload(client, Calendar, "another.ics");
        var metadata = await client.GetFromJsonAsync<DocumentDetails>("/v1/documents/" + page.Records[0].Id + "/metadata");
        Assert.NotNull(metadata!.Calendar); Assert.Equal(1, metadata.Calendar.DuplicateCalendars);
        Assert.Equal(2, metadata.Calendar.Occurrences.Count);
        var invalid = await Upload(client, "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:truncated", "invalid.ics");
        Assert.Equal("failed", invalid.Document.Status);
        Assert.Equal(4, (await Query(client, new(Type: "calendar_event"))).Records.Count);
    }

    private static async Task<QueryPage> Query(HttpClient client, QueryRequest request)
    {
        using var response = await client.PostAsJsonAsync("/v1/query", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QueryPage>())!;
    }
    private static async Task<CaptureResult> Upload(HttpClient client, string body, string name)
    {
        using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(body)), "file", name);
        using var response = await client.PostAsync("/v1/documents?wait=15", form);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Accepted });
        return (await response.Content.ReadFromJsonAsync<CaptureResult>())!;
    }
    private static async Task<HttpClient> ClientAsync(LaneDWebFactory factory)
    {
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var key = factory.Services.GetRequiredService<CredentialFactory>().CreateApiKey("calendar-test", new HashSet<Scope> { Scope.Read, Scope.Write }, await auth.GetEpochAsync());
        await auth.AddAsync(key.Record);
        var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext); return client;
    }
    private const string Calendar = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//SecondBrain//Synthetic acceptance//EN
        BEGIN:VEVENT
        UID:weekly
        DTSTAMP:20261001T000000Z
        DTSTART:20261004T100000Z
        DTEND:20261004T110000Z
        RRULE:FREQ=WEEKLY;COUNT=3
        SUMMARY:Weekly planning
        DESCRIPTION:Prepare the weekly plan.
        ATTENDEE:mailto:ada@example.com
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly
        DTSTAMP:20261002T000000Z
        RECURRENCE-ID:20261011T100000Z
        DTSTART:20261011T120000Z
        DTEND:20261011T130000Z
        STATUS:CANCELLED
        SUMMARY:Cancelled planning
        END:VEVENT
        END:VCALENDAR
        """;
}
