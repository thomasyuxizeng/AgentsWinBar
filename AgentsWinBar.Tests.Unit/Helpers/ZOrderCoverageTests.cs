using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ZOrderCoverageTests
{
    private static readonly ScreenRect Strip = new(0, 1032, 400, 1080);

    private static WindowAbove Window(bool visible, ScreenRect bounds, bool cloaked = false) => new(visible, bounds, () => cloaked);

    [TestMethod]
    public void NeedsRaise_WhenNothingAbove_ReturnsFalse() =>
        ZOrderCoverage.NeedsRaise(true, Strip, []).Should().BeFalse();

    [TestMethod]
    public void NeedsRaise_WhenNotTopmost_ReturnsTrue() =>
        ZOrderCoverage.NeedsRaise(false, Strip, []).Should().BeTrue();

    [TestMethod]
    public void NeedsRaise_WhenOwnBoundsUnknown_ReturnsTrue() =>
        ZOrderCoverage.NeedsRaise(true, default, []).Should().BeTrue();

    [TestMethod]
    public void NeedsRaise_WhenVisibleWindowAboveOverlaps_ReturnsTrue() =>
        ZOrderCoverage.NeedsRaise(true, Strip, [Window(true, new ScreenRect(0, 1032, 1920, 1080))]).Should().BeTrue();

    [TestMethod]
    public void NeedsRaise_WhenWindowsAboveAreHiddenCloakedOrElsewhere_ReturnsFalse()
    {
        var above = new[]
        {
            Window(false, new ScreenRect(0, 1032, 1920, 1080)),
            Window(true, new ScreenRect(0, 1032, 1920, 1080), cloaked: true),
            Window(true, new ScreenRect(1500, 0, 1920, 400)),
            Window(true, new ScreenRect(400, 1032, 800, 1080)),   // touches the right edge only
            Window(true, default)
        };

        ZOrderCoverage.NeedsRaise(true, Strip, above).Should().BeFalse();
    }

    [TestMethod]
    public void NeedsRaise_WhenWindowDoesNotOverlap_DoesNotAskWhetherCloaked()
    {
        var asked = false;
        var above = new[] { new WindowAbove(true, new ScreenRect(1500, 0, 1920, 400), () => asked = true) };

        ZOrderCoverage.NeedsRaise(true, Strip, above);

        asked.Should().BeFalse();
    }

    [TestMethod]
    public void NeedsRaise_WhenTooManyWindowsAbove_ReturnsTrue()
    {
        var above = Enumerable.Range(0, ZOrderCoverage.MaxWindowsAbove + 1).Select(_ => Window(false, default));

        ZOrderCoverage.NeedsRaise(true, Strip, above).Should().BeTrue();
    }

    [TestMethod]
    [DataRow(0, 0, 10, 10, 5, 5, 15, 15, true)]
    [DataRow(0, 0, 10, 10, 10, 0, 20, 10, false)]
    [DataRow(0, 0, 10, 10, 0, 10, 10, 20, false)]
    [DataRow(0, 0, 10, 10, 2, 2, 3, 3, true)]
    [DataRow(0, 0, 0, 10, 0, 0, 10, 10, false)]
    public void Intersects_WhenRectanglesCompared_ReturnsExpected(int l1, int t1, int r1, int b1, int l2, int t2, int r2, int b2, bool expected)
    {
        new ScreenRect(l1, t1, r1, b1).Intersects(new ScreenRect(l2, t2, r2, b2)).Should().Be(expected);
        new ScreenRect(l2, t2, r2, b2).Intersects(new ScreenRect(l1, t1, r1, b1)).Should().Be(expected);
    }

    [TestMethod]
    [DataRow(true, 0, 1032, 1920, 1080, true)]
    [DataRow(false, 0, 1032, 1920, 1080, false)]
    [DataRow(true, 400, 1032, 800, 1080, false)]
    public void OverlapsVisible_WhenChecked_ReturnsExpected(bool visible, int left, int top, int right, int bottom, bool expected) =>
        ZOrderCoverage.OverlapsVisible(visible, new ScreenRect(left, top, right, bottom), Strip).Should().Be(expected);
}
