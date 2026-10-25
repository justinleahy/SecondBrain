using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Calendar = Ical.Net.Calendar;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Extractor;

/// <summary>Offline complete-container parsing. Nothing in a calendar grants authority to load a resource.</summary>
public static class CalendarParser
{
    private static readonly SemaphoreSlim ResolverGate = new(1, 1);
    public const int MaximumMembers = 10000;
    public const int MaximumInstances = 100000;

    public static CalendarSnapshot Parse(string source, CalendarWindow window, CancellationToken token)
    {
        ResolverGate.Wait(token);
        try { return ParseCore(source, window, token); }
        finally { ResolverGate.Release(); }
    }

    private static CalendarSnapshot ParseCore(string source, CalendarWindow window, CancellationToken token)
    {
        var (from, to, zone) = window.Validate();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var stamps = Preflight(source, token);
            var calendar = Calendar.Load(source) ?? throw Malformed();
            using var timeZones = new CalendarTimeZones(calendar, CheckTime);
            var parsed = calendar.Children.OfType<CalendarEvent>().ToArray();
            if (parsed.Length != stamps.Count) throw Malformed();
            var selected = parsed.Select((item, position) => new
            {
                Event = item, Position = position,
                Key = Key(item), Stamp = stamps[position].HasStamp ? item.DtStamp?.AsUtc ?? DateTime.MinValue : DateTime.MinValue
            }).GroupBy(item => item.Key, StringComparer.Ordinal)
              .Select(group => group.OrderBy(item => item.Stamp).ThenBy(item => item.Position).Last())
              .OrderBy(item => item.Position).ToArray();
            foreach (var item in parsed) calendar.Children.Remove(item);
            foreach (var item in selected) calendar.Children.Add(item.Event);

            var masters = selected.Where(item => item.Event.RecurrenceIdentifier is null).ToDictionary(item => item.Event.Uid!, item => item.Event, StringComparer.Ordinal);
            foreach (var item in selected.Where(item => item.Event.RecurrenceIdentifier is not null))
            {
                var entry = item.Event;
                if (entry.DtStart is null && entry.Status?.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true)
                    entry.DtStart = entry.RecurrenceIdentifier!.StartTime;
                if (!masters.TryGetValue(entry.Uid!, out var master)) continue;
                entry.Summary ??= master.Summary;
                entry.Description ??= master.Description;
                entry.Location ??= master.Location;
                entry.Organizer ??= master.Organizer;
                if (!entry.Properties.ContainsKey("ATTENDEE")) entry.Attendees = master.Attendees.ToList();
                if (entry.DtEnd is null && entry.Duration is null) entry.Duration = master.EffectiveDuration;
            }

            var members = new List<CalendarMember>();
            var byKey = new Dictionary<string, CalendarMember>(StringComparer.Ordinal);
            var longest = TimeSpan.Zero;
            var textBytes = 0;
            foreach (var item in selected)
            {
                CheckTime();
                var entry = item.Event;
                if (entry.DtStart is null)
                {
                    if (entry.Status?.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) != true || entry.RecurrenceIdentifier is null) throw Malformed();
                    entry.DtStart = entry.RecurrenceIdentifier.StartTime;
                }
                var start = Utc(entry.DtStart, zone);
                var endValue = entry.DtEnd ?? new Period(entry.DtStart, entry.EffectiveDuration).EffectiveEndTime ?? entry.DtStart;
                var end = Utc(endValue, zone);
                if (end < start) throw Malformed();
                if (end - start > longest) longest = end - start;
                var title = entry.Summary ?? "Untitled event";
                if (title.Length > 4096 || entry.Description?.Length > 131072 || entry.Location?.Length > 4096) throw Bound("calendar_metadata");
                var text = string.Join('\n', new[] { title, entry.Description, entry.Location }.Where(value => !string.IsNullOrWhiteSpace(value)));
                textBytes = checked(textBytes + Encoding.UTF8.GetByteCount(text));
                if (textBytes > DocumentParser.MaximumTextBytes) throw Bound("calendar_text");
                var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["uid"] = JsonSerializer.SerializeToElement(entry.Uid),
                    ["recurrence_id"] = JsonSerializer.SerializeToElement(RecurrenceId(entry)),
                    ["starts_at"] = JsonSerializer.SerializeToElement(Format(start)),
                    ["ends_at"] = JsonSerializer.SerializeToElement(Format(end)),
                    ["ends_basis"] = JsonSerializer.SerializeToElement(entry.DtEnd is not null ? "dtend" : entry.Duration is not null ? "duration" : entry.DtStart.HasTime ? "start" : "next_day"),
                    ["all_day"] = JsonSerializer.SerializeToElement(!entry.DtStart.HasTime),
                    ["tz"] = JsonSerializer.SerializeToElement(!entry.DtStart.HasTime ? zone.Id : entry.DtStart.IsFloating ? "floating" : entry.DtStart.TzId),
                    ["status"] = JsonSerializer.SerializeToElement(Status(entry)),
                    ["sequence"] = JsonSerializer.SerializeToElement(entry.Sequence)
                };
                if (entry.Attendees.Count > 1000) throw Bound("calendar_people");
                props["attendees"] = JsonSerializer.SerializeToElement(entry.Attendees.Select(person => Person(person.Value ?? throw Malformed())).Distinct(StringComparer.Ordinal).ToArray());
                if (entry.Organizer?.Value is { } organizer) props["organizer"] = JsonSerializer.SerializeToElement(Person(organizer));
                if (entry.Location is not null) props["location"] = JsonSerializer.SerializeToElement(entry.Location);
                if (entry.RecurrenceRule is { } rule) props["rrule"] = JsonSerializer.SerializeToElement(rule.ToString());
                props["ical_properties"] = JsonSerializer.SerializeToElement(stamps[item.Position].Lines);
                var people = entry.Attendees.Select(person => new CalendarPerson("attendee", Person(person.Value ?? throw Malformed()), DisplayName(person.CommonName, person.Value!))).ToList();
                if (entry.Organizer?.Value is { } host) people.Add(new("organizer", Person(host), DisplayName(entry.Organizer.CommonName, host)));
                var member = new CalendarMember(entry.Uid!, RecurrenceId(entry), title, text, props, people);
                members.Add(member); byKey.Add(item.Key, member);
            }

            // The v5 evaluator expands recurrence rules, but does not consistently apply
            // RANGE=THISANDFUTURE or RDATE period durations. Reconcile these explicitly.
            var shifts = selected.Where(item => item.Event.RecurrenceIdentifier?.Range == RecurrenceRange.ThisAndFuture)
                .Select(item => (item.Event.DtStart!.Value - item.Event.RecurrenceIdentifier!.StartTime.Value).Duration());
            var padding = longest + shifts.DefaultIfEmpty(TimeSpan.Zero).Max() + TimeSpan.FromDays(2);
            var first = from.UtcDateTime.Ticks > padding.Ticks ? from.UtcDateTime - padding : DateTime.MinValue;
            var stop = to.UtcDateTime.Ticks < DateTime.MaxValue.Ticks - padding.Ticks ? to.UtcDateTime + padding : DateTime.MaxValue;
            var instances = new List<CalendarInstance>();
            var evaluated = 0;
            foreach (var series in selected.GroupBy(item => item.Event.Uid, StringComparer.Ordinal))
            {
                var master = series.Select(item => item.Event).FirstOrDefault(entry => entry.RecurrenceIdentifier is null);
                var exceptions = series.Select(item => item.Event).Where(entry => entry.RecurrenceIdentifier is not null).ToArray();
                if (master is not null)
                {
                    var exact = exceptions.ToDictionary(RecurrenceId, StringComparer.Ordinal);
                    var ranges = exceptions.Where(entry => entry.RecurrenceIdentifier!.Range == RecurrenceRange.ThisAndFuture)
                        .OrderBy(entry => Utc(entry.RecurrenceIdentifier!.StartTime, zone)).ToArray();
                    var periods = master.RecurrenceDates.GetAllPeriods().ToDictionary(period => TimeKey(period.StartTime), StringComparer.Ordinal);
                    foreach (var occurrence in master.GetOccurrences(new CalDateTime(first, "UTC"), new EvaluationOptions { MaxUnmatchedIncrementsLimit = 1000 }))
                    {
                        CheckWork();
                        var original = occurrence.Period.StartTime;
                        var instant = Utc(original, zone);
                        if (instant.UtcDateTime > stop) break;
                        if (exact.TryGetValue(TimeKey(original), out var changed) && changed.RecurrenceIdentifier!.Range == RecurrenceRange.ThisInstance) continue;
                        var range = ranges.LastOrDefault(entry => Utc(entry.RecurrenceIdentifier!.StartTime, zone) <= instant);
                        if (range is null)
                            Emit(master, periods.GetValueOrDefault(TimeKey(original)) ?? occurrence.Period);
                        else
                        {
                            var shift = range.DtStart!.Value - range.RecurrenceIdentifier!.StartTime.Value;
                            var start = new CalDateTime(original.Value + shift, range.DtStart.TzId, range.DtStart.HasTime);
                            Emit(range, new Period(start, range.EffectiveDuration));
                        }
                    }
                }
                foreach (var entry in exceptions.Where(entry => master is null || entry.RecurrenceIdentifier!.Range == RecurrenceRange.ThisInstance))
                {
                    CheckWork();
                    Emit(entry, new Period(entry.DtStart!, entry.EffectiveDuration));
                }
            }

            void CheckWork()
            {
                CheckTime();
                if (++evaluated > window.MaxOccurrences) throw Bound("calendar_occurrences");
            }
            void Emit(CalendarEvent entry, Period period)
            {
                if (!byKey.ContainsKey(Key(entry))) throw Malformed();
                var start = Utc(period.StartTime, zone);
                var end = Utc(period.EffectiveEndTime ?? period.StartTime, zone);
                if (end < start) throw Malformed();
                if (start >= to || end <= from) return;
                instances.Add(new(entry.Uid!, RecurrenceId(entry), Format(start), Format(end), Status(entry)));
            }
            return new(new(Format(from), Format(to), zone.Id, window.MaxOccurrences), members,
                instances.OrderBy(item => item.StartsAt, StringComparer.Ordinal).ThenBy(item => item.Uid, StringComparer.Ordinal).ThenBy(item => item.RecurrenceId, StringComparer.Ordinal).ToArray());
        }
        catch (CalendarExtractionException) { throw; }
        catch (EvaluationLimitExceededException) { throw Bound("calendar_recurrence_work"); }
        catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
        { throw Malformed(); }

        void CheckTime()
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(30)) throw Bound("calendar_deadline");
        }
    }

    private sealed record RawEvent(bool HasStamp, IReadOnlyList<string> Lines);
    private static List<RawEvent> Preflight(string source, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(source) > DocumentParser.MaximumTextBytes) throw Bound("calendar_bytes");
        if (source.Contains('\0')) throw Malformed();
        var physical = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (physical.Length > 200000) throw Bound("calendar_lines");
        var lines = new List<string>();
        foreach (var line in physical)
        {
            token.ThrowIfCancellationRequested();
            if (line.Length > 65536) throw Bound("calendar_line");
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (lines.Count == 0) throw Malformed();
                if (lines[^1].Length + line.Length > 65536) throw Bound("calendar_line");
                lines[^1] += line[1..];
            }
            else if (line.Length != 0) lines.Add(line);
        }
        var stack = new Stack<string>(); var stamps = new List<RawEvent>(); var eventLines = new List<string>();
        var rootSeen = false; var versionSeen = false; var uid = false; var stamp = false; var rules = 0;
        foreach (var line in lines)
        {
            token.ThrowIfCancellationRequested();
            var quoted = false; var colon = -1;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') quoted = !quoted;
                else if (line[i] == ':' && !quoted) { colon = i; break; }
            }
            if (colon < 1) throw Malformed();
            var name = line[..colon].Split(';', 2)[0].ToUpperInvariant();
            var value = line[(colon + 1)..];
            if (name == "BEGIN")
            {
                var component = value.ToUpperInvariant();
                if (stack.Count >= 16) throw Bound("calendar_depth");
                if (stack.Count == 0)
                {
                    if (rootSeen || component != "VCALENDAR") throw Malformed();
                    rootSeen = true;
                }
                else if (stack.Count == 1 && component is not ("VEVENT" or "VTIMEZONE")) throw Malformed();
                if (component == "VEVENT")
                {
                    if (stack.Count != 1 || stack.Peek() != "VCALENDAR") throw Malformed();
                    uid = false; stamp = false; rules = 0; eventLines = [];
                }
                stack.Push(component);
            }
            else if (name == "END")
            {
                if (!stack.TryPop(out var component) || component != value.ToUpperInvariant()) throw Malformed();
                if (component == "VEVENT")
                {
                    if (!uid) throw Malformed();
                    if (stamps.Count >= MaximumMembers) throw Bound("calendar_members");
                    stamps.Add(new(stamp, eventLines));
                }
            }
            else
            {
                if (stack.Count == 0) throw Malformed();
                if (stack.Count == 1 && name == "VERSION") { if (value != "2.0") throw Malformed(); versionSeen = true; }
                if (stack.Count == 2 && stack.Peek() == "VEVENT")
                {
                    eventLines.Add(line);
                    if (name == "UID") { if (uid || string.IsNullOrWhiteSpace(value)) throw Malformed(); uid = true; }
                    if (name == "DTSTAMP") stamp = true;
                    if (name == "RRULE" && ++rules > 1) throw Bound("calendar_recurrence_rules");
                }
            }
        }
        if (!rootSeen || !versionSeen || stack.Count != 0) throw Malformed();
        return stamps;
    }

    private static string Key(CalendarEvent entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Uid) || entry.Uid.Length > 2048 || entry.Uid.Any(char.IsControl)) throw Malformed();
        return entry.Uid + "\0" + RecurrenceId(entry);
    }
    private static string RecurrenceId(CalendarEvent entry) => entry.RecurrenceIdentifier?.StartTime is not { } value ? "" : TimeKey(value);
    private static string TimeKey(CalDateTime value) => value.HasTime && !value.IsFloating ? Format(new DateTimeOffset(value.AsUtc)) : value.Value.ToString(value.HasTime ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTimeOffset Utc(CalDateTime value, TimeZoneInfo zone) => value.IsFloating || !value.HasTime
        ? NodaTime.LocalDateTime.FromDateTime(value.Value).InZoneLeniently(NodaTime.TimeZones.BclDateTimeZone.FromTimeZoneInfo(zone)).ToDateTimeOffset()
        : new DateTimeOffset(value.AsUtc);
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Status(CalendarEvent value) => value.Status?.ToUpperInvariant() switch
    {
        null or "" or "CONFIRMED" => "confirmed", "TENTATIVE" => "tentative", "CANCELLED" => "cancelled", _ => throw Malformed()
    };
    private static string Person(Uri value)
    {
        var result = (value.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(value.OriginalString[7..].Split('?', 2)[0])
            : value.OriginalString).Trim().ToLowerInvariant();
        if (result.Length > 2048 || result.Any(char.IsControl)) throw Bound("calendar_people");
        return result;
    }
    private static string DisplayName(string? name, Uri value)
    {
        var display = string.IsNullOrWhiteSpace(name) ? Person(value) : name.Trim();
        if (display.Length > 2048 || display.Any(char.IsControl)) throw Bound("calendar_people");
        return display;
    }
    private static CalendarExtractionException Malformed() => new("malformed_calendar", false);
    private static CalendarExtractionException Bound(string code) => new(code, true);
}

public sealed class CalendarExtractionException(string code, bool isBound) : Exception(code)
{
    public string Code { get; } = code;
    public bool IsBound { get; } = isBound;
}
