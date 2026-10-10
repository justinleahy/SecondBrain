using SecondBrain.Core.Ingestion;
using Xunit;
namespace SecondBrain.Core.Tests.Ingestion;
public sealed class SourceTimeTests
{
    [Fact]
    public void DateUsesInstanceZoneAndPreservesDatePrecisionWithoutInventingOffset()
    {
        var result = SourceTime.Parse("2026-07-02", "created", TimeZoneInfo.FindSystemTimeZoneById("America/New_York"))!;
        Assert.StartsWith("2026-07-02T04:00:00", result.At); Assert.Null(result.OffsetMinutes); Assert.Equal("date", result.Precision);
    }
    [Fact]
    public void ExplicitOffsetIsPreservedWhileAtIsUtc()
    {
        var result = SourceTime.Parse("2026-07-02T10:30:00+02:00", "published_at", TimeZoneInfo.Utc)!;
        Assert.StartsWith("2026-07-02T08:30:00", result.At); Assert.Equal(120, result.OffsetMinutes); Assert.Equal("datetime", result.Precision);
        Assert.Null(SourceTime.Parse(null, "created", TimeZoneInfo.Utc)); Assert.Null(SourceTime.Parse("invalid", "created", TimeZoneInfo.Utc));
    }
}
