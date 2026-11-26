using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Services;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class UsageThresholdsTests
{
    [TestMethod]
    public void Constants_AreAmberAt75AndRedAt90()
    {
        UsageThresholds.Warning.Should().Be(75);
        UsageThresholds.Critical.Should().Be(90);
        UsageAlertEvaluator.NearLimitPercent.Should().Be(UsageThresholds.Critical);
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

    [TestMethod]
    [DataRow(74.99, 0, 0)]
    [DataRow(75, 1, 0)]
    [DataRow(89.99, 1, 0)]
    [DataRow(90, 0, 1)]
    [DataRow(95, 0, 1)]
    public void Build_SummaryCounts_UseSharedBoundaries(double percent, int near, int critical)
    {
        var snapshot = DashboardSnapshotBuilder.Build(new() { Providers = [new() { Provider = "gemini",
            Session = new() { ReportedPercent = percent } }] });
        snapshot.Summary.WindowsNearLimit.Should().Be(near);
        snapshot.Summary.WindowsAtLimit.Should().Be(critical);
    }
}
