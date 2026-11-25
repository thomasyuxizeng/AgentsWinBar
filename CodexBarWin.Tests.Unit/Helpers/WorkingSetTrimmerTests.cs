using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class WorkingSetTrimmerTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void ShouldTrim_IdleWithNoPreviousTrim_ReturnsTrue()
    {
        WorkingSetTrimmer.ShouldTrim(Now, null, false, false, false).Should().BeTrue();
    }

    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public void ShouldTrim_WindowVisibleOrRefreshRunning_ReturnsFalse(bool flyout, bool dashboard, bool refreshing)
    {
        WorkingSetTrimmer.ShouldTrim(Now, null, flyout, dashboard, refreshing).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(59, false)]
    [DataRow(60, true)]
    [DataRow(61, true)]
    [DataRow(-1, false)]
    public void ShouldTrim_ElapsedTimeSincePreviousTrim_RequiresSixtySeconds(int seconds, bool expected)
    {
        WorkingSetTrimmer.ShouldTrim(Now, Now.AddSeconds(-seconds), false, false, false).Should().Be(expected);
    }

    [TestMethod]
    public void ShouldCompactLoh_BeforeFirstStartupCompaction_ReturnsTrue()
    {
        WorkingSetTrimmer.ShouldCompactLoh(
                Now, lastCompact: Now, hasCompactedAfterStartup: false,
                dashboardJustClosed: false, currentMemoryBytes: 10_000_000, baselineMemoryBytes: 10_000_000)
            .Should().BeTrue();
    }

    [TestMethod]
    [DataRow(59, true, false)]
    [DataRow(60, true, true)]
    [DataRow(60, false, false)]
    public void ShouldCompactLoh_DashboardJustClosed_RequiresStartupOrDashboardCooldown(
        int secondsSinceLastCompact, bool dashboardJustClosed, bool expected)
    {
        WorkingSetTrimmer.ShouldCompactLoh(
                Now, Now.AddSeconds(-secondsSinceLastCompact), hasCompactedAfterStartup: true,
                dashboardJustClosed, currentMemoryBytes: 10_000_000, baselineMemoryBytes: 10_000_000)
            .Should().Be(expected);
    }

    [TestMethod]
    [DataRow(9, 8 * 1024 * 1024, false)]
    [DataRow(10, 8 * 1024 * 1024 - 1, false)]
    [DataRow(10, 8 * 1024 * 1024, true)]
    public void ShouldCompactLoh_MemoryGrowth_RequiresTenMinutesAndEightMegabytes(
        int minutesSinceLastCompact, long growthBytes, bool expected)
    {
        const long baseline = 32L * 1024 * 1024;
        WorkingSetTrimmer.ShouldCompactLoh(
                Now, Now.AddMinutes(-minutesSinceLastCompact), hasCompactedAfterStartup: true,
                dashboardJustClosed: false, currentMemoryBytes: baseline + growthBytes, baselineMemoryBytes: baseline)
            .Should().Be(expected);
    }
}
