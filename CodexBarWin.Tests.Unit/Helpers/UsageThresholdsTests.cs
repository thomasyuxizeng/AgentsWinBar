using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class UsageThresholdsTests
{
    [TestMethod]
    public void Constants_AreAmberAt75AndRedAt90()
    {
        UsageThresholds.Warning.Should().Be(75);
        UsageThresholds.Critical.Should().Be(90);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(50, 0)]
    [DataRow(74.99, 0)]
    [DataRow(75, 1)]
    [DataRow(80, 1)]
    [DataRow(89.99, 1)]
    [DataRow(90, 2)]
    [DataRow(95, 2)]
    [DataRow(100, 2)]
    [DataRow(105, 2)]
    public void Level_UsesTheSharedBoundaries(double percent, int expected) =>
        UsageThresholds.Level(percent).Should().Be(expected);
}
