using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Core.Storage;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Server.Sources;
using SecondBrain.Server.Tests.Support;
using SecondBrain.Storage;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class CalendarRefreshTests
{
    [Fact]
    public async Task HostedRefreshPreservesIdentityHandles304AndReconcilesAnEmptySnapshot()
    {
        await using var fixture = await Fixture.Create();
        fixture.Transport.Replies.Enqueue(new(Text: Feed("Initial"), Tag: "\"initial\""));
        await fixture.Register();
        await fixture.Wait(source => source.LastSuccessAt is not null);
        var id = (string)(await fixture.Scalar("SELECT document_id FROM calendar_members"))!;
        Assert.Equal("Initial", (string)(await fixture.Scalar("SELECT title FROM documents WHERE type='calendar_event'"))!);
        var changedAt = (await fixture.Source())!.LastChangeAt;
        fixture.Transport.Replies.Enqueue(new(Status: "not_modified"));
        await fixture.Refresh(source => source.LastSuccessAt > changedAt);
        Assert.Equal(1L, await fixture.Scalar("SELECT MAX(revision) FROM documents"));
        Assert.Equal(changedAt, (await fixture.Source())!.LastChangeAt);
        fixture.Transport.Replies.Enqueue(new(Text: Feed("Initial"), Tag: "\"same\""));
        var before = (await fixture.Source())!.LastSuccessAt;
        await fixture.Refresh(source => source.LastSuccessAt > before);
        Assert.Equal(1L, await fixture.Scalar("SELECT MAX(revision) FROM documents"));
        Assert.Equal("\"same\"", await fixture.Scalar("SELECT etag FROM calendar_subscriptions"));
        fixture.Transport.Replies.Enqueue(new(Text: Feed("Changed"), Tag: "\"changed\""));
        await fixture.Refresh(source => source.LastChangeAt > changedAt);
        Assert.Equal(id, await fixture.Scalar("SELECT document_id FROM calendar_members"));
        Assert.Equal("Changed", await fixture.Scalar("SELECT title FROM documents WHERE type='calendar_event'"));
        fixture.Transport.Replies.Enqueue(new(Text: "BEGIN:VCALENDAR\nVERSION:2.0\nEND:VCALENDAR\n"));
        await fixture.Refresh(_ => fixture.Transport.Calls >= 5, async () => (long)(await fixture.Scalar("SELECT COUNT(*) FROM event_occurrences", index: true))! == 0);
        Assert.Equal("removed_from_source", await fixture.Scalar("SELECT status FROM documents WHERE type='calendar_event'"));
        Assert.DoesNotContain("private-token", (string)(await fixture.Scalar("SELECT props_json FROM documents WHERE type='file'"))!);
    }

    [Fact]
    public async Task Initial304RetriesOnceAndRepeatedRejectedBytesDoNotReparseOrReplaceValidators()
    {
        await using var fixture = await Fixture.Create();
        fixture.Transport.Replies.Enqueue(new(Status: "not_modified"));
        fixture.Transport.Replies.Enqueue(new(Text: Feed("Accepted"), Tag: "\"accepted\""));
        await fixture.Register(); await fixture.Wait(source => source.LastSuccessAt is not null);
        Assert.Equal(2, fixture.Transport.Calls); Assert.Equal(1, fixture.Extractor!.Calls);
        const string invalid = "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:incomplete";
        fixture.Transport.Replies.Enqueue(new(Text: invalid, Tag: "\"bad\""));
        await fixture.Refresh(source => source.Error == "calendar_parse_failed");
        Assert.Equal(2, fixture.Extractor.Calls);
        Assert.Equal("\"accepted\"", await fixture.Scalar("SELECT etag FROM calendar_subscriptions"));
        Assert.Equal("Accepted", await fixture.Scalar("SELECT title FROM indexed_documents WHERE type='calendar_event'", index: true));
        fixture.Transport.Replies.Enqueue(new(Text: invalid));
        await fixture.Refresh(source => source.Error == "calendar_rejected_unchanged");
        Assert.Equal(2, fixture.Extractor.Calls);
        fixture.Transport.Replies.Enqueue(new(Text: Feed("Recovered"), Tag: "\"recovered\""));
        await fixture.Refresh(source => source.Error is null, async () => (string)(await fixture.Scalar("SELECT title FROM documents WHERE type='calendar_event'"))! == "Recovered");
        Assert.Equal(3, fixture.Extractor.Calls);
        Assert.Equal("current", (await fixture.Source())!.Freshness);
    }

    [Fact]
    public async Task PausingDuringDownloadFencesTheResultAndRemovalStopsFuturePolling()
    {
        await using var fixture = await Fixture.Create();
        fixture.Transport.Replies.Enqueue(new(Text: Feed("Accepted"))); await fixture.Register(); await fixture.Wait(source => source.LastSuccessAt is not null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Replies.Enqueue(new(Text: Feed("Must not publish"), Before: async () => { entered.SetResult(); await release.Task; }));
        fixture.Factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Repository.QueueAsync(fixture.Id, false, fixture.Actor); fixture.Worker.Notify();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Repository.UpdateAsync(fixture.Id, null, null, false, fixture.Actor);
        release.SetResult(); await Task.Delay(300);
        Assert.Equal("Accepted", await fixture.Scalar("SELECT title FROM indexed_documents WHERE type='calendar_event'", index: true));
        Assert.Equal("paused", (await fixture.Source())!.Freshness);
        await fixture.Repository.RemoveAsync(fixture.Id, fixture.Actor);
        fixture.Factory.Clock.Advance(TimeSpan.FromDays(1)); fixture.Worker.Notify(); await Task.Delay(500);
        Assert.Equal(2, fixture.Transport.Calls);
        Assert.Equal(0L, await fixture.Scalar("SELECT COUNT(*) FROM event_occurrences", index: true));
    }

    private static string Feed(string title) => "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:one\nDTSTAMP:20261001T000000Z\nDTSTART:20261025T100000Z\nDTEND:20261025T110000Z\nSUMMARY:" + title + "\nEND:VEVENT\nEND:VCALENDAR\n";
    private sealed record Reply(string? Text = null, string Status = "modified", string? Tag = null, Func<Task>? Before = null);
    private sealed class Transport(IServiceProvider services) : ICalendarDownloadTransport
    {
        internal readonly ConcurrentQueue<Reply> Replies = new(); private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public async ValueTask<CalendarDownloadResult> DownloadAsync(CalendarDownloadRequest request, CancellationToken token = default)
        {
            Interlocked.Increment(ref calls);
            if (!Replies.TryDequeue(out var reply)) return new("failed", Error: "calendar_transport");
            if (reply.Before is not null) await reply.Before().WaitAsync(token);
            if (reply.Status != "modified") return new(reply.Status);
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(reply.Text!));
            return new("modified", await services.GetRequiredService<IInputStager>().StageAsync(input, "calendar.ics", token), reply.Tag);
        }
    }
    private sealed class Counter(IExtractorClient inner) : IExtractorClient
    {
        private int calls; internal int Calls => Volatile.Read(ref calls);
        public ValueTask<ExtractionResult> ExtractAsync(Stream stream, string hint, CancellationToken token = default) => inner.ExtractAsync(stream, hint, token);
        public ValueTask<ExtractionResult> ExtractCalendarAsync(Stream stream, CalendarWindow window, CancellationToken token = default)
        { Interlocked.Increment(ref calls); return inner.ExtractCalendarAsync(stream, window, token); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal LaneDWebFactory Factory { get; }
        internal Transport Transport => Factory.Services.GetRequiredService<Transport>();
        internal Counter? Extractor => Factory.Services.GetRequiredService<IExtractorClient>() as Counter;
        internal CalendarSubscriptionOrchestrator Worker => Factory.Services.GetRequiredService<CalendarSubscriptionOrchestrator>();
        internal ICalendarSubscriptionRepository Repository => Factory.Services.GetRequiredService<ICalendarSubscriptionRepository>();
        internal IngestionAuthority Actor { get; private set; } = null!;
        internal string Id { get; private set; } = "";
        private Fixture()
        {
            Factory = new(options => { options.Time.Zone = "UTC"; options.Sources.CalendarAllowedOrigins = ["https://feed.test"]; }, services =>
            {
                services.AddSingleton<Transport>();
                services.Replace(ServiceDescriptor.Singleton<ICalendarDownloadTransport>(provider => provider.GetRequiredService<Transport>()));
                services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider => new Counter(new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
            });
        }
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture(); var key = await fixture.Factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin, Scope.Read, Scope.Write });
            fixture.Actor = new(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "fixture", "api_key"); return fixture;
        }
        internal async Task Register() { Id = (await Factory.Services.GetRequiredService<CalendarSubscriptionService>().CreateAsync("Shared fixture", "webcal://feed.test/private-token", 3600, true, Actor)).Id; Worker.Notify(); }
        internal ValueTask<CalendarSubscription?> Source() => Repository.GetAsync(Id);
        internal async Task Wait(Func<CalendarSubscription, bool> predicate, Func<Task<bool>>? extra = null)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (await Source() is not { } source || !predicate(source) || extra is not null && !await extra()) await Task.Delay(20, stop.Token);
        }
        internal async Task Refresh(Func<CalendarSubscription, bool> predicate, Func<Task<bool>>? extra = null)
        { Factory.Clock.Advance(TimeSpan.FromMinutes(1)); await Repository.QueueAsync(Id, false, Actor); Worker.Notify(); await Wait(predicate, extra); }
        internal async Task<object?> Scalar(string sql, bool index = false)
        {
            var store = index ? (IStoreHandle)Factory.Services.GetRequiredService<IIndexStore>() : Factory.Services.GetRequiredService<IStateStore>();
            await using var lease = await store.OpenReadConnectionAsync(); await using var command = lease.Connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
        }
        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }
}
