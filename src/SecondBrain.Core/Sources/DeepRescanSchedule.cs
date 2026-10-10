using System.Globalization;

namespace SecondBrain.Core.Sources;

/// <summary>The nightly deep-rescan tier of FLD-2, scheduled at <c>ingest.deep_rescan_at</c> in the configured zone.</summary>
public static class DeepRescanSchedule
{
    /// <summary>The first occurrence of <paramref name="at"/> (<c>HH:mm</c>) strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset Next(DateTimeOffset now, string at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var time = TimeOnly.ParseExact(at, "HH:mm", CultureInfo.InvariantCulture);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = DateOnly.FromDateTime(local.DateTime);
        for (var attempt = 0; attempt < 3; attempt++, day = day.AddDays(1))
        {
            var wall = day.ToDateTime(time);
            // A wall time skipped by a forward DST transition runs at the first valid minute after it.
            while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(1);
            var candidate = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            if (candidate > now) return candidate;
        }
        throw new InvalidOperationException("No deep rescan time follows the current instant.");
    }
}
