using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class DashboardValuesTests
{
    [TestMethod]
    [DataRow(double.NaN, 0d)]
    [DataRow(double.PositiveInfinity, 0d)]
    [DataRow(double.NegativeInfinity, 0d)]
    [DataRow(-5d, 0d)]
    [DataRow(120d, 100d)]
    [DataRow(42d, 42d)]
    public void Percent_InvalidOrOutOfRangeValues_ReturnsFiniteBoundedValue(double value, double expected) =>
        DashboardValues.Percent(value).Should().Be(expected);

    [TestMethod]
    public void Utc_AllTimestampKinds_PreservesInstantAndTreatsUnspecifiedAsUtc()
    {
        var utc = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        DashboardValues.Utc(utc).Should().Be(utc);
        DashboardValues.Utc(utc.ToLocalTime()).Should().Be(utc);
        DashboardValues.Utc(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)).Should().Be(utc);
        DashboardValues.Utc(utc.ToLocalTime()).Kind.Should().Be(DateTimeKind.Utc);
    }
}
