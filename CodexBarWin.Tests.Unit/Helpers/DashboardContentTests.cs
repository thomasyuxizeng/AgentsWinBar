using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class DashboardContentTests
{
    private static BurndownSeries Series() => new()
    {
        Usage = [new(0.1, 10), new(0.2, 20)], Guide = [new(0, 0), new(1, 100)],
        Projection = [new(0.2, 20), new(1, 100)], CapturedAt = new DateTime(2026, 10, 5), ProjectedAtReset = 100
    };
    private static DailyCost Day() => new()
    { Date = new(2026, 10, 5), TotalCost = 2, TotalTokens = 100, ModelBreakdowns = [new() { ModelName = "model", Cost = 2, TotalTokens = 100 }] };
    private static DashboardCost Cost() => new()
    { Daily = [Day()], PeakDay = Day(), TopModels = [new() { ModelName = "model", Cost = 2 }], TodayCost = 2 };
    private static DashboardProvider Provider() => new()
    { Id = "claude", Cost = Cost(), Windows = [new() { Key = "session", UsedPercent = 20, History = Series() }] };

    [TestMethod]
    public void ProviderEquals_IndependentNestedLists_ReturnsTrue() =>
        DashboardContent.ProviderEquals(Provider(), Provider()).Should().BeTrue();

    [TestMethod]
    public void ProviderEquals_ScalarOrWindowChange_ReturnsFalse()
    {
        var a = Provider();
        DashboardContent.ProviderEquals(a, a with { IsStale = true }).Should().BeFalse();
        DashboardContent.ProviderEquals(a, a with { Windows = [a.Windows[0] with { UsedPercent = 21 }] }).Should().BeFalse();
        DashboardContent.ProviderEquals(a, a with { Cost = a.Cost! with { Currency = "EUR" } }).Should().BeFalse();
    }

    [TestMethod]
    public void WindowEquals_HistoryOrTimestampChange_ReturnsFalse()
    {
        var a = Provider().Windows[0];
        DashboardContent.WindowEquals(a, a with { ResetsAtUtc = DateTime.UtcNow }).Should().BeFalse();
        DashboardContent.WindowEquals(a, a with { History = a.History with { Guide = [new(0, 1)] } }).Should().BeFalse();
        DashboardContent.WindowEquals(a, a with { ResetText = "changed" }).Should().BeFalse();
    }

    [TestMethod]
    public void SeriesEquals_IndependentPoints_ReturnsTrue() =>
        DashboardContent.SeriesEquals(Series(), Series()).Should().BeTrue();

    [TestMethod]
    public void SeriesEquals_AnyRenderedContentChange_ReturnsFalse()
    {
        var a = Series();
        DashboardContent.SeriesEquals(a, a with { Usage = [new(0.1, 11), new(0.2, 20)] }).Should().BeFalse();
        DashboardContent.SeriesEquals(a, a with { Projection = [] }).Should().BeFalse();
        DashboardContent.SeriesEquals(a, a with { Message = "waiting" }).Should().BeFalse();
        DashboardContent.SeriesEquals(a, a with { CapturedAt = a.CapturedAt!.Value.AddMinutes(1) }).Should().BeFalse();
    }

    [TestMethod]
    public void CostEquals_NestedModelOrDailyChange_ReturnsFalse()
    {
        var a = Cost();
        DashboardContent.CostEquals(a, Cost()).Should().BeTrue();
        DashboardContent.CostEquals(a, a with { Daily = [Day() with { TotalCost = 3 }] }).Should().BeFalse();
        DashboardContent.CostEquals(a, a with { PeakDay = Day() with { TotalTokens = 101 } }).Should().BeFalse();
        DashboardContent.CostEquals(a, a with { TopModels = [] }).Should().BeFalse();
        DashboardContent.DailyEquals(Day(), Day() with { ModelBreakdowns = [] }).Should().BeFalse();
    }

    [TestMethod]
    public void DiagnosticsEquals_IndependentLists_ReturnsTrue()
    {
        DashboardDiagnostics Create() => new()
        { RecentRefreshes = [new() { ProviderId = "claude" }], RecentAlerts = [new() { Title = "alert" }], CostCache = [new() { ProviderId = "claude" }] };
        var a = Create();
        DashboardContent.DiagnosticsEquals(a, Create()).Should().BeTrue();
        DashboardContent.DiagnosticsEquals(a, a with { Theme = "Light" }).Should().BeFalse();
        DashboardContent.DiagnosticsEquals(a, a with { RecentRefreshes = [new() { Success = true }] }).Should().BeFalse();
        DashboardContent.DiagnosticsEquals(a, a with { RecentAlerts = [] }).Should().BeFalse();
        DashboardContent.DiagnosticsEquals(a, a with { CostCache = [] }).Should().BeFalse();
    }

    [TestMethod]
    public void SequenceEquals_OrderAndLengthChanges_ReturnsFalse()
    {
        DashboardContent.SequenceEquals(new[] { 1, 2 }, new[] { 2, 1 }, (a, b) => a == b).Should().BeFalse();
        DashboardContent.SequenceEquals(new[] { 1 }, new[] { 1, 2 }, (a, b) => a == b).Should().BeFalse();
    }

    [TestMethod]
    public void Equals_NullAndSameInstances_ReturnsExpectedResult()
    {
        DashboardContent.ProviderEquals(null, null).Should().BeTrue();
        DashboardContent.ProviderEquals(Provider(), null).Should().BeFalse();
        DashboardContent.WindowEquals(null, new()).Should().BeFalse();
        DashboardContent.CostEquals(null, new()).Should().BeFalse();
        DashboardContent.DailyEquals(null, new()).Should().BeFalse();
        DashboardContent.SeriesEquals(null, new()).Should().BeFalse();
        DashboardContent.DiagnosticsEquals(null, new()).Should().BeFalse();
        var a = Provider();
        DashboardContent.ProviderEquals(a, a).Should().BeTrue();
    }
}
