using System.Globalization;
using System.Text.RegularExpressions;

namespace SecondBrain.Core.Ingestion;

/// <summary>Interprets declared source time without substituting admission time.</summary>
public sealed record SourceTime(string At, int? OffsetMinutes, string Basis, string Precision)
{
    public string? Offset => OffsetMinutes is { } minutes ? string.Create(CultureInfo.InvariantCulture, $"{(minutes < 0 ? "-" : "+")}{Math.Abs(minutes) / 60:00}:{Math.Abs(minutes) % 60:00}") : null;
    public static SourceTime? Parse(string? value, string basis, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return null;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(midnight)) return null;
            return new(TimeZoneInfo.ConvertTimeToUtc(midnight, zone).ToString("O", CultureInfo.InvariantCulture), null, basis, "date");
        }
        if (Regex.IsMatch(value, @"(?:[zZ]|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var explicitTime)) return null;
            return new(explicitTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), checked((int)explicitTime.Offset.TotalMinutes), basis, "datetime");
        }
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) return null;
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone).ToString("O", CultureInfo.InvariantCulture), null, basis, "datetime");
    }
}
