using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class BarResetGhostTests
{
    [TestMethod]
    [DataRow(0.0, 0.0)]
    [DataRow(50.0, 100.0)]
    [DataRow(79.0, 158.0)]
    [DataRow(100.0, 200.0)]
    [DataRow(150.0, 200.0)]
    [DataRow(-5.0, 0.0)]
    public void Width_FinitePercent_MapsLikeLiveFill(double percent, double expected)
    {
        BarResetGhost.Width(200, percent).Should().BeApproximately(expected, 1e-9);
    }

    [TestMethod]
    public void Width_NaNInfinityOrNull_ReturnsNull()
    {
        BarResetGhost.Width(200, double.NaN).Should().BeNull();
        BarResetGhost.Width(200, double.PositiveInfinity).Should().BeNull();
        BarResetGhost.Width(200, null).Should().BeNull();
    }

    [TestMethod]
    [DataRow(0.0, 0.0)]
    [DataRow(50.0, 50.0)]
    [DataRow(79.0, 79.0)]
    [DataRow(100.0, 100.0)]
    [DataRow(150.0, 150.0)]
    [DataRow(-5.0, 0.0)]
    public void ColourPercent_FinitePercent_KeepsThresholdValue(double percent, double expected)
    {
        BarResetGhost.ColourPercent(percent).Should().Be(expected);
    }

    [TestMethod]
    public void ColourPercent_NaNOrNull_ReturnsNull()
    {
        BarResetGhost.ColourPercent(double.NaN).Should().BeNull();
        BarResetGhost.ColourPercent(null).Should().BeNull();
    }

    [TestMethod]
    public void VisualPercent_NaNOrNull_ReturnsNull()
    {
        BarResetGhost.VisualPercent(double.NaN).Should().BeNull();
        BarResetGhost.VisualPercent(null).Should().BeNull();
    }
}
