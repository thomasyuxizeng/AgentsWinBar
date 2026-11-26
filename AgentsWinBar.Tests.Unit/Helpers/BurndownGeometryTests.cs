using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class BurndownGeometryTests
{
    [TestMethod]
    public void Y_WhenAtTheLimitOrEmpty_KeepsTheInsetFree()
    {
        BurndownGeometry.Y(100, 88, 4).Should().Be(4);
        BurndownGeometry.Y(0, 88, 4).Should().Be(84);
        BurndownGeometry.Y(50, 88, 4).Should().Be(44);
    }

    [TestMethod]
    public void Y_WhenOutsideZeroToHundred_DrawsOnTheNearestEdgeLine()
    {
        BurndownGeometry.Y(140, 88, 4).Should().Be(4);
        BurndownGeometry.Y(-5, 88, 4).Should().Be(84);
        BurndownGeometry.Y(double.NaN, 88, 4).Should().Be(84);
    }

    [TestMethod]
    public void Y_WhenInsetLargerThanHalfThePlot_StaysInsideThePlot()
    {
        BurndownGeometry.Y(100, 6, 4).Should().Be(3);
        BurndownGeometry.Y(0, 6, 4).Should().Be(3);
    }

    [TestMethod]
    public void ClipAtCeiling_WhenProjectionStaysBelowTheLimit_KeepsItWhole()
    {
        var points = new[] { new BurndownPoint(0.4, 30), new BurndownPoint(1, 75) };

        var clipped = BurndownGeometry.ClipAtCeiling(points, out var crossed);

        clipped.Should().Equal(points);
        crossed.Should().BeFalse();
    }

    [TestMethod]
    public void ClipAtCeiling_WhenProjectionEndsExactlyAtTheLimit_KeepsItWithoutACrossing()
    {
        var points = new[] { new BurndownPoint(0.5, 50), new BurndownPoint(1, 100) };

        BurndownGeometry.ClipAtCeiling(points, out var crossed).Should().Equal(points);
        crossed.Should().BeFalse();
    }

    [TestMethod]
    public void ClipAtCeiling_WhenProjectionPassesTheLimit_EndsWhereItReachesHundredPercent()
    {
        // 40% at 0.2 rising to 140% at reset: 60 points over 0.8 of the window, so 100% is reached at 0.2 + 0.8 * 0.6 = 0.68
        var points = new[] { new BurndownPoint(0.2, 40), new BurndownPoint(1, 140) };

        var clipped = BurndownGeometry.ClipAtCeiling(points, out var crossed);

        crossed.Should().BeTrue();
        clipped.Should().HaveCount(2);
        clipped[0].Should().Be(points[0]);
        clipped[1].ElapsedFraction.Should().BeApproximately(0.68, 1e-9);
        clipped[1].UsedPercent.Should().Be(100);
    }

    [TestMethod]
    public void ClipAtCeiling_WhenUsageStaysAtTheLimit_KeepsTheFlatProjectionAlongTheTop()
    {
        var points = new[] { new BurndownPoint(0.3, 100), new BurndownPoint(1, 100) };

        BurndownGeometry.ClipAtCeiling(points, out var crossed).Should().Equal(points);
        crossed.Should().BeFalse();
    }

    [TestMethod]
    public void ClipAtCeiling_WhenUsageIsAtTheLimitAndRisingOrAboveIt_DrawsNoProjection()
    {
        BurndownGeometry.ClipAtCeiling([new BurndownPoint(0.5, 100), new BurndownPoint(1, 180)], out var atLimit).Should().BeEmpty();
        atLimit.Should().BeFalse();
        BurndownGeometry.ClipAtCeiling([new BurndownPoint(0.5, 104), new BurndownPoint(1, 190)], out var above).Should().BeEmpty();
        above.Should().BeFalse();
    }

    [TestMethod]
    public void ClipAtCeiling_WhenFewerThanTwoPoints_ReturnsEmpty()
    {
        BurndownGeometry.ClipAtCeiling([], out _).Should().BeEmpty();
        BurndownGeometry.ClipAtCeiling([new BurndownPoint(0.5, 20)], out _).Should().BeEmpty();
    }
}
