using System.Globalization;
using SecondBrain.Server.Ingestion;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class TimelineWindowTests
{
    [Theory]
    [InlineData("2026-03-08", "2026-03-09", 23)]
    [InlineData("2026-11-01", "2026-11-02", 25)]
    public void CivilDayBoundsRespectDaylightSaving(string from, string to, int hours)
    {
        var result = TimelineWindow.Resolve(from, to, DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        Assert.Equal(TimeSpan.FromHours(hours), DateTimeOffset.Parse(result.ToInstant, CultureInfo.InvariantCulture) - DateTimeOffset.Parse(result.FromInstant, CultureInfo.InvariantCulture));
        Assert.Equal(from, result.FromDay);
        Assert.Equal(to, result.ToDay);
    }

    [Fact]
    public void DefaultMonthUsesTheInstancesLocalDate()
    {
        var result = TimelineWindow.Resolve(null, null, DateTimeOffset.Parse("2026-11-01T00:30:00Z", CultureInfo.InvariantCulture), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        Assert.Equal("2026-10-01", result.FromDay);
        Assert.Equal("2026-11-01", result.ToDay);
    }

    [Theory]
    [InlineData("2026-11-01", "2026-11-01")]
    [InlineData("2026-11-02", "2026-11-01")]
    [InlineData("November 1", "2026-11-02")]
    public void InvalidOrInvertedDatesAreRejected(string from, string to) =>
        Assert.Throws<ArgumentException>(() => TimelineWindow.Resolve(from, to, DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
}
