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
}
