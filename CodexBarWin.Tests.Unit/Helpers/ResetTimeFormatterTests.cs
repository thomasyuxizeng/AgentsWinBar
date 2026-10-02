using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class ResetTimeFormatterTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Format_WhenResetTimeUnknown_ReturnsNull()
    {
        ResetTimeFormatter.Format(null, Now).Should().BeNull();
    }

    [TestMethod]
    [DataRow(30, "Resets in <1m")]          // 30 seconds
    [DataRow(60 * 12, "Resets in 12m")]
    [DataRow(60 * 59, "Resets in 59m")]
    [DataRow(60 * 60, "Resets in 1h 0m")]
    [DataRow(60 * (4 * 60 + 59), "Resets in 4h 59m")]
    [DataRow(60 * (24 * 60 - 1), "Resets in 23h 59m")]
    [DataRow(60 * 24 * 60, "Resets in 1d 0h")]
    [DataRow(60 * (2 * 24 * 60 + 3 * 60 + 30), "Resets in 2d 3h")]
    public void Format_WhenInFuture_ReturnsCompactCountdown(int secondsAhead, string expected)
    {
        ResetTimeFormatter.Format(Now.AddSeconds(secondsAhead), Now).Should().Be(expected);
    }

    [TestMethod]
    public void Format_WhenInThePast_ReturnsResettingNow()
    {
        ResetTimeFormatter.Format(Now.AddMinutes(-5), Now).Should().Be("Resetting now");
    }

    [TestMethod]
    public void Format_WhenKindUnspecified_TreatsValueAsUtc()
    {
        var unspecified = DateTime.SpecifyKind(Now.AddHours(2), DateTimeKind.Unspecified);

        ResetTimeFormatter.Format(unspecified, Now).Should().Be("Resets in 2h 0m");
    }

    [TestMethod]
    public void Format_WhenLocalKind_ConvertsToUtcBeforeComparing()
    {
        var local = Now.AddHours(3).ToLocalTime();

        ResetTimeFormatter.Format(local, Now).Should().Be("Resets in 3h 0m");
    }

    [TestMethod]
    public void ResetText_WhenNoResetTime_FallsBackToCliDescription()
    {
        var window = new UsageWindow { ResetIn = "  resets Oct 2, 1pm  " };

        window.ResetText.Should().Be("resets Oct 2, 1pm");
    }

    [TestMethod]
    public void ResetText_WhenNothingKnown_IsEmpty()
    {
        new UsageWindow().ResetText.Should().BeEmpty();
    }

    [TestMethod]
    public void ResetText_WhenResetTimeKnown_PrefersComputedCountdown()
    {
        var window = new UsageWindow
        {
            ResetAt = DateTime.UtcNow.AddHours(5).AddMinutes(30),
            ResetIn = "resets Oct 2, 1pm (Asia/Seoul)"
        };

        window.ResetText.Should().StartWith("Resets in 5h");
    }
}
