using SecondBrain.Core.Configuration;
using SecondBrain.Core.Sources;
using Xunit;

namespace SecondBrain.Core.Tests.Sources;

public sealed class DeepRescanScheduleTests
{
    [Fact]
    public void DefaultIsNightlyAtThreeInTheConfiguredZone()
    {
        Assert.Equal("03:00", new SecondBrainOptions().Ingest.DeepRescanAt);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var evening = new DateTimeOffset(2026, 10, 9, 22, 15, 0, TimeSpan.FromHours(-4));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 3, 0, 0, TimeSpan.FromHours(-4)), DeepRescanSchedule.Next(evening, "03:00", zone));
        var earlyMorning = new DateTimeOffset(2026, 10, 10, 1, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 3, 0, 0, TimeSpan.FromHours(-4)), DeepRescanSchedule.Next(earlyMorning, "03:00", zone));
        var exactly = new DateTimeOffset(2026, 10, 10, 3, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(exactly.AddDays(1), DeepRescanSchedule.Next(exactly, "03:00", zone));
    }

    [Fact]
    public void ConfiguredTimeAndSkippedDaylightSavingTimeAreHonored()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 23, 30, 0, TimeSpan.FromHours(-4)), DeepRescanSchedule.Next(now, "23:30", zone));
        // 2027-03-14 02:30 does not exist in New York; the rescan runs at the first valid minute, 03:00 EDT.
        var beforeSpringForward = new DateTimeOffset(2027, 3, 14, 0, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2027, 3, 14, 3, 0, 0, TimeSpan.FromHours(-4)), DeepRescanSchedule.Next(beforeSpringForward, "02:30", zone));
    }
}
