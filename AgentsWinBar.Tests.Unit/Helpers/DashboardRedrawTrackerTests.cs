using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class DashboardRedrawTrackerTests
{
    [TestMethod]
    public void ClockTickNeedsFullRedraw_NothingMissed_ReturnsFalse()
    {
        var tracker = new DashboardRedrawTracker();

        tracker.ClockTickNeedsFullRedraw().Should().BeFalse();
        tracker.RedrawPending.Should().BeFalse();
    }

    [TestMethod]
    public void ClockTickNeedsFullRedraw_AfterRedrawMissed_ReturnsTrueOnEveryTickUntilRedrawDone()
    {
        var tracker = new DashboardRedrawTracker();
        tracker.RedrawDone();

        tracker.RedrawMissed();

        tracker.ClockTickNeedsFullRedraw().Should().BeTrue();
        // Still pending while the full redraw keeps being skipped (screen still locked) or failing
        tracker.ClockTickNeedsFullRedraw().Should().BeTrue();
        tracker.RedrawPending.Should().BeTrue();
    }

    [TestMethod]
    public void ClockTickNeedsFullRedraw_AfterRedrawDone_ReturnsFalse()
    {
        var tracker = new DashboardRedrawTracker();
        tracker.RedrawMissed();

        tracker.RedrawDone();

        tracker.ClockTickNeedsFullRedraw().Should().BeFalse();
        tracker.RedrawPending.Should().BeFalse();
    }

    [TestMethod]
    public void RedrawMissed_AfterRedrawDone_MakesRedrawPendingAgain()
    {
        var tracker = new DashboardRedrawTracker();
        tracker.RedrawMissed();
        tracker.RedrawDone();

        tracker.RedrawMissed();

        tracker.ClockTickNeedsFullRedraw().Should().BeTrue();
    }
}
