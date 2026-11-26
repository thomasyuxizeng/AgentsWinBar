using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

/// <summary>Why a provider is fetched (or will be fetched next). Logged for each refresh.</summary>
public enum RefreshReason
{
    /// <summary>The provider has no reading from this app session yet (just enabled, or the app just started).</summary>
    First,
    /// <summary>Its tool wrote local logs within <see cref="ProviderRefreshPlanner.ActiveWindow"/>.</summary>
    Active,
    /// <summary>Its logs are quiet: the user's interval with the adaptive back-off.</summary>
    Idle,
    /// <summary>A window is at or above <see cref="ProviderRefreshPlanner.NearLimitPercent"/> or runs out before its reset.</summary>
    NearLimit,
    /// <summary>The session is locked: the locked interval is longer than any other.</summary>
    Locked,
    /// <summary>A usage window just reset.</summary>
    Reset,
    /// <summary>A window opened and the reading was older than the caller allows.</summary>
    OnOpen,
    /// <summary>The user asked (Refresh button, Settings change, a new sign-in).</summary>
    Manual
}

/// <summary>What the planner knows about one enabled provider.</summary>
/// <param name="Provider">Provider id; invalid ids are skipped.</param>
/// <param name="LastActivityUtc">When the provider's tool last wrote its local logs, if seen.</param>
/// <param name="LastFetchUtc">When this app last asked for the provider's usage (the attempt, not the reading).</param>
/// <param name="HighestPercent">The highest used percent among its windows, if any is known.</param>
/// <param name="PaceWarning">A window runs out before its reset at the current pace.</param>
/// <param name="UnchangedStreak">Refreshes in a row whose reading did not change (the adaptive back-off).</param>
/// <param name="RateLimitedUntilUtc">The provider answered 429 and is not called again before this time.</param>
public readonly record struct ProviderRefreshInput(
    string Provider,
    DateTime? LastActivityUtc,
    DateTime? LastFetchUtc,
    double? HighestPercent = null,
    bool PaceWarning = false,
    int UnchangedStreak = 0,
    DateTime? RateLimitedUntilUtc = null);

/// <summary>Settings and machine state shared by every provider in one plan.</summary>
public sealed record RefreshPlanOptions
{
    /// <summary>The user's refresh interval (Settings).</summary>
    public TimeSpan BaseInterval { get; init; } = TimeSpan.FromMinutes(2);
    public bool AdaptiveEnabled { get; init; } = true;
    /// <summary>"Refresh faster while a tool is in use". Off gives every provider today's single delay.</summary>
    public bool ActivityRefreshEnabled { get; init; } = true;
    public bool BatterySaver { get; init; }
    public bool Locked { get; init; }
    public TimeSpan LockedInterval { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>When one provider is due and why.</summary>
/// <param name="Interval">The whole interval for its current state (the time since its last fetch is not subtracted).</param>
/// <param name="BackoffStep">0 at the base interval, 1 when doubled, 2 when tripled (idle providers only).</param>
/// <param name="RateLimited">The due time was pushed back to the end of a 429 cooldown.</param>
public readonly record struct ProviderRefreshDue(
    string Provider,
    DateTime DueUtc,
    TimeSpan Interval,
    RefreshReason Reason,
    int BackoffStep = 0,
    bool RateLimited = false);

/// <summary>
/// Pure per-provider refresh schedule. A provider whose tool wrote local logs in the last <see cref="ActiveWindow"/>
/// is "active": its usage is probably moving, so it is read every <see cref="ActiveInterval"/> (Claude never more
/// often than <see cref="ClaudeActiveFloor"/>, because its endpoint rate-limits hard). An idle provider can only move
/// from another device, so it follows the user's interval with the adaptive back-off (doubled after 2 unchanged
/// refreshes, tripled after 4, at most 15 minutes), except that a provider near a limit is read at least every
/// <see cref="NearLimitInterval"/>. Battery saver stretches every interval and a locked session waits at least the
/// locked interval, as before. With the setting off every provider gets today's single delay.
/// </summary>
public static class ProviderRefreshPlanner
{
    /// <summary>Logs written within this long make a provider active.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(5);
    /// <summary>How often an active provider is read (never slower than the user's interval).</summary>
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromMinutes(2);
    /// <summary>
    /// Claude is never read more often than this, whatever its state. Claude's usage endpoint rate-limits hard and the
    /// safe rate is not known; a live test decides the final value.
    /// </summary>
    public static readonly TimeSpan ClaudeActiveFloor = TimeSpan.FromMinutes(2);
    /// <summary>A provider near a limit is read at least this often, even when idle.</summary>
    public static readonly TimeSpan NearLimitInterval = TimeSpan.FromMinutes(5);
    /// <summary>A window used this much (percent) or more counts as near its limit.</summary>
    public const double NearLimitPercent = 85;
    /// <summary>A provider due within this long after the one that wakes the timer is fetched with it (one wake-up, one redraw).</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(20);
    /// <summary>How much shorter Claude's reading cache is than its floor, so the cache never turns a due refresh into a no-op.</summary>
    public static readonly TimeSpan ClaudeCacheMargin = TimeSpan.FromSeconds(30);

    /// <summary>Claude's reading cache while the activity schedule is on: just under its floor.</summary>
    public static TimeSpan ClaudeCacheLifetime => ClaudeActiveFloor - ClaudeCacheMargin;

    private const string Claude = "claude";

    /// <summary>Whether logs written at <paramref name="lastActivityUtc"/> make the provider active now.</summary>
    /// <remarks>A time ahead of now (the clock was set back) counts as just now.</remarks>
    public static bool IsActive(DateTime? lastActivityUtc, DateTime nowUtc) =>
        lastActivityUtc is { } last && Utc(nowUtc) - Utc(last) <= ActiveWindow;

    public static bool IsNearLimit(double? highestPercent, bool paceWarning) =>
        paceWarning || highestPercent is { } percent && double.IsFinite(percent) && percent >= NearLimitPercent;

    /// <summary>0 at the base interval, 1 after 2 unchanged refreshes, 2 after 4 (as <see cref="AdaptiveRefreshPolicy"/>).</summary>
    public static int BackoffStep(int unchangedStreak, bool adaptiveEnabled) =>
        !adaptiveEnabled || unchangedStreak < 2 ? 0 : unchangedStreak < 4 ? 1 : 2;

    /// <summary>The shortest time between two reads of <paramref name="provider"/>.</summary>
    public static TimeSpan MinimumInterval(string provider) =>
        string.Equals(provider, Claude, StringComparison.OrdinalIgnoreCase) ? ClaudeActiveFloor : TimeSpan.Zero;

    /// <summary>Plans every valid provider; invalid ids and duplicates are skipped.</summary>
    public static IReadOnlyList<ProviderRefreshDue> Plan(IEnumerable<ProviderRefreshInput> providers,
        RefreshPlanOptions options, DateTime nowUtc)
    {
        var plan = new List<ProviderRefreshDue>(3);
        foreach (var input in providers)
        {
            if (!ProviderConstants.IsValidProvider(input.Provider)) continue;
            var due = PlanProvider(input, options, nowUtc);
            if (plan.Exists(p => p.Provider == due.Provider)) continue;
            plan.Add(due);
        }
        return plan;
    }

    public static ProviderRefreshDue PlanProvider(ProviderRefreshInput input, RefreshPlanOptions options, DateTime nowUtc)
    {
        var provider = ProviderConstants.ValidateAndNormalize(input.Provider);
        var now = Utc(nowUtc);
        var baseInterval = options.BaseInterval > TimeSpan.Zero ? options.BaseInterval : TimeSpan.FromMinutes(2);
        var streak = Math.Max(0, input.UnchangedStreak);
        TimeSpan interval;
        RefreshReason reason;
        var step = 0;
        if (!options.ActivityRefreshEnabled)
        {
            // Today's schedule: one adaptive delay, stretched by battery saver.
            interval = AdaptiveRefreshPolicy.GetDelay(baseInterval, streak, options.AdaptiveEnabled, options.BatterySaver);
            reason = RefreshReason.Idle;
            step = BackoffStep(streak, options.AdaptiveEnabled);
        }
        else
        {
            var idle = AdaptiveRefreshPolicy.GetDelay(baseInterval, streak, options.AdaptiveEnabled);
            if (IsActive(input.LastActivityUtc, now))
            {
                interval = ActiveInterval < baseInterval ? ActiveInterval : baseInterval;
                reason = RefreshReason.Active;
            }
            else if (IsNearLimit(input.HighestPercent, input.PaceWarning) && idle > NearLimitInterval)
            {
                interval = NearLimitInterval;
                reason = RefreshReason.NearLimit;
            }
            else
            {
                interval = idle;
                reason = RefreshReason.Idle;
                step = BackoffStep(streak, options.AdaptiveEnabled);
            }
            if (options.BatterySaver) interval = AdaptiveRefreshPolicy.GetDelay(interval, 0, false, batterySaver: true);
            var floor = MinimumInterval(provider);
            if (interval < floor) interval = floor;
        }
        if (options.Locked && options.LockedInterval > interval)
        {
            interval = options.LockedInterval;
            reason = RefreshReason.Locked;
        }

        if (input.LastFetchUtc is not { } lastFetch) return Limited(new(provider, now, interval, RefreshReason.First, step));
        // A fetch stamped ahead of now (the clock was set back) counts as just now, so the wait never exceeds one interval.
        var since = Utc(lastFetch) > now ? now : Utc(lastFetch);
        return Limited(new(provider, Add(since, interval), interval, reason, step));

        ProviderRefreshDue Limited(ProviderRefreshDue due) =>
            input.RateLimitedUntilUtc is { } until && Utc(until) > due.DueUtc
                ? due with { DueUtc = Utc(until), RateLimited = true }
                : due;
    }

    /// <summary>
    /// The providers to fetch now: every provider that is due, plus any due within <see cref="CoalesceWindow"/>
    /// (never Claude early, its floor is strict). On a locked session every planned provider goes at once, as today.
    /// Empty when nothing is due yet.
    /// </summary>
    public static IReadOnlyList<ProviderRefreshDue> SelectDue(IReadOnlyList<ProviderRefreshDue> plan, DateTime nowUtc,
        bool locked = false)
    {
        var now = Utc(nowUtc);
        if (!plan.Any(p => p.DueUtc <= now)) return [];
        if (locked) return plan.Where(p => !p.RateLimited || p.DueUtc <= now).ToArray();
        return plan.Where(p => p.DueUtc <= now ||
            !p.RateLimited && MinimumInterval(p.Provider) == TimeSpan.Zero && p.DueUtc - now <= CoalesceWindow).ToArray();
    }

    /// <summary>The earliest due time in the plan, or null when the plan is empty.</summary>
    public static DateTime? NextDue(IReadOnlyList<ProviderRefreshDue> plan) =>
        plan.Count == 0 ? null : plan.Min(p => p.DueUtc);

    private static DateTime Add(DateTime time, TimeSpan delta) =>
        DateTime.MaxValue - time < delta ? DateTime.MaxValue : time + delta;

    private static DateTime Utc(DateTime time) => DashboardValues.Utc(time);
}
