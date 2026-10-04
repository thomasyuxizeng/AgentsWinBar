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

    [TestMethod]
    public void Equals_EveryRecordPropertyMutation_ReturnsFalse()
    {
        Verify(new DashboardProvider(), DashboardContent.ProviderEquals);
        Verify(new DashboardWindow(), DashboardContent.WindowEquals);
        Verify(new DashboardCost(), DashboardContent.CostEquals);
        Verify(new BurndownSeries(), DashboardContent.SeriesEquals);
        Verify(new DailyCost(), DashboardContent.DailyEquals);
        Verify(new DashboardDiagnostics(), DashboardContent.DiagnosticsEquals);
        Verify(new ProviderStatus(), (a, b) => DashboardContent.ProviderEquals(
            new() { ServiceStatus = a }, new() { ServiceStatus = b }));
        Verify(new CreditsInfo(), (a, b) => DashboardContent.ProviderEquals(
            new() { Credits = a }, new() { Credits = b }));
        Verify(new CostCoverage(), (a, b) => DashboardContent.CostEquals(
            new() { Coverage = a }, new() { Coverage = b }));
        Verify(new PaceProjection(PaceKind.Unknown), (a, b) => DashboardContent.WindowEquals(
            new() { Pace = a }, new() { Pace = b }));
        Verify(new ModelCost(), (a, b) => DashboardContent.CostEquals(
            new() { TopModels = [a] }, new() { TopModels = [b] }));
        Verify(new ModelCost(), (a, b) => DashboardContent.DailyEquals(
            new() { ModelBreakdowns = [a] }, new() { ModelBreakdowns = [b] }));
        Verify(new DailyCost(), (a, b) => DashboardContent.CostEquals(
            new() { Daily = [a] }, new() { Daily = [b] }));
        Verify(new DailyCost(), (a, b) => DashboardContent.CostEquals(
            new() { PeakDay = a }, new() { PeakDay = b }));
        foreach (var list in new[] { nameof(BurndownSeries.Usage), nameof(BurndownSeries.Guide), nameof(BurndownSeries.Projection) })
            Verify(new BurndownPoint(0, 0), (a, b) =>
            {
                var first = new BurndownSeries(); var second = new BurndownSeries();
                typeof(BurndownSeries).GetProperty(list)!.SetValue(first, new[] { a });
                typeof(BurndownSeries).GetProperty(list)!.SetValue(second, new[] { b });
                return DashboardContent.SeriesEquals(first, second);
            });
        Verify(new DashboardRefreshEntry(), (a, b) => DashboardContent.DiagnosticsEquals(
            new() { RecentRefreshes = [a] }, new() { RecentRefreshes = [b] }));
        Verify(new DashboardAlertEntry(), (a, b) => DashboardContent.DiagnosticsEquals(
            new() { RecentAlerts = [a] }, new() { RecentAlerts = [b] }));
        Verify(new DashboardCostCacheEntry(), (a, b) => DashboardContent.DiagnosticsEquals(
            new() { CostCache = [a] }, new() { CostCache = [b] }));
    }

    private static void Verify<T>(T original, Func<T, T, bool> equals) where T : class
    {
        equals(original, (T)typeof(T).GetMethod("<Clone>$")!.Invoke(original, null)!).Should().BeTrue();
        foreach (var property in typeof(T).GetProperties().Where(p => p.SetMethod != null))
        {
            var changed = (T)typeof(T).GetMethod("<Clone>$")!.Invoke(original, null)!;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = type == typeof(string) ? "changed"
                : type == typeof(bool) ? !(bool)(property.GetValue(original) ?? false)
                : type == typeof(int) ? 1
                : type == typeof(long) ? 1L
                : type == typeof(double) ? 1d
                : type == typeof(decimal) ? 1m
                : type == typeof(DateTime) ? new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc)
                : type == typeof(DateOnly) ? new DateOnly(2026, 10, 5)
                : type.IsEnum ? Enum.GetValues(type).GetValue(1)!
                : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                    ? OneItem(type.GetGenericArguments()[0])
                    : property.GetValue(original) != null ? null : Activator.CreateInstance(type);
            property.SetValue(changed, value);
            equals(original, changed).Should().BeFalse($"{typeof(T).Name}.{property.Name} must be compared");
        }
    }

    private static Array OneItem(Type type)
    {
        var array = Array.CreateInstance(type, 1);
        array.SetValue(type == typeof(BurndownPoint) ? new BurndownPoint(0, 0) : Activator.CreateInstance(type), 0);
        return array;
    }
}
