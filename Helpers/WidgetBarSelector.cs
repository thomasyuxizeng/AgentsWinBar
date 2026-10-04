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
/// weekly windows; a different window takes a slot when it is more relevant: at or near its limit, running
/// ahead of schedule, or the one that has been changing recently.
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

        // Show them in their natural order so positions stay stable
        return chosen.OrderBy(c => c.Order).Select(c => c.Bar).ToList();
    }

    /// <summary>
    /// Higher is more relevant. The base score keeps session, then weekly, ahead when nothing is going on.
    /// </summary>
    internal static double Score(UsageWindow window, int naturalOrder, bool isExtra, DateTime nowUtc)
    {
        var score = 100.0 - naturalOrder * 10;
        var used = window.Percent;

        if (used >= 100)
        {
            score += 1000;     // at the limit
        }
        else if (used >= UsageAlertEvaluator.NearLimitPercent)
        {
            score += 600;      // nearly out
        }
        else
        {
            var elapsed = ResetTimeFormatter.ElapsedPercent(window.ResetAt, window.WindowMinutes, nowUtc);
            var pace = PaceCalculator.Project(used, window.WindowMinutes, window.ResetAt, nowUtc);
            if (pace.Kind == PaceKind.WillRunOut && elapsed is { } e && used - e >= AheadOfPacePoints)
            {
                score += 300 + Math.Min(100, used - e);   // running ahead of schedule
            }
        }

        if (window.LastChangedAt is { } changedAt)
        {
            var age = nowUtc - changedAt;
            if (age >= TimeSpan.Zero && age < RecentWindow)
            {
                var freshness = 1 - age.TotalMinutes / RecentWindow.TotalMinutes;   // 1 = just now, 0 = 30 min ago
                score += 150 * freshness;
                if (isExtra)
                {
                    score += 40;   // a specific window that moved is more informative than the general ones
                }
            }
        }

        return score;
    }
}
