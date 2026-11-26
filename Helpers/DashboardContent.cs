using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

/// <summary>Ordered content equality for snapshot records whose lists otherwise compare by identity.</summary>
public static class DashboardContent
{
    public static bool ProviderEquals(DashboardProvider? a, DashboardProvider? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        a with { Windows = b.Windows, Cost = b.Cost, GeminiApi = b.GeminiApi } == b &&
        SequenceEquals(a.Windows, b.Windows, WindowEquals) && CostEquals(a.Cost, b.Cost) &&
        GeminiApiEquals(a.GeminiApi, b.GeminiApi);

    public static bool GeminiApiEquals(GeminiApiSpendInfo? a, GeminiApiSpendInfo? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        a with { Models = b.Models } == b &&
        a.Models.SequenceEqual(b.Models);

    public static bool WindowEquals(DashboardWindow? a, DashboardWindow? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        a with { History = b.History } == b && SeriesEquals(a.History, b.History);

    public static bool SeriesEquals(BurndownSeries? a, BurndownSeries? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        a with { Usage = b.Usage, Guide = b.Guide, Projection = b.Projection } == b &&
        a.Usage.SequenceEqual(b.Usage) && a.Guide.SequenceEqual(b.Guide) && a.Projection.SequenceEqual(b.Projection);

    public static bool CostEquals(DashboardCost? a, DashboardCost? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        a with { Daily = b.Daily, TopModels = b.TopModels, PeakDay = b.PeakDay } == b &&
        SequenceEquals(a.Daily, b.Daily, DailyEquals) && a.TopModels.SequenceEqual(b.TopModels) && DailyEquals(a.PeakDay, b.PeakDay);

    public static bool DailyEquals(DailyCost? a, DailyCost? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        a with { ModelBreakdowns = b.ModelBreakdowns } == b && a.ModelBreakdowns.SequenceEqual(b.ModelBreakdowns);

    public static bool DiagnosticsEquals(DashboardDiagnostics? a, DashboardDiagnostics? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        a with { RecentRefreshes = b.RecentRefreshes, RecentAlerts = b.RecentAlerts, CostCache = b.CostCache } == b &&
        a.RecentRefreshes.SequenceEqual(b.RecentRefreshes) && a.RecentAlerts.SequenceEqual(b.RecentAlerts) &&
        a.CostCache.SequenceEqual(b.CostCache);

    public static bool SequenceEquals<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> equals)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (!equals(a[i], b[i])) return false;
        return true;
    }

    public static bool GraphWindowEquals(DashboardWindow? a, DashboardWindow? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        string.Equals(a.Key, b.Key, StringComparison.Ordinal) &&
        string.Equals(a.Title, b.Title, StringComparison.Ordinal) &&
        a.UsedPercent.Equals(b.UsedPercent) &&
        a.Accent == b.Accent &&
        SeriesEquals(a.History, b.History);

    public static bool GraphCostEquals(DashboardCost? a, DashboardCost? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        string.Equals(a.Currency, b.Currency, StringComparison.Ordinal) &&
        SequenceEquals(a.Daily, b.Daily, DailyEquals);

    public static bool GraphProviderEquals(DashboardProvider? a, DashboardProvider? b) =>
        ReferenceEquals(a, b) || a != null && b != null &&
        string.Equals(a.Id, b.Id, StringComparison.Ordinal) &&
        string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal) &&
        SequenceEquals(a.Windows, b.Windows, GraphWindowEquals) &&
        GraphCostEquals(a.Cost, b.Cost);
}
