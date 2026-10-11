namespace CodexBarWin.Helpers;

/// <summary>
/// Presentation string helpers for early reset badges, details, and notifications. Pure and English-only.
/// </summary>
public static class EarlyResetCaption
{
    /// <summary>
    /// Badge/chip text displayed next to an early-reset window.
    /// </summary>
    public static string Chip() => "RESET EARLY";

    /// <summary>
    /// Formats a detail subtitle line, e.g. "Reset early 2h ago · 79% to 3%".
    /// </summary>
    public static string Detail(double fromPercent, double toPercent, DateTime atUtc, DateTime nowUtc) =>
        $"Reset early {StaleCaption.Age(atUtc, nowUtc)} · {fromPercent:F0}% to {toPercent:F0}%";

    /// <summary>
    /// Notification toast title: e.g. "Codex Session was reset early" or "Claude Current week (Sonnet) was reset early".
    /// </summary>
    public static string ToastTitle(string providerName, string windowTitle)
    {
        var provider = CapitalizeProvider(providerName);
        var title = string.IsNullOrWhiteSpace(windowTitle) ? "Window" : windowTitle.Trim();
        return $"{provider} {title} was reset early";
    }

    /// <summary>
    /// Notification toast body: e.g. "Usage dropped from 79% to 3%. The scheduled reset was in 4h 59m."
    /// </summary>
    public static string ToastBody(double fromPercent, double toPercent, DateTime? previousResetAtUtc, DateTime nowUtc)
    {
        string resetText;
        if (previousResetAtUtc is { } resetAt)
        {
            var resetUtc = DashboardValues.Utc(resetAt);
            if (resetUtc > nowUtc)
            {
                var formatted = ResetTimeFormatter.Format(resetUtc, nowUtc);
                resetText = formatted != null && formatted.StartsWith("Resets ", StringComparison.OrdinalIgnoreCase)
                    ? formatted["Resets ".Length..]
                    : formatted?.ToLowerInvariant() ?? "in the future";
            }
            else
            {
                resetText = StaleCaption.Age(resetUtc, nowUtc);
            }
        }
        else
        {
            resetText = "unknown";
        }

        return $"Usage dropped from {fromPercent:F0}% to {toPercent:F0}%. The scheduled reset was {resetText}.";
    }

    private static string CapitalizeProvider(string provider) =>
        (provider ?? string.Empty).ToLowerInvariant() switch
        {
            "claude" => "Claude",
            "codex" => "Codex",
            "gemini" => "Gemini",
            _ => string.IsNullOrEmpty(provider) ? "?" : char.ToUpperInvariant(provider[0]) + provider[1..]
        };
}
