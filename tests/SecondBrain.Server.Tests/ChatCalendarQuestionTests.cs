using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Chat;
using SecondBrain.MockProvider;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed partial class ChatWorkflowTests
{
    [Fact]
    public async Task CalendarToolAcceptsOccurrenceStartAliasAndRetainsOriginalCallArguments()
    {
        await using var factory = BufferedFactory();
        var actor = await CreateActorAsync(factory);
        var calls = 0;
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = async context =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                await ToolResponseAsync(context, "query_records", new { query = new
                {
                    Type = "calendar_event", Sort = "starts_at", Direction = "asc", Limit = 3,
                    Predicates = new[] { new { Field = "starts_at", Op = "gte", Value = "2026-10-12T04:00:00+04:00" } }
                } });
            else await TextResponseAsync(context, "No matching records in this fixture.");
        };
        var started = await factory.Services.GetRequiredService<ChatService>().StartAsync(new("List my calendar", Filters: new(Type: "calendar_event", Source: "fixed-source")), actor);
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        Assert.Equal("complete", (await FinishAsync(repository, started.Turn.Id, actor)).Status);
        var recorded = await repository.ResumeStateAsync(started.Turn.Id, actor);
        var call = Assert.Single(recorded.Calls, item => item.Arguments.GetProperty("Sort").GetString() == "starts_at");
        Assert.Equal("committed", call.Status);
        Assert.Equal("starts_at", call.Arguments.GetProperty("Predicates")[0].GetProperty("Field").GetString());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("What meetings did I have last week?", null, "2026-10-26T04:00:00.0000000Z", "2026-11-02T05:00:00.0000000Z")]
    [InlineData("What meetings did I have last week?", "transcript", null, null)]
    [InlineData("List my calendar", "calendar_event", null, null)]
    public async Task CalendarQuestionsStartWithOrderedOccurrenceMetadataAndRespectExplicitTypes(string prompt, string? type, string? from, string? to)
    {
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Time.Zone = "America/New_York";
            options.Models.Chat!.Capabilities = options.Models.Chat.Capabilities with { Streaming = false };
            options.Models.Chat.Limits = options.Models.Chat.Limits with { ContextTokens = 32768 };
        });
        factory.Clock.SetUtcNow(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        var actor = await CreateActorAsync(factory);
        factory.MockProvider.Services.GetRequiredService<MockProviderState>().ChatResponse = context => TextResponseAsync(context, "No matching records in this fixture.");
        var started = await factory.Services.GetRequiredService<ChatService>().StartAsync(new(prompt, Filters: type is null ? null : new(Type: type)), actor);
        var repository = factory.Services.GetRequiredService<IChatRepository>();
        Assert.Equal("complete", (await FinishAsync(repository, started.Turn.Id, actor)).Status);
        var recorded = await repository.ResumeStateAsync(started.Turn.Id, actor);
        var first = Assert.Single(recorded.Calls);
        if (type == "transcript")
        {
            Assert.Equal("search", first.Name);
            return;
        }
        Assert.Equal("query_records", first.Name);
        var query = first.Arguments.Deserialize<SecondBrain.Core.Retrieval.QueryRequest>();
        Assert.Equal("calendar_event", query!.Type);
        Assert.Equal("occurred_at", query.Sort);
        Assert.Equal("asc", query.Direction);
        Assert.Equal(from, query.Occurred?.From);
        Assert.Equal(to, query.Occurred?.To);
        Assert.Contains("calendar_coverage_partial", recorded.InitialContext!.Value.GetRawText());
    }
}
