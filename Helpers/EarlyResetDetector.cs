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
    FetchOutcome FetchOutcome = FetchOutcome.Success,
    string? Plan = null,
    int Limit = 0,
    int WindowMinutes = 0);

/// <summary>
/// Pure, static detector for unexpected early usage limit resets (e.g. unannounced OpenAI resets or Claude limit resets).
/// Account switches with identical usage metadata cannot be distinguished from resets.
/// </summary>
public static class EarlyResetDetector
{
    /// <summary>Minimum percentage point drop required to consider an early reset (filters jitter and CLI noise).</summary>
    public const double EarlyResetMinDropPoints = 10.0;

    /// <summary>Restart evidence when current usage is at or below two percent, subject to all other guards.</summary>
    public const double NearZeroPercent = 2.0;

    /// <summary>Grace period before scheduled ResetAt. A drop within this duration is considered a normal scheduled reset.</summary>
    public static readonly TimeSpan GraceBeforeReset = TimeSpan.FromMinutes(10);

    /// <summary>Maximum age of a previous reading to be considered for early-reset comparison (24 hours).</summary>
    public static readonly TimeSpan MaxPreviousReadingAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Detects whether an early reset occurred between two consecutive readings of the same usage window.
    /// A plain percentage drop with the same reset time can be a limit increase or provider
    /// recalculation (OpenAI often raises limits), so require a known, not-yet-due baseline reset
    /// and restart evidence: a reset shift exceeding ten minutes or usage at or below two percent.
    /// Plan, limit and window length changes start a new baseline. Account identity is not
    /// available in UsageData, so account switches with identical metadata cannot be distinguished.
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
        var curReadingAt = DashboardValues.Utc(current.ReadingAtUtc);
        var now = DashboardValues.Utc(nowUtc);

        if (curReadingAt <= prevReadingAt ||
            curReadingAt > now + CacheRetention.ClockSkewTolerance ||
            prevReadingAt > now + CacheRetention.ClockSkewTolerance ||
            curReadingAt - prevReadingAt > MaxPreviousReadingAge || now - prevReadingAt > MaxPreviousReadingAge ||
            previous.Plan != current.Plan || previous.Limit != current.Limit || previous.WindowMinutes != current.WindowMinutes)
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

        if (!prevResetUtc.HasValue || prevResetUtc.Value <= curReadingAt + GraceBeforeReset)
        {
            return null;
        }

        bool resetShiftedForward = curResetUtc.HasValue && curResetUtc.Value > prevResetUtc.Value + GraceBeforeReset;
        bool droppedToNearZero = current.UsedPercent <= NearZeroPercent;

        if (resetShiftedForward || droppedToNearZero)
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
            ReadingAtUtc: readingTimeUtc ?? DashboardValues.Utc(data.DataUpdatedAt ?? data.CacheUpdatedAt ?? data.FetchedAt),
            IsCached: data.UsageIsCached,
            IsStale: data.UsageIsStale,
            FetchOutcome: data.UsageFetchOutcome,
            Plan: data.Plan,
            Limit: window.Limit,
            WindowMinutes: window.WindowMinutes);
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

        var previousExtras = UniqueExtras(previousData);
        foreach (var extra in UniqueExtras(currentData))
        {
            if (previousExtras.TryGetValue(extra.Key, out var prevExtra))
            {
                Check(extra.Key, extra.Value.Title, prevExtra.Window, extra.Value.Window);
            }
        }

        return results;
    }

    internal static Dictionary<string, ExtraUsageWindow> UniqueExtras(UsageData data) =>
        (data.ExtraWindows ?? []).GroupBy(UsageHistoryStore.ExtraWindowKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<EarlyResetWindowReading> Readings(UsageData data)
    {
        if (data.Session != null) yield return CreateReading(data, "session", data.SessionLabel, data.Session);
        if (data.Weekly != null) yield return CreateReading(data, "weekly", data.WeeklyLabel, data.Weekly);
        if (data.Tertiary != null) yield return CreateReading(data, "tertiary", data.TertiaryLabel, data.Tertiary);
        foreach (var extra in UniqueExtras(data))
            yield return CreateReading(data, extra.Key, extra.Value.Title, extra.Value.Window);
    }
}
