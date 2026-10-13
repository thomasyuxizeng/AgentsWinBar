using System.Globalization;
using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

/// <summary>Reports only the provider/window identities captured when a targeted attempt began.</summary>
public static class ResetRefreshOutcome
{
    public static string? Describe(IReadOnlyList<EarlyResetWindowReading> targets, UsageData result, DateTime resultAtUtc)
    {
        var current = EarlyResetDetector.Readings(result).ToDictionary(r => r.WindowKey, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (var target in targets.Where(t => string.Equals(t.Provider, result.Provider, StringComparison.OrdinalIgnoreCase)))
        {
            current.TryGetValue(target.WindowKey, out var reading);
            var confirmed = UsageResetTracker.Detect(target, reading, resultAtUtc);
            var qualifier = result.UsageFetchOutcome != FetchOutcome.Success ? " (unavailable)"
                : result.UsageIsCached || result.UsageIsStale ? " (cached/stale)"
                : reading == null ? " (missing)"
                : !EarlyResetDetector.AreComparable(target, reading, resultAtUtc) ? " (unconfirmed)" : "";
            var delay = (DashboardValues.Utc(resultAtUtc) - DashboardValues.Utc(target.ResetAt!.Value)).TotalSeconds;
            parts.Add($"{target.WindowKey} {(confirmed ? "NEW" : "OLD")}{qualifier} +{delay.ToString("F1", CultureInfo.InvariantCulture)}s");
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
