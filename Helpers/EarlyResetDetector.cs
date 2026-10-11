using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Helpers;

/// <summary>
/// Represents a detected early reset event for a usage window.
/// </summary>
public sealed record EarlyReset(
    string Provider,
    string WindowKey,
    string WindowTitle,
    double PreviousPercent,
    double CurrentPercent,
    DateTime? PreviousResetAtUtc,
    DateTime? NewResetAtUtc,
    DateTime DetectedAtUtc);

/// <summary>
/// An isolated snapshot of one usage window used for pure early-reset evaluation.
/// </summary>
public sealed record EarlyResetWindowReading(
    string Provider,
    string WindowKey,
    string WindowTitle,
    double UsedPercent,
    DateTime? ResetAt,
    DateTime ReadingAtUtc,
    bool IsCached = false,
    bool IsStale = false,
    FetchOutcome FetchOutcome = FetchOutcome.Success);

/// <summary>
/// Pure, static detector for unexpected early usage limit resets (e.g. unannounced OpenAI resets or Claude limit resets).
/// </summary>
public static class EarlyResetDetector
{
    /// <summary>Minimum percentage point drop required to consider an early reset (filters jitter and CLI noise).</summary>
    public const double EarlyResetMinDropPoints = 10.0;

    /// <summary>Large drop threshold in points to recognize resets for providers with rolling/unknown reset times.</summary>
    public const double RollingResetMinDropPoints = 25.0;

    /// <summary>Near-zero threshold percentage: drops to at or below this value from >= 10 points are considered resets.</summary>
    public const double NearZeroPercent = 1.0;

    /// <summary>Grace period before scheduled ResetAt. A drop within this duration is considered a normal scheduled reset.</summary>
    public static readonly TimeSpan GraceBeforeReset = TimeSpan.FromMinutes(10);

    /// <summary>Maximum age of a previous reading to be considered for early-reset comparison (24 hours).</summary>
    public static readonly TimeSpan MaxPreviousReadingAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Detects whether an early reset occurred between two consecutive readings of the same usage window.
    /// </summary>
    /// <param name="previous">The previous reading of the window.</param>
    /// <param name="current">The current reading of the window.</param>
    /// <param name="nowUtc">The current timestamp in UTC.</param>
    /// <returns>An <see cref="EarlyReset"/> event if an early reset was detected; otherwise, null.</returns>
    public static EarlyReset? Detect(EarlyResetWindowReading? previous, EarlyResetWindowReading? current, DateTime nowUtc)
    {
        if (previous == null || current == null)
        {
            return null;
        }

        if (!ProviderConstants.IsValidProvider(previous.Provider) ||
            !ProviderConstants.IsValidProvider(current.Provider))
        {
            return null;
        }

        var prevProvider = ProviderConstants.ValidateAndNormalize(previous.Provider);
        var curProvider = ProviderConstants.ValidateAndNormalize(current.Provider);
        if (!string.Equals(prevProvider, curProvider, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(previous.WindowKey) ||
            !string.Equals(previous.WindowKey, current.WindowKey, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Both readings must be fresh successful readings (not cached/stale replays or failures)
        if (previous.IsCached || previous.IsStale || previous.FetchOutcome != FetchOutcome.Success)
        {
            return null;
        }

        if (current.IsCached || current.IsStale || current.FetchOutcome != FetchOutcome.Success)
        {
            return null;
        }

        var prevReadingAt = DashboardValues.Utc(previous.ReadingAtUtc);
        var curReadingAt = DashboardValues.Utc(current.ReadingAtUtc != default ? current.ReadingAtUtc : nowUtc);
        var now = DashboardValues.Utc(nowUtc);

        // Previous reading must not be older than 24 hours
        if (curReadingAt - prevReadingAt > MaxPreviousReadingAge || now - prevReadingAt > MaxPreviousReadingAge)
        {
            return null;
        }

        if (!double.IsFinite(previous.UsedPercent) || !double.IsFinite(current.UsedPercent))
        {
            return null;
        }

        var drop = previous.UsedPercent - current.UsedPercent;

        // Small decreases (< 10 points) are never flagged (jitter, rounding, minor adjustments)
        if (drop < EarlyResetMinDropPoints)
        {
            return null;
        }

        DateTime? prevResetUtc = previous.ResetAt.HasValue ? DashboardValues.Utc(previous.ResetAt.Value) : null;
        DateTime? curResetUtc = current.ResetAt.HasValue ? DashboardValues.Utc(current.ResetAt.Value) : null;

        // Both reset times unknown:
        if (prevResetUtc == null && curResetUtc == null)
        {
            // Windows with unknown ResetAt on both sides when the drop is < 25 points must never flag
            if (drop < RollingResetMinDropPoints)
            {
                return null;
            }

            return new EarlyReset(
                Provider: prevProvider,
                WindowKey: current.WindowKey,
                WindowTitle: current.WindowTitle,
                PreviousPercent: previous.UsedPercent,
                CurrentPercent: current.UsedPercent,
                PreviousResetAtUtc: null,
                NewResetAtUtc: null,
                DetectedAtUtc: now);
        }

        // Previous reset time unknown, but current is known:
        if (prevResetUtc == null)
        {
            if (drop < RollingResetMinDropPoints)
            {
                return null;
            }

            return new EarlyReset(
                Provider: prevProvider,
                WindowKey: current.WindowKey,
                WindowTitle: current.WindowTitle,
                PreviousPercent: previous.UsedPercent,
                CurrentPercent: current.UsedPercent,
                PreviousResetAtUtc: null,
                NewResetAtUtc: curResetUtc,
                DetectedAtUtc: now);
        }

        // Previous ResetAt is known:
        // Must be more than GraceBeforeReset (10 min) after CURRENT reading time (so the old window had not ended).
        // Reset time passed or within 10 minutes is a normal scheduled reset and must NEVER count.
        if (prevResetUtc.Value <= curReadingAt + GraceBeforeReset)
        {
            return null;
        }

        // Old window had not ended! Early reset if:
        // 1. current.ResetAt is later than previous.ResetAt, OR
        // 2. drop >= 25 points (rolling window drop), OR
        // 3. usedPercent dropped to ~0 from >= 10 with a not-yet-due reset
        bool resetShiftedForward = curResetUtc.HasValue && curResetUtc.Value > prevResetUtc.Value;
        bool rollingLargeDrop = drop >= RollingResetMinDropPoints;
        bool droppedToNearZero = previous.UsedPercent >= EarlyResetMinDropPoints && current.UsedPercent <= NearZeroPercent;

        if (resetShiftedForward || rollingLargeDrop || droppedToNearZero)
        {
            return new EarlyReset(
                Provider: prevProvider,
                WindowKey: current.WindowKey,
                WindowTitle: current.WindowTitle,
                PreviousPercent: previous.UsedPercent,
                CurrentPercent: current.UsedPercent,
                PreviousResetAtUtc: prevResetUtc,
                NewResetAtUtc: curResetUtc,
                DetectedAtUtc: now);
        }

        return null;
    }

    /// <summary>
    /// Creates a reading snapshot from a <see cref="UsageData"/> provider and <see cref="UsageWindow"/>.
    /// </summary>
    public static EarlyResetWindowReading CreateReading(
        UsageData data,
        string windowKey,
        string windowTitle,
        UsageWindow window,
        DateTime? readingTimeUtc = null)
    {
        return new EarlyResetWindowReading(
            Provider: data.Provider,
            WindowKey: windowKey,
            WindowTitle: windowTitle,
            UsedPercent: window.Percent,
            ResetAt: window.ResetAt,
            ReadingAtUtc: readingTimeUtc ?? DashboardValues.Utc(data.DataUpdatedAt ?? data.FetchedAt),
            IsCached: data.UsageIsCached,
            IsStale: data.UsageIsStale,
            FetchOutcome: data.UsageFetchOutcome);
    }

    /// <summary>
    /// Detects whether an early reset occurred between two specific usage windows.
    /// </summary>
    public static EarlyReset? Detect(
        UsageData? previousData,
        UsageWindow? previousWindow,
        UsageData? currentData,
        UsageWindow? currentWindow,
        string windowKey,
        string windowTitle,
        DateTime nowUtc)
    {
        if (previousData == null || previousWindow == null || currentData == null || currentWindow == null)
        {
            return null;
        }

        var prevReading = CreateReading(previousData, windowKey, windowTitle, previousWindow);
        var curReading = CreateReading(currentData, windowKey, windowTitle, currentWindow);
        return Detect(prevReading, curReading, nowUtc);
    }

    /// <summary>
    /// Evaluates all matched usage windows between previous and current readings for early resets.
    /// </summary>
    public static IReadOnlyList<EarlyReset> DetectAll(
        UsageData? previousData,
        UsageData? currentData,
        DateTime nowUtc)
    {
        if (previousData == null || currentData == null)
        {
            return [];
        }

        var results = new List<EarlyReset>();

        void Check(string key, string title, UsageWindow? prevWin, UsageWindow? curWin)
        {
            if (prevWin == null || curWin == null) return;
            var reset = Detect(previousData, prevWin, currentData, curWin, key, title, nowUtc);
            if (reset != null) results.Add(reset);
        }

        Check("session", currentData.SessionLabel, previousData.Session, currentData.Session);
        Check("weekly", currentData.WeeklyLabel, previousData.Weekly, currentData.Weekly);
        Check("tertiary", currentData.TertiaryLabel, previousData.Tertiary, currentData.Tertiary);

        foreach (var extra in currentData.ExtraWindows)
        {
            var key = UsageHistoryStore.ExtraWindowKey(extra);
            var prevExtra = previousData.ExtraWindows.FirstOrDefault(e =>
                string.Equals(UsageHistoryStore.ExtraWindowKey(e), key, StringComparison.OrdinalIgnoreCase));
            if (prevExtra != null)
            {
                Check(key, extra.Title, prevExtra.Window, extra.Window);
            }
        }

        return results;
    }
}
