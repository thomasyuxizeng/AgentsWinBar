using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

/// <summary>How loud a dashboard attention entry is: amber for caution, red for critical.</summary>
public enum AttentionSeverity { Caution, Critical }

/// <summary>
/// One entry in the dashboard's "Needs attention" list: a short label ("Near limit"), what it applies to
/// ("Claude Weekly, Codex Weekly") and the provider to scroll to when it is clicked.
/// </summary>
public sealed record AttentionItem(string Label, string Detail, AttentionSeverity Severity, string ProviderId);

/// <summary>
/// Pure, cross-provider summary logic for the dashboard: what needs attention, which providers count as stale,
/// which usage windows share a reset and whether the all-provider spend adds anything to the panels.
/// No WinUI types, so it is unit tested; the page only formats the results.
/// </summary>
public static class DashboardGlance
{
    /// <summary>Two resets this close together are treated as the same moment (CLI timestamps jitter by seconds).</summary>
    public static readonly TimeSpan SameResetTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// True when the provider's numbers should not be trusted at face value: a failed refresh kept old usage,
    /// the reading itself is old, or the status or cost shown is last-known-good. Unlike
    /// <see cref="DashboardProvider.IsStale"/> it ignores plain age since the last refresh (the header already says when that was).
    /// </summary>
    public static bool IsStale(DashboardProvider? provider) => provider != null &&
        (provider.UsageIsStale || provider.StatusIsStale || provider.OldReadingUtc != null || provider.Cost?.IsStale == true);

    /// <summary>"Claude Weekly": a window named with its provider, for cross-provider lists.</summary>
    public static string WindowLabel(DashboardProvider provider, DashboardWindow window) =>
        string.IsNullOrWhiteSpace(window.Title) ? provider.DisplayName : $"{provider.DisplayName} {window.Title}";

    /// <summary>
    /// The attention list, most severe first. Each window appears at most once (at limit, then runs out before reset,
    /// then near limit); providers with the same kind of problem share one entry. Empty when everything is fine.
    /// </summary>
    public static IReadOnlyList<AttentionItem> Attention(IReadOnlyList<DashboardProvider>? providers)
    {
        var items = new List<AttentionItem>();
        if (providers == null || providers.Count == 0) return items;

        void Group(string label, AttentionSeverity severity, IReadOnlyList<(string ProviderId, string Name)> entries)
        {
            if (entries.Count == 0) return;
            items.Add(new(label, string.Join(", ", entries.Select(e => e.Name)), severity, entries[0].ProviderId));
        }

        foreach (var p in providers.Where(p => p.AuthRequired))
            items.Add(new($"{p.DisplayName} is signed out", p.AuthHint ?? string.Empty, AttentionSeverity.Critical, p.Id));
        // A signed-out provider gets one explicit entry; its held windows are not live warnings.
        providers = providers.Where(p => !p.AuthRequired).ToArray();
        var failed = providers.Where(p => !string.IsNullOrWhiteSpace(p.Error)).Select(p => (p.Id, p.DisplayName)).ToList();
        var incidents = providers.Where(p => ProviderStatusPresentation.IsIncident(p.ServiceStatus)).ToList();
        var windows = providers.SelectMany(p => p.Windows.Select(w => (Provider: p, Window: w))).ToList();
        var atLimit = windows.Where(x => x.Window.UsedPercent >= UsageThresholds.Critical).ToList();
        var runsOut = windows.Except(atLimit).Where(x => x.Window.Pace.Kind == PaceKind.WillRunOut).ToList();
        var nearLimit = windows.Except(atLimit).Except(runsOut)
            .Where(x => x.Window.UsedPercent >= UsageThresholds.Warning).ToList();
        var api = providers.Select(p => (Provider: p, Api: p.ApiCredits)).FirstOrDefault(x => x.Api is { State: ApiCreditsState.Ok });

        Group("Fetch failed", AttentionSeverity.Critical, failed);
        foreach (var p in incidents.Where(p => ProviderStatusPresentation.Severity(p.ServiceStatus) == ProviderStatusSeverity.Error))
            items.Add(new($"{p.DisplayName} service", ProviderStatusPresentation.Description(p.ServiceStatus), AttentionSeverity.Critical, p.Id));
        Group("At limit", AttentionSeverity.Critical, atLimit.Select(x => (x.Provider.Id, WindowLabel(x.Provider, x.Window))).ToList());
        if (api.Api is { } overBudget && (overBudget.IsOverBudget || overBudget.PercentUsed >= UsageThresholds.Critical))
            items.Add(new("API credits", overBudget.IsOverBudget ? "over budget" : "nearly used up", AttentionSeverity.Critical, api.Provider.Id));

        foreach (var p in incidents.Where(p => ProviderStatusPresentation.Severity(p.ServiceStatus) != ProviderStatusSeverity.Error))
            items.Add(new($"{p.DisplayName} service", ProviderStatusPresentation.Description(p.ServiceStatus), AttentionSeverity.Caution, p.Id));
        Group("Runs out before reset", AttentionSeverity.Caution, runsOut.Select(x => (x.Provider.Id, WindowLabel(x.Provider, x.Window))).ToList());
        Group("Near limit", AttentionSeverity.Caution, nearLimit.Select(x => (x.Provider.Id, WindowLabel(x.Provider, x.Window))).ToList());
        if (api.Api is { IsOverBudget: false } low && low.PercentUsed is >= UsageThresholds.Warning and < UsageThresholds.Critical)
            items.Add(new("API credits", "running low", AttentionSeverity.Caution, api.Provider.Id));
        Group("Stale data", AttentionSeverity.Caution,
            providers.Where(p => string.IsNullOrWhiteSpace(p.Error) && IsStale(p)).Select(p => (p.Id, p.DisplayName)).ToList());
        return items;
    }

    /// <summary>
    /// Group index per window, in order: consecutive windows that reset at the same moment share a group.
    /// A window without a known reset is always alone.
    /// </summary>
    public static IReadOnlyList<int> ResetGroups(IReadOnlyList<DashboardWindow>? windows)
    {
        var groups = new List<int>();
        if (windows == null) return groups;
        var group = -1;
        for (var i = 0; i < windows.Count; i++)
        {
            var same = i > 0 && SameReset(windows[i - 1].ResetsAtUtc, windows[i].ResetsAtUtc);
            if (!same) group++;
            groups.Add(group);
        }
        return groups;
    }

    /// <summary>True when both resets are known and within <see cref="SameResetTolerance"/> of each other.</summary>
    public static bool SameReset(DateTime? a, DateTime? b) =>
        a is { } x && b is { } y && (DashboardValues.Utc(x) - DashboardValues.Utc(y)).Duration() <= SameResetTolerance;

    /// <summary>The verb for a reset caption shared by <paramref name="count"/> windows: "Resets", "Both reset", "All 3 reset".</summary>
    public static string ResetVerb(int count) => count <= 1 ? "Resets" : count == 2 ? "Both reset" : $"All {count} reset";

    /// <summary>
    /// The all-provider spend only adds information when two or more providers report cost;
    /// with one provider it would repeat that provider's own numbers.
    /// </summary>
    public static bool ShowCombinedSpend(IReadOnlyList<DashboardProvider>? providers) =>
        providers != null && providers.Count(p => p.Cost != null) >= 2;
}
