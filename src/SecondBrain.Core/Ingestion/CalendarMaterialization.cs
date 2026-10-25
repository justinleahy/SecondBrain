using System.Globalization;
using SecondBrain.Core.Configuration;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Core.Ingestion;

public static class CalendarMaterialization
{
    public static CalendarWindow Window(SecondBrainOptions options, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Time.Zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var from = StartOfDay(today.AddMonths(-options.Calendar.PastMonths), zone);
        var to = StartOfDay(today.AddMonths(options.Calendar.FutureMonths), zone);
        return new(from.ToString("O", CultureInfo.InvariantCulture), to.ToString("O", CultureInfo.InvariantCulture), zone.Id, options.Limits.Global.CalendarOccurrences);
    }
    private static DateTimeOffset StartOfDay(DateTime day, TimeZoneInfo zone)
    {
        day = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(day)) day = day.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(day) ? zone.GetAmbiguousTimeOffsets(day).Max() : zone.GetUtcOffset(day);
        return new DateTimeOffset(day, offset).ToUniversalTime();
    }
}
