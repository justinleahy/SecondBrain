using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using NodaTime;
using NodaTime.TimeZones;

namespace SecondBrain.Extractor;

/// <summary>Scoped v5 resolver. CalendarParser serializes its use of Ical.Net's process-global resolver.</summary>
internal sealed class CalendarTimeZones : IDisposable
{
    private readonly Func<string, DateTimeZone> previous;

    internal CalendarTimeZones(Calendar calendar, Action check)
    {
        previous = TimeZoneResolvers.TimeZoneResolver;
        var definitions = calendar.TimeZones.ToArray();
        if (definitions.Length > 128) throw new CalendarExtractionException("calendar_time_zones", true);
        var zones = definitions.ToDictionary(zone => zone.TzId ?? throw new InvalidDataException(),
            zone => (DateTimeZone)new DefinedZone(zone, check), StringComparer.Ordinal);
        TimeZoneResolvers.TimeZoneResolver = id => zones.TryGetValue(id, out var zone) ? zone : previous(id);
    }

    public void Dispose() => TimeZoneResolvers.TimeZoneResolver = previous;

    private sealed class DefinedZone : DateTimeZone
    {
        private readonly Ical.Net.VTimeZoneInfo[] rules;
        private readonly Action check;
        private Transition[] transitions = [];
        private int throughYear;

        internal DefinedZone(VTimeZone source, Action check)
            : base(source.TzId!, false, Minimum(source), Maximum(source))
        {
            this.check = check;
            if (source.TzId is not { Length: > 0 and <= 128 } || source.TzId.Any(char.IsControl) || source.TimeZoneInfos.Count is < 1 or > 128)
                throw new InvalidDataException("calendar_time_zone");
            rules = source.TimeZoneInfos.ToArray();
            foreach (var rule in rules)
                if (rule.DtStart is not { IsFloating: true, HasTime: true } || rule.OffsetFrom is null || rule.OffsetTo is null)
                    throw new InvalidDataException("calendar_time_zone_rule");
        }

        public override ZoneInterval GetZoneInterval(Instant instant)
        {
            check();
            var year = instant.InUtc().Year;
            if (throughYear <= year) Expand(Math.Min(9999, year + 2));
            var index = Array.BinarySearch(transitions, new Transition(instant, Offset.Zero, Offset.Zero), TransitionComparer.Instance);
            if (index < 0) index = ~index - 1;
            var prior = index >= 0 ? transitions[index] : transitions[0] with { After = transitions[0].Before };
            return new ZoneInterval(Id, index >= 0 ? prior.At : null,
                index + 1 < transitions.Length ? transitions[index + 1].At : null, prior.After, Offset.Zero);
        }

        private void Expand(int year)
        {
            var values = new List<Transition>();
            var stop = new DateTime(year, 12, 31, 23, 59, 59, DateTimeKind.Unspecified);
            foreach (var rule in rules)
            {
                var before = Offset.FromTimeSpan(rule.OffsetFrom!.Offset);
                var after = Offset.FromTimeSpan(rule.OffsetTo!.Offset);
                // Observance DTSTART is local time using the offset before the transition.
                foreach (var occurrence in rule.GetOccurrences(options: new EvaluationOptions { MaxUnmatchedIncrementsLimit = 1000 }))
                {
                    check();
                    if (values.Count >= 20000) throw new CalendarExtractionException("calendar_time_zone_transitions", true);
                    var local = occurrence.Period.StartTime.Value;
                    var at = LocalDateTime.FromDateTime(local).WithOffset(before).ToInstant();
                    values.Add(new(at, before, after));
                    if (local > stop) break;
                }
            }
            if (values.Count == 0) throw new InvalidDataException("calendar_time_zone_rule");
            // Conflicting transitions at one instant cannot define a single time zone.
            var ordered = values.OrderBy(item => item.At).ToArray();
            if (ordered.GroupBy(item => item.At).Any(group => group.Select(item => item.After).Distinct().Count() > 1))
                throw new InvalidDataException("calendar_time_zone_conflict");
            transitions = ordered.DistinctBy(item => item.At).ToArray();
            throughYear = year;
        }

        private static Offset Minimum(VTimeZone zone) => Offsets(zone).Min();
        private static Offset Maximum(VTimeZone zone) => Offsets(zone).Max();
        private static IEnumerable<Offset> Offsets(VTimeZone zone) => zone.TimeZoneInfos.SelectMany(rule => new[]
        {
            Offset.FromTimeSpan(rule.OffsetFrom?.Offset ?? throw new InvalidDataException()),
            Offset.FromTimeSpan(rule.OffsetTo?.Offset ?? throw new InvalidDataException())
        }).DefaultIfEmpty(Offset.Zero);
        private sealed record Transition(Instant At, Offset Before, Offset After);
        private sealed class TransitionComparer : IComparer<Transition>
        {
            internal static readonly TransitionComparer Instance = new();
            public int Compare(Transition? x, Transition? y) => x!.At.CompareTo(y!.At);
        }
    }
}
