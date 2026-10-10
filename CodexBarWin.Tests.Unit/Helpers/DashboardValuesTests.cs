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

    [TestMethod]
    public void Utc_KindUtc_ReturnsIdenticalUtcInstant()
    {
        var dt = new DateTime(2026, 5, 1, 8, 30, 0, DateTimeKind.Utc);
        var result = DashboardValues.Utc(dt);
        result.Kind.Should().Be(DateTimeKind.Utc);
        result.Should().Be(dt);
    }

    [TestMethod]
    public void Utc_KindLocal_ConvertsToUtcKindAndCorrectInstant()
    {
        var local = new DateTime(2026, 5, 1, 14, 0, 0, DateTimeKind.Local);
        var result = DashboardValues.Utc(local);
        result.Kind.Should().Be(DateTimeKind.Utc);
        result.Should().Be(local.ToUniversalTime());
    }

    [TestMethod]
    public void Utc_KindUnspecified_TreatedAsUtcWithoutTimeShift()
    {
        var unspec = new DateTime(2026, 5, 1, 14, 0, 0, DateTimeKind.Unspecified);
        var result = DashboardValues.Utc(unspec);
        result.Kind.Should().Be(DateTimeKind.Utc);
        result.Year.Should().Be(2026);
        result.Month.Should().Be(5);
        result.Day.Should().Be(1);
        result.Hour.Should().Be(14);
        result.Minute.Should().Be(0);
        result.Second.Should().Be(0);
    }
}
