using AgentsWinBar.Models;
using AgentsWinBar.Services;

namespace AgentsWinBar.Helpers;

/// <summary>
/// Updates time-dependent window fields on an existing <see cref="DashboardSnapshot"/> for the 30-second clock
/// tick without rebuilding history series, cost totals, or diagnostics. Falls back to a full snapshot when a
/// boundary (reset time, local/UTC day rollover, API stale threshold, retention expiry) is crossed.
/// </summary>
public static class DashboardClockUpdater
{
    private static readonly TimeSpan ApiCreditsStaleAfter = TimeSpan.FromMinutes(15);

    public static bool TryRefreshClock(
        DashboardSnapshot current,
        DateTime nowUtc,
        TimeZoneInfo localZone,
        out DashboardSnapshot updated)
    {
        updated = current;
        var now = DashboardValues.Utc(nowUtc);
        var prev = DashboardValues.Utc(current.GeneratedAtUtc);
        if (now < prev) return false;

        var prevLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(prev, localZone));
        var nowLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, localZone));
        if (prevLocalDate != nowLocalDate) return false;

        var prevUtcDate = DateOnly.FromDateTime(prev);
        var nowUtcDate = DateOnly.FromDateTime(now);

        var sourceProviders = current.Providers;
        var updatedProviders = new DashboardProvider[sourceProviders.Count];
        for (var i = 0; i < sourceProviders.Count; i++)
        {
            var provider = sourceProviders[i];
            if (provider.EarlyResetAtUtc is { } er && now - DashboardValues.Utc(er) >= EarlyResetStore.ActiveDisplayWindow)
                return false;

            if (provider.ServiceStatus != null && provider.LastSuccessfulStatusFetchUtc is { } sAt &&
                !CacheRetention.IsRetained(sAt, now))
                return false;

            // Cost data has no maximum age (see CacheRetention.RetainedCost): only the builder's rule can drop it
            if (provider.Cost?.LastSuccessfulFetchUtc is { } cAt &&
                !CacheRetention.IsCostRetained(cAt, now))
                return false;

            if (provider.ApiCredits is { } api)
            {
                if (prevUtcDate != nowUtcDate) return false;
                if (api.State == ApiCreditsState.Ok && !api.IsStale && api.FetchedAtUtc is { } fetched &&
                    now - DashboardValues.Utc(fetched) > ApiCreditsStaleAfter)
                    return false;
            }

            var windows = provider.Windows;
            var updatedWindows = new DashboardWindow[windows.Count];
            for (var j = 0; j < windows.Count; j++)
            {
                var w = windows[j];
                if (w.EarlyResetAtUtc is { } wer && now - DashboardValues.Utc(wer) >= EarlyResetStore.ActiveDisplayWindow)
                    return false;
                if (w.ResetsAtUtc is { } reset)
                {
                    var resetUtc = DashboardValues.Utc(reset);
                    if (prev < resetUtc && now >= resetUtc) return false;
                }

                // The same rule as the snapshot builder: no pace for a window whose reported percent was not a number
                var pace = w.UsedPercentKnown && double.IsFinite(w.UsedPercent)
                    ? PaceCalculator.Project(w.UsedPercent, w.WindowMinutes, w.ResetsAtUtc, now)
                    : new PaceProjection(PaceKind.Unknown);
                updatedWindows[j] = w with
                {
                    ElapsedPercent = ResetTimeFormatter.ElapsedPercent(w.ResetsAtUtc, w.WindowMinutes, now),
                    ResetText = ResetTimeFormatter.Format(w.ResetsAtUtc, now) ?? w.ResetText,
                    Pace = pace,
                    PaceSummary = PaceCalculator.Summarize(pace)
                };
            }

            updatedProviders[i] = provider with { Windows = updatedWindows };
        }

        updated = current with { GeneratedAtUtc = now, Providers = updatedProviders };
        return true;
    }
}
