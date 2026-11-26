using System.Globalization;
using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class TimestampFormatterTests
{
    private static readonly TimeZoneInfo Seoul = TimeZoneInfo.CreateCustomTimeZone("Test Seoul", TimeSpan.FromHours(9), "Seoul", "Seoul");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [TestMethod]
    [DataRow(DateTimeKind.Utc)]
    [DataRow(DateTimeKind.Unspecified)]
    public void FormatLocal_WhenUtcOrOffsetMissing_DisplaysLocalTime(DateTimeKind kind) =>
        TimestampFormatter.FormatLocal(new DateTime(2026, 10, 4, 11, 55, 0, kind), Seoul, English)
            .Should().Be("10/4/2026 8:55 PM");

    [TestMethod]
    public void FormatLocal_WhenLocalKind_DoesNotApplyOffsetTwice() =>
        TimestampFormatter.FormatLocal(new DateTime(2026, 10, 4, 11, 55, 0, DateTimeKind.Utc).ToLocalTime(), Seoul, English)
            .Should().Be("10/4/2026 8:55 PM");

    [TestMethod]
    public void FormatLocal_WhenConversionCrossesMidnight_DisplaysNextDay() =>
        TimestampFormatter.FormatLocal(new DateTime(2026, 10, 4, 23, 55, 0, DateTimeKind.Utc), Seoul, English)
            .Should().Be("10/5/2026 8:55 AM");

    [TestMethod]
    public void FormatLocal_WhenTimezoneBehindUtc_SubtractsOffset()
    {
        var pacific = TimeZoneInfo.CreateCustomTimeZone("Test Pacific", TimeSpan.FromHours(-7), "Pacific", "Pacific");
        TimestampFormatter.FormatLocal(new DateTime(2026, 10, 4, 11, 55, 0, DateTimeKind.Utc), pacific, English)
            .Should().Be("10/4/2026 4:55 AM");
    }

    [TestMethod]
    public void FormatLocal_WhenCultureUses24HourClock_RespectsCulture() =>
        TimestampFormatter.FormatLocal(new DateTime(2026, 10, 4, 11, 55, 0, DateTimeKind.Utc), Seoul, CultureInfo.GetCultureInfo("en-GB"))
            .Should().Be("04/10/2026 20:55");
}
