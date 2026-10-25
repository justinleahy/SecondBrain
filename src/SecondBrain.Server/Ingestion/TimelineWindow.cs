using System.Globalization;

namespace SecondBrain.Server.Ingestion;

public sealed record TimelineWindow(string FromDay, string ToDay, string FromInstant, string ToInstant)
{
    public static TimelineWindow Resolve(string? from, string? to, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var start = from is null ? new DateOnly(today.Year, today.Month, 1) : Parse(from);
        var end = to is null ? start.AddMonths(1) : Parse(to);
        if (end <= start) throw new ArgumentException("The end date must be after the start date.");
        return new(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StartOfDay(start, zone).ToString("O", CultureInfo.InvariantCulture), StartOfDay(end, zone).ToString("O", CultureInfo.InvariantCulture));
    }

    private static DateOnly Parse(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
        ? day : throw new ArgumentException("Dates must use yyyy-MM-dd.");

    internal static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some zones advance at midnight or skip an entire civil day. Its boundary is
        // the first actual local instant at or after midnight, not a fabricated offset.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
