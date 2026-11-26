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
    public void ClockTickNeedsFullRedraw_TenthTickWithoutFullRedraw_ReturnsTrue()
    {
        var tracker = new DashboardRedrawTracker();
        tracker.RedrawDone();

        var results = Enumerable.Range(0, DashboardRedrawTracker.FullRedrawEveryTicks)
            .Select(_ => tracker.ClockTickNeedsFullRedraw()).ToList();

        results.Take(DashboardRedrawTracker.FullRedrawEveryTicks - 1).Should().OnlyContain(r => !r);
        results.Last().Should().BeTrue();
    }

    [TestMethod]
    public void ClockTickNeedsFullRedraw_PeriodicRedrawDone_StartsCountingAgain()
    {
        var tracker = new DashboardRedrawTracker();
        for (var i = 0; i < DashboardRedrawTracker.FullRedrawEveryTicks; i++) tracker.ClockTickNeedsFullRedraw();
        tracker.RedrawDone();

        var results = Enumerable.Range(0, DashboardRedrawTracker.FullRedrawEveryTicks)
            .Select(_ => tracker.ClockTickNeedsFullRedraw()).ToList();

        results.Count(r => r).Should().Be(1);
        results.Last().Should().BeTrue();
    }

    [TestMethod]
    public void ClockTickNeedsFullRedraw_DataRedrawBetweenTicks_PostponesPeriodicRedraw()
    {
        // A redraw for new data counts as the periodic one: no extra snapshot right after it
        var tracker = new DashboardRedrawTracker();
        for (var i = 0; i < DashboardRedrawTracker.FullRedrawEveryTicks - 1; i++) tracker.ClockTickNeedsFullRedraw();

        tracker.RedrawDone();

        tracker.ClockTickNeedsFullRedraw().Should().BeFalse();
    }

    [TestMethod]
    public void ClockTickNeedsFullRedraw_PeriodicRedrawFailedOrNotRun_StaysTrue()
    {
        var tracker = new DashboardRedrawTracker();
        for (var i = 0; i < DashboardRedrawTracker.FullRedrawEveryTicks; i++) tracker.ClockTickNeedsFullRedraw();

        tracker.ClockTickNeedsFullRedraw().Should().BeTrue();
        tracker.ClockTickNeedsFullRedraw().Should().BeTrue();
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
