using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class FlyoutHeightTests
{
    [TestMethod]
    public void Fit_WhenAllCardsFitTheWorkArea_ReturnsTheContentHeightInPixels()
    {
        // 600 DIP at 150% = 900 px, plus a 2 px border and 1 px of slack
        FlyoutHeight.Fit(600, 1.5, 2, 480, 1400).Should().Be(903);
    }

    [TestMethod]
    public void Fit_WhenContentIsShort_KeepsTheOriginalHeight()
    {
        FlyoutHeight.Fit(200, 1.5, 2, 480, 1400).Should().Be(480);
    }

    [TestMethod]
    public void Fit_WhenCardsDoNotFit_StopsAtTheWorkAreaAndScrolls()
    {
        FlyoutHeight.Fit(1200, 1.5, 2, 480, 1400).Should().Be(1400);
    }

    [TestMethod]
    public void Fit_WhenWorkAreaIsShorterThanTheMinimum_UsesTheWorkArea()
    {
        FlyoutHeight.Fit(200, 1, 2, 480, 400).Should().Be(400);
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void Fit_WhenScaleIsUnusable_KeepsTheMinimum(double scale)
    {
        FlyoutHeight.Fit(600, scale, 2, 480, 1400).Should().Be(480);
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(double.NaN)]
    public void Fit_WhenContentIsNotMeasured_KeepsTheMinimum(double content)
    {
        FlyoutHeight.Fit(content, 1.5, 2, 480, 1400).Should().Be(480);
    }

    [TestMethod]
    public void Max_WhenGivenWorkArea_LeavesTheBottomMarginAndScaledTopGap()
    {
        // 1432 px work area at 150%: 2 px bottom margin, 12 DIP = 18 px top gap
        FlyoutHeight.Max(1432, 2, 12, 1.5).Should().Be(1412);
    }

    [TestMethod]
    public void Max_WhenWorkAreaIsTiny_NeverGoesBelowOnePixel()
    {
        FlyoutHeight.Max(10, 2, 12, 2).Should().Be(1);
    }
}
