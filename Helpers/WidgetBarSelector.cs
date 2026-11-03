using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Helpers;

/// <summary>
/// One bar to show on a compact widget.
/// </summary>
/// <param name="Title">Name of the window when it is not the provider's main session or weekly one
/// (e.g. "Fable only", "Claude and GPT 5-hour"); null for the main windows.</param>
/// <param name="Window">The usage window.</param>
public sealed record WidgetBar(string? Title, UsageWindow Window)
{
    public DateTime? ResetDetectedAtUtc => Window.ResetDetectedAtUtc;
    public double? ResetFromUsedPercent => Window.ResetFromUsedPercent;

    /// <summary>
    /// Colour family of the bar: standard blue for the main windows, a provider colour for extra windows.
    /// </summary>
    public WindowAccent Accent => WindowAccents.FromTitle(Title);

    /// <summary>
    /// Tooltip text for the bar: the pace sentence, prefixed with the window's name when it isn't one of the
    /// main windows (so you can tell which bar you are looking at).
    /// </summary>
    public string TooltipText
    {
        get
        {
            var pace = Window.PaceSummary;
            return Title == null ? pace : string.IsNullOrEmpty(pace) ? Title : $"{Title}: {pace}";
        }
    }
}

/// <summary>
/// Chooses which of a provider's usage windows to show on the compact widgets (the floating overlay and
/// the taskbar strip), which only have room for two bars per provider. Normally that is the session and
/// weekly windows; a different window takes a slot when it is more relevant: the one that has been changing
/// recently, or one running ahead of schedule. Windows at or near their limit are deliberately ranked last,
/// so the widgets show the resources you still have. See <see cref="Score"/> for the full priority.
/// </summary>
public static class WidgetBarSelector
{
    /// <summary>How long after its last change a window still counts as "recently changed".</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(30);

    /// <summary>Usage this far ahead of the time elapsed counts as running ahead of schedule.</summary>
    private const double AheadOfPacePoints = 15;

    public static IReadOnlyList<WidgetBar> Pick(UsageData data, DateTime nowUtc, int count = 2)
    {
        // Candidates in their natural order: session, weekly, tertiary, then the extras
        var candidates = new List<(WidgetBar Bar, bool IsExtra)>();
        if (data.Session != null) candidates.Add((new WidgetBar(null, data.Session), false));
        if (data.Weekly != null) candidates.Add((new WidgetBar(null, data.Weekly), false));
        if (data.Tertiary != null) candidates.Add((new WidgetBar(data.TertiaryLabel, data.Tertiary), true));
        foreach (var extra in data.ExtraWindows)
        {
            candidates.Add((new WidgetBar(extra.Title, extra.Window), true));
        }

        if (candidates.Count <= count)
        {
            return candidates.Select(c => c.Bar).ToList();
        }

        var ranked = candidates
            .Select((c, order) => (c.Bar, c.IsExtra, Order: order, Score: Score(c.Bar.Window, order, c.IsExtra, nowUtc)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Order)
            .ToList();

        // At most one extra window may take a slot: a provider's row should never be all extras (a Gemini
        // row showing only Claude/GPT bars would be misleading), so the rest go to its own main windows.
        var chosen = new List<(WidgetBar Bar, bool IsExtra, int Order, double Score)>();
        foreach (var candidate in ranked)
        {
            if (chosen.Count >= count) break;
            if (candidate.IsExtra && chosen.Any(c => c.IsExtra)) continue;
            chosen.Add(candidate);
        }

        // Not enough main windows to fill the row: top up with the best remaining extras
        foreach (var candidate in ranked)
        {
            if (chosen.Count >= count) break;
            if (!chosen.Contains(candidate)) chosen.Add(candidate);
        }

        // A short window whose longer sibling is used up can't be used either: show the sibling in its place
        for (var i = 0; i < chosen.Count; i++)
        {
            var shorter = chosen[i];
            var blocker = ranked.FirstOrDefault(r => IsBlocking(r, shorter) && !chosen.Contains(r));
            if (blocker.Bar != null) chosen[i] = blocker;
        }

        // Show them in their natural order so positions stay stable
        return chosen.OrderBy(c => c.Order).Select(c => c.Bar).ToList();
    }

    /// <summary>
    /// Whether <paramref name="longer"/> is the at-its-limit longer window of the same pool as <paramref name="shorter"/>
    /// (weekly over session, "External 7d" over "External 5h"), which makes the shorter one unusable while it lasts.
    /// </summary>
    private static bool IsBlocking(
        (WidgetBar Bar, bool IsExtra, int Order, double Score) longer,
        (WidgetBar Bar, bool IsExtra, int Order, double Score) shorter)
    {
        if (longer.IsExtra != shorter.IsExtra) return false;
        if (longer.Bar.Window.Percent < 100 || shorter.Bar.Window.Percent >= 100) return false;
        if (shorter.Bar.Window.WindowMinutes <= 0 || longer.Bar.Window.WindowMinutes <= shorter.Bar.Window.WindowMinutes) return false;
        // The main windows are one pool; extras pool by title
        return !longer.IsExtra || (PoolKey(longer.Bar.Title) is { } pool && pool == PoolKey(shorter.Bar.Title));
    }

    /// <summary>The title without its trailing window length ("External 5h" and "External 7d" share "External"); null when it has none.</summary>
    private static string? PoolKey(string? title)
    {
        var space = title?.LastIndexOf(' ') ?? -1;
        return space > 0 ? title![..space] : null;
    }

    /// <summary>
    /// Higher is more relevant. The priority, most important first:
    /// 1. the window that changed most recently (within <see cref="RecentWindow"/>),
    /// 2. a window running ahead of its schedule,
    /// 3. (nothing special: the quiet windows, which still have plenty left),
    /// 4. a window near its limit (90%+),
    /// 5. a window at its limit.
    /// At or near a limit there is little left to do with it, so those sink below windows with resources remaining.
    /// Within each tier the natural order (session, weekly, ...) breaks ties.
    /// </summary>
    internal static double Score(UsageWindow window, int naturalOrder, bool isExtra, DateTime nowUtc)
    {
        var natural = 100.0 - naturalOrder * 10;
        var used = window.Percent;

        // 1. Recently changed
        if (window.LastChangedAt is { } changedAt)
        {
            var age = nowUtc - changedAt;
            if (age >= TimeSpan.Zero && age < RecentWindow)
            {
                var freshness = 1 - age.TotalMinutes / RecentWindow.TotalMinutes;   // 1 = just now, 0 = 30 min ago
                // A specific window that moved is more informative than the general ones
                return 4000 + 150 * freshness + (isExtra ? 40 : 0) + natural;
            }
        }

        // 5. At the limit, then 4. near it: least interesting, whatever the pace (little left to do with them)
        if (used >= 100) return 0 + natural;
        if (used >= UsageAlertEvaluator.NearLimitPercent) return 1000 + natural;

        // 2. Running ahead of schedule while there is still a good amount left
        var elapsed = ResetTimeFormatter.ElapsedPercent(window.ResetAt, window.WindowMinutes, nowUtc);
        var pace = PaceCalculator.Project(used, window.WindowMinutes, window.ResetAt, nowUtc);
        if (pace.Kind == PaceKind.WillRunOut && elapsed is { } e && used - e >= AheadOfPacePoints)
        {
            return 3000 + Math.Min(100, used - e) + natural;
        }

        // 3. Quiet windows with resources remaining
        return 2000 + natural;
    }
}
