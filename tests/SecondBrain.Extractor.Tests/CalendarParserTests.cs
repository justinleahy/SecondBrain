using SecondBrain.Extraction.Contracts;
using SecondBrain.Extractor;
using SecondBrain.Infrastructure.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SecondBrain.Extractor.Tests;

public sealed class CalendarParserTests
{
    private static readonly CalendarWindow October = new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", "America/New_York");

    [Fact]
    public void WeeklyMasterAndMovedCancelledExceptionHaveStableMembersAndEffectiveTimes()
    {
        var parsed = Parse(Event("series", "DTSTART;TZID=America/New_York:20261004T090000\nDTEND;TZID=America/New_York:20261004T100000\nRRULE:FREQ=WEEKLY;COUNT=3\nSUMMARY:Weekly") +
            Event("series", "RECURRENCE-ID;TZID=America/New_York:20261011T090000\nDTSTART;TZID=America/New_York:20261011T110000\nDTEND;TZID=America/New_York:20261011T120000\nSTATUS:CANCELLED\nSUMMARY:Moved cancellation"));
        Assert.Equal(2, parsed.Members.Count); Assert.Equal(3, parsed.Instances.Count);
        Assert.Equal(new[] { "2026-10-04T13:00:00.0000000+00:00", "2026-10-11T15:00:00.0000000+00:00", "2026-10-18T13:00:00.0000000+00:00" }, parsed.Instances.Select(item => item.StartsAt));
        var cancelled = parsed.Instances[1];
        Assert.Equal("cancelled", cancelled.Status); Assert.Equal("2026-10-11T13:00:00.0000000+00:00", cancelled.RecurrenceId);
        Assert.Equal("2026-10-11T16:00:00.0000000+00:00", cancelled.EndsAt);
    }

    [Fact]
    public void LaterStampWinsWithinContainerAndEmptyCalendarIsComplete()
    {
        var old = Event("same", "DTSTART:20261004T090000Z\nSUMMARY:Old", "20261001T090000Z");
        var newer = Event("same", "DTSTART:20261004T100000Z\nSUMMARY:New", "20261002T090000Z");
        var parsed = Parse(newer + old);
        Assert.Equal("New", Assert.Single(parsed.Members).Title);
        Assert.Equal("2026-10-04T10:00:00.0000000+00:00", Assert.Single(parsed.Instances).StartsAt);
        Assert.Empty(Parse("").Members); Assert.Empty(Parse("").Instances);
    }

    [Fact]
    public void AllDayAndFloatingValuesUseTheInstanceZoneAcrossDst()
    {
        var window = new CalendarWindow("2026-11-01T00:00:00Z", "2026-11-03T00:00:00Z", "America/New_York");
        var parsed = Parse(Event("all-day", "DTSTART;VALUE=DATE:20261101\nSUMMARY:All day") +
            Event("floating", "DTSTART:20261102T090000\nDURATION:PT1H\nSUMMARY:Floating"), window);
        var allDay = Assert.Single(parsed.Members, item => item.Uid == "all-day");
        Assert.True(allDay.Properties["all_day"].GetBoolean());
        Assert.Equal("2026-11-01T04:00:00.0000000+00:00", allDay.Properties["starts_at"].GetString());
        Assert.Equal("2026-11-02T05:00:00.0000000+00:00", allDay.Properties["ends_at"].GetString());
        var floating = Assert.Single(parsed.Members, item => item.Uid == "floating");
        Assert.Equal("floating", floating.Properties["tz"].GetString());
        Assert.Equal("2026-11-02T14:00:00.0000000+00:00", floating.Properties["starts_at"].GetString());
    }

    [Fact]
    public void RecurrenceDatesAndExclusionsKeepTheirEffectiveDurations()
    {
        var parsed = Parse(Event("series", "DTSTART:20261004T090000Z\nDTEND:20261004T100000Z\nRRULE:FREQ=WEEKLY;COUNT=3\nEXDATE:20261011T090000Z\nRDATE;VALUE=PERIOD:20261021T120000Z/20261021T140000Z"));
        Assert.Equal(3, parsed.Instances.Count);
        Assert.DoesNotContain(parsed.Instances, item => item.StartsAt.StartsWith("2026-10-11", StringComparison.Ordinal));
        Assert.Equal("2026-10-21T14:00:00.0000000+00:00", parsed.Instances[^1].EndsAt);
    }

    [Fact]
    public void ThisAndFutureExceptionMovesTheRemainingSeries()
    {
        var parsed = Parse(Event("series", "DTSTART:20261004T090000Z\nDTEND:20261004T100000Z\nRRULE:FREQ=WEEKLY;COUNT=3") +
            Event("series", "RECURRENCE-ID;RANGE=THISANDFUTURE:20261011T090000Z\nDTSTART:20261011T110000Z\nDTEND:20261011T120000Z"));
        Assert.Equal(new[] { "2026-10-04T09:00:00.0000000+00:00", "2026-10-11T11:00:00.0000000+00:00", "2026-10-18T11:00:00.0000000+00:00" }, parsed.Instances.Select(item => item.StartsAt));
    }

    [Fact]
    public void WindowIncludesEventsThatBeginBeforeItAndFiltersAtTheEnd()
    {
        var window = new CalendarWindow("2026-10-10T00:00:00Z", "2026-10-11T00:00:00Z", "UTC");
        var parsed = Parse(Event("overlap", "DTSTART:20261001T000000Z\nDTEND:20261010T010000Z") +
            Event("after", "DTSTART:20261011T000000Z\nDTEND:20261011T010000Z"), window);
        Assert.Equal("overlap", Assert.Single(parsed.Instances).Uid);
    }

    [Fact]
    public void RecurrenceBombStopsAtTheCountBound()
    {
        var error = Assert.Throws<CalendarExtractionException>(() => Parse(Event("bomb", "DTSTART:20261001T000000Z\nDURATION:PT1S\nRRULE:FREQ=SECONDLY")));
        Assert.True(error.IsBound); Assert.Equal("calendar_occurrences", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>not a calendar</html>")]
    [InlineData("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:incomplete\nDTSTART:20261001T000000Z\nEND:VEVENT")]
    [InlineData("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nDTSTART:20261001T000000Z\nEND:VEVENT\nEND:VCALENDAR")]
    public void MalformedOrTruncatedInputCannotProduceAnAuthoritativeEmptySnapshot(string text)
    {
        var error = Assert.Throws<CalendarExtractionException>(() => CalendarParser.Parse(text, October, CancellationToken.None));
        Assert.Equal("malformed_calendar", error.Code);
    }

    [Fact]
    public void AddressesNormalizeAndAttachmentsAreNotOpened()
    {
        var parsed = Parse(Event("people", "DTSTART:20261004T100000Z\nATTENDEE;CN=Ada:mailto:Ada@Example.COM\nORGANIZER:mailto:HOST@Example.COM\nATTACH:http://127.0.0.1:9/no-resource\nSUMMARY:Meeting"));
        var member = Assert.Single(parsed.Members);
        Assert.Equal("ada@example.com", member.Properties["attendees"][0].GetString());
        Assert.Equal("host@example.com", member.Properties["organizer"].GetString());
        Assert.Contains(member.People!, person => person.Role == "attendee" && person.Identifier == "ada@example.com" && person.DisplayName == "Ada");
        Assert.Contains(member.Properties["ical_properties"].EnumerateArray(), line => line.GetString()!.StartsWith("ATTACH:", StringComparison.Ordinal));
        Assert.DoesNotContain("no-resource", member.Text);
    }

    [Fact]
    public void SparseCancelledExceptionInheritsItsMasterAndKeepsOriginalFields()
    {
        var parsed = Parse(Event("series", "DTSTART:20261004T090000Z\nDTEND:20261004T100000Z\nRRULE:FREQ=WEEKLY;COUNT=3\nSUMMARY:Team meeting\nLOCATION:Room 2\nATTENDEE;CN=\"Ada: Lovelace\":mailto:ADA@example.com\nX-CUSTOM:unchanged") +
            Event("series", "RECURRENCE-ID:20261011T090000Z\nSTATUS:CANCELLED"));
        var cancelled = Assert.Single(parsed.Members, member => member.RecurrenceId.Length != 0);
        Assert.Equal("Team meeting", cancelled.Title);
        Assert.Equal("Room 2", cancelled.Properties["location"].GetString());
        Assert.Equal("Ada: Lovelace", Assert.Single(cancelled.People!).DisplayName);
        Assert.Equal("2026-10-11T10:00:00.0000000+00:00", Assert.Single(parsed.Instances, item => item.Status == "cancelled").EndsAt);
        Assert.DoesNotContain(cancelled.Properties["ical_properties"].EnumerateArray(), line => line.GetString()!.StartsWith("SUMMARY:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("20261101T013000", "2026-11-01T05:30:00.0000000+00:00", "2026-11-01T00:00:00Z", "2026-11-02T00:00:00Z")]
    [InlineData("20260308T023000", "2026-03-08T07:30:00.0000000+00:00", "2026-03-08T00:00:00Z", "2026-03-09T00:00:00Z")]
    public void FloatingFoldAndGapUseRfcFirstOccurrenceAndPreGapOffset(string local, string expected, string from, string to)
    {
        var parsed = Parse(Event("floating", "DTSTART:" + local + "\nDURATION:PT1H"), new(from, to, "America/New_York"));
        Assert.Equal(expected, Assert.Single(parsed.Members).Properties["starts_at"].GetString());
    }

    [Fact]
    public void CalendarDefinedZoneHandlesDstAndDoesNotLeakIntoTheNextParse()
    {
        const string zone = """
            BEGIN:VTIMEZONE
            TZID:Fixture/Private
            BEGIN:STANDARD
            DTSTART:19701101T020000
            TZOFFSETFROM:-0400
            TZOFFSETTO:-0500
            RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU
            END:STANDARD
            BEGIN:DAYLIGHT
            DTSTART:19700308T020000
            TZOFFSETFROM:-0500
            TZOFFSETTO:-0400
            RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU
            END:DAYLIGHT
            END:VTIMEZONE
            """;
        var events = Event("custom", "DTSTART;TZID=Fixture/Private:20261101T013000\nDTEND;TZID=Fixture/Private:20261101T023000\nRRULE:FREQ=DAILY;COUNT=2");
        var parsed = Parse(zone + "\n" + events, new("2026-11-01T00:00:00Z", "2026-11-03T00:00:00Z", "UTC"));
        Assert.Equal("2026-11-01T05:30:00.0000000+00:00", parsed.Instances[0].StartsAt);
        Assert.Equal("2026-11-01T07:30:00.0000000+00:00", parsed.Instances[0].EndsAt);
        Assert.Equal("2026-11-02T06:30:00.0000000+00:00", parsed.Instances[1].StartsAt);
        Assert.Throws<CalendarExtractionException>(() => Parse(events));
    }

    [Fact]
    public async Task DescriptorProtocolMaterializesOnlyTheExplicitWindowAndZone()
    {
        var socket = Path.Combine(Path.GetTempPath(), "calendar-wire-" + Guid.NewGuid().ToString("N"));
        var path = Path.GetTempFileName();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new ExtractorSocketServer(new() { SocketPath = socket }, NullLogger<ExtractorSocketServer>.Instance);
        var running = server.RunAsync(stop.Token);
        try
        {
            await File.WriteAllTextAsync(path, "BEGIN:VCALENDAR\nVERSION:2.0\n" + Event("floating", "DTSTART:20261004T090000\nDTEND:20261004T100000\nRRULE:FREQ=WEEKLY;COUNT=10") + "END:VCALENDAR\n", stop.Token);
            await using var file = File.OpenRead(path);
            var result = await new DescriptorExtractorClient(socket).ExtractCalendarAsync(file, October, stop.Token);
            Assert.Equal("extracted", result.Status); Assert.Equal("calendar", result.PhysicalFormat);
            Assert.NotNull(result.Calendar);
            Assert.Equal(4, result.Calendar.Instances.Count);
            Assert.Equal("2026-10-04T13:00:00.0000000+00:00", result.Calendar.Instances[0].StartsAt);
            Assert.Equal("America/New_York", result.Calendar.Window.TimeZone);
        }
        finally { File.Delete(path); stop.Cancel(); await running; }
    }

    private static CalendarSnapshot Parse(string events, CalendarWindow? window = null) =>
        CalendarParser.Parse("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//SecondBrain//Synthetic test//EN\n" + events + "END:VCALENDAR\n", window ?? October, CancellationToken.None);

    private static string Event(string uid, string body, string stamp = "20261001T000000Z") =>
        "BEGIN:VEVENT\nUID:" + uid + "\nDTSTAMP:" + stamp + "\n" + body + "\nEND:VEVENT\n";
}
