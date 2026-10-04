using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>
/// A notification the user should see.
/// </summary>
public sealed record UsageAlert(string Key, string Title, string Message);

/// <summary>
/// Decides when usage is worth a heads-up, and makes sure each situation is reported only once per
/// usage window. Pure logic (no UI), so it can be unit tested.
/// </summary>
public class UsageAlertEvaluator
{
    /// <summary>Usage at or above this is "nearly out".</summary>
    public const double NearLimitPercent = 90;

    /// <summary>How many points usage must be ahead of the time elapsed to count as "far ahead of pace".</summary>
    public const double AheadOfPacePoints = 25;

    /// <summary>Don't warn about running out when the reset is this close anyway.</summary>
    public const double MinMinutesBeforeReset = 30;

    private readonly HashSet<string> _alreadyAlerted = [];

    /// <summary>
    /// Returns the alerts that are new since the last call.
    /// </summary>
    public IReadOnlyList<UsageAlert> Evaluate(IEnumerable<UsageData> providers, DateTime nowUtc)
    {
        var alerts = new List<UsageAlert>();

        foreach (var data in providers)
        {
            if (data.HasError || data.IsLoading)
            {
                continue;
            }

            var name = Capitalize(data.Provider);
            Check(alerts, data, $"{name} {data.SessionLabel.ToLowerInvariant()}", "session", data.Session, nowUtc);
            Check(alerts, data, $"{name} {data.WeeklyLabel.ToLowerInvariant()}", "weekly", data.Weekly, nowUtc);

            // Extra windows (Claude and GPT quota, Fable only, ...) get the same alerts
            foreach (var extra in data.ExtraWindows)
            {
                Check(alerts, data, $"{name} · {extra.Title}", $"extra:{extra.Title}", extra.Window, nowUtc);
            }
        }

        return alerts;
    }

    /// <param name="subject">What the alert is about, e.g. "Claude session" or "Gemini · Claude and GPT 5-hour".</param>
    private void Check(List<UsageAlert> alerts, UsageData data, string subject, string slot, UsageWindow? window, DateTime nowUtc)
    {
        if (window == null)
        {
            return;
        }

        var pace = PaceCalculator.Project(window.Percent, window.WindowMinutes, window.ResetAt, nowUtc);
        var elapsedPercent = ResetTimeFormatter.ElapsedPercent(window.ResetAt, window.WindowMinutes, nowUtc);
        var title = $"{subject} usage";

        // Highest-priority situation only, so one window never produces several toasts at once
        string? level = null;
        string? message = null;

        if (window.Percent >= 100)
        {
            level = "limit";
            title = $"{subject} limit reached";
            message = pace.Kind == PaceKind.LimitReached ? PaceCalculator.Summarize(pace) : "You've used all of it.";
        }
        else if (window.Percent >= NearLimitPercent)
        {
            level = "near";
            message = $"{window.Percent:F0}% used" +
                      (pace.MinutesUntilReset > 0 ? $", resets in {PaceCalculator.FormatDuration(pace.MinutesUntilReset)}" : string.Empty);
        }
        else if (pace.Kind == PaceKind.WillRunOut
                 && pace.MinutesBeforeReset >= MinMinutesBeforeReset
                 && elapsedPercent is { } elapsed
                 && window.Percent - elapsed >= AheadOfPacePoints)
        {
            level = "pace";
            title = $"{subject}: using it fast";
            message = $"{window.Percent:F0}% used with {elapsed:F0}% of the time gone. {PaceCalculator.Summarize(pace)}.";
        }

        if (level == null || message == null)
        {
            return;
        }

        // The same situation in the same window is reported once; a new window (new reset time) starts fresh
        var key = $"{data.Provider}|{slot}|{WindowKey(window.ResetAt)}|{level}";
        if (_alreadyAlerted.Add(key))
        {
            alerts.Add(new UsageAlert(key, title, message));
        }
    }

    /// <summary>
    /// Buckets the reset time to 30 minutes so small jitter between fetches doesn't look like a new window.
    /// </summary>
    private static long WindowKey(DateTime? resetAt) =>
        resetAt is { } value ? value.ToUniversalTime().Ticks / TimeSpan.FromMinutes(30).Ticks : 0;

    private static string Capitalize(string text) =>
        text.Length == 0 ? "?" : char.ToUpperInvariant(text[0]) + text[1..];
}
