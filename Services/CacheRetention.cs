using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>
/// Retention and clock-skew policies for cached last-known-good usage and cost data.
/// </summary>
public static class CacheRetention
{
    /// <summary>
    /// Maximum age of last-known-good cache data before it expires and is treated as absent (7 days).
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Maximum clock drift into the future permitted before cache data is discarded (5 minutes).
    /// </summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    public static DateTime StatusFetchedAt(UsageData data) => DashboardValues.Utc(
        data.LastSuccessfulStatusFetch ?? data.ServiceStatus?.UpdatedAt ?? data.LastSuccessfulFetch ?? data.FetchedAt);

    public static bool IsRetained(DateTime timestamp, DateTime nowUtc)
    {
        var age = nowUtc - DashboardValues.Utc(timestamp);
        return age >= -ClockSkewTolerance && age <= MaxAge;
    }

    /// <summary>Expire usage and status independently; a fresh status cannot extend old usage.</summary>
    public static UsageData? PruneExpired(UsageData data, DateTime nowUtc)
    {
        var statusAt = StatusFetchedAt(data);
        if (data.ServiceStatus != null && !IsRetained(statusAt, nowUtc))
            data = data with { ServiceStatus = null, LastSuccessfulStatusFetch = null,
                StatusIsStale = false, StatusFetchOutcome = FetchOutcome.Unavailable };
        else if (data.ServiceStatus != null)
            data = data with { LastSuccessfulStatusFetch = statusAt > nowUtc ? nowUtc : statusAt };

        if (IsRetained(data.LastSuccessfulFetch ?? data.FetchedAt, nowUtc)) return data;
        if (data.ServiceStatus == null) return null;
        return data with { Session = null, Weekly = null, Tertiary = null, ExtraWindows = [], Credits = null,
            Plan = null, Status = null, DataUpdatedAt = null, LastSuccessfulFetch = null,
            UsageIsStale = false, UsageFetchOutcome = FetchOutcome.Unavailable };
    }
}
