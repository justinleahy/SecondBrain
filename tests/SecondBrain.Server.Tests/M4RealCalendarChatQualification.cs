using System.Text.Json;
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
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Sources;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>Opt-in live calendar answer review; private input and answer never enter test output.</summary>
public sealed class M4RealCalendarChatQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task AskAboutLastWeeksMeetings()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Private socket qualification requires Unix.");
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_REAL_CALENDAR_CHAT"));
        var directory = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_LIVE_OUTPUT")!;
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        var url = await M4LiveCalendarQualification.ReadPrivateLink(directory, token);
        var link = CalendarLink.Parse(url);
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Time.Zone = "UTC";
            options.Sources.CalendarAllowedOrigins = [link.Origin];
            options.Providers["live-chat"] = new() { Kind = "openai_compatible", Endpoint = "http://127.0.0.1:8000/v1", Trusted = true };
            options.Privacy.TrustedServices.Add("http://127.0.0.1:8000");
            options.Models.Chat = new() { Provider = "live-chat", Model = "Qwen/Qwen3-8B", Reasoning = "low",
                Capabilities = new() { Tools = true, Streaming = false, ReasoningControl = true },
                Limits = new() { ContextTokens = 32768, MaxOutputTokens = 4096 } };
        }, services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
            new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
        await factory.SeedAccountAsync();
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin, Scope.Read, Scope.Write, Scope.Infer }, cancellationToken: token);
        var worker = factory.Services.GetRequiredService<CalendarSubscriptionOrchestrator>();
        await worker.StopAsync(token);
        var subscriptions = factory.Services.GetRequiredService<ICalendarSubscriptionRepository>();
        var authority = new IngestionAuthority(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "Private calendar answer qualification", "api_key");
        var source = await factory.Services.GetRequiredService<CalendarSubscriptionService>().CreateAsync("Private qualification", url, 3600, true, authority, token);
        await worker.ProcessAsync(Assert.Single(await subscriptions.ClaimDueAsync(token: token)), token);
        Assert.True((await subscriptions.GetAsync(source.Id, token))!.LastSuccessAt is not null, "The approved feed must import successfully.");
        var today = factory.Clock.GetUtcNow().UtcDateTime.Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var from = new DateTimeOffset(monday.AddDays(-7), TimeSpan.Zero);
        var to = new DateTimeOffset(monday, TimeSpan.Zero);
        var window = new TimeWindow(from.ToString("O"), to.ToString("O"));
        var page = await factory.Services.GetRequiredService<IRetrievalRepository>().QueryAsync(
            new(Type: "calendar_event", Occurred: window, Sort: "occurred_at", Direction: "asc", Limit: 50, Source: source.Id), authority, token);
        Assert.True(page.Records.Count > 0, "The real feed must contain meetings in the previous calendar week.");
        Assert.True(page.Records.All(record => DateTimeOffset.Parse(record.StartsAt!) >= from && DateTimeOffset.Parse(record.StartsAt!) < to));
        Assert.True(page.Records.Select(record => record.StartsAt).SequenceEqual(page.Records.Select(record => record.StartsAt).Order()));
        var coverage = Assert.IsType<CalendarCoverage>(page.Coverage);
        var actor = new AuthenticatedCredential(key.Record.Id, "api_key", key.Record.Generation, key.Record.AccountEpoch, key.Record.GrantedScopes, null);
        var started = await factory.Services.GetRequiredService<ChatService>().StartAsync(new(
            $"What meetings did I have last week ({from:yyyy-MM-dd} inclusive to {to:yyyy-MM-dd} exclusive, UTC)? List the first three chronologically with their dates and times, read and cite their records, and explain calendar coverage and any omitted meetings. Distinguish scheduled meetings from proof that I attended.",
            Mode: "ask", Filters: new(Type: "calendar_event", Source: source.Id, Occurred: window)), actor);
        var turns = factory.Services.GetRequiredService<IChatRepository>();
        ChatTurn turn;
        do { await Task.Delay(100, token); turn = await turns.GetTurnAsync(started.Turn.Id, actor, token); }
        while (turn.Status == "running");
        var resume = await turns.ResumeStateAsync(turn.Id, actor, token);
        var privateOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        await using (var review = new FileStream(Path.Combine(directory, "private-review.json"), privateOptions))
            await JsonSerializer.SerializeAsync(review, new { from, to, records = page.Records, coverage, answer = turn.Answer, calls = resume.Calls }, cancellationToken: token);
        await File.WriteAllTextAsync(Path.Combine(directory, "completion.json"), JsonSerializer.Serialize(new
        {
            turn.Status, turn.FailureCode, answerPresent = turn.Answer is not null,
            tools = resume.Calls.Select(call => new { call.Name, call.Status }),
            initialContextBytes = resume.InitialContext?.GetRawText().Length
        }), token);
        Assert.True(turn.Status == "complete", "The fresh local-model calendar answer must complete.");
        Assert.True(turn.Answer?.Citations.Count > 0, "The answer must cite real calendar records.");
        Assert.True(turn.Answer!.Citations.All(citation => citation.State == "current" && page.Records.Any(record => record.Id == citation.Source.DocumentId && record.Revision == citation.Source.Revision)),
            "Every citation must identify a current record in the requested real calendar week.");
        Assert.True(page.Records.Take(3).All(record => turn.Answer.Citations.Any(citation => citation.Source.DocumentId == record.Id)),
            "The first three chronological records must each have a current citation.");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            status = "passed_automated_checks_pending_semantic_review", from, to, queriedMeetings = page.Records.Count,
            page.Truncated, coverage = coverage.Status, citedRecords = turn.Answer.Citations.Select(c => c.Source.DocumentId).Distinct().Count(),
            freshLocalModel = "Qwen/Qwen3-8B", chronologicalQuery = true, currentCitationsInRequestedWeek = true,
            realTransport = true, realExtractor = true, isolatedTemporaryStore = true, hardenedDeployment = false,
            embeddingProvider = "controlled fixture; structured calendar query does not require semantic search", privateContentInEvidence = false
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    }
}
