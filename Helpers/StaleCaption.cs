namespace CodexBarWin.Helpers;

/// <summary>Quiet caption for last-known-good data: empty when fresh, "Stale · last updated 12m ago" otherwise.</summary>
public static class StaleCaption
{
    /// <summary>"just now", "12m ago", "3h ago", "2d ago".</summary>
    public static string Age(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - DashboardValues.Utc(utc);
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalDays}d ago";
    }

    /// <summary>Empty when <paramref name="isStale"/> is false, so fresh data shows no extra text.</summary>
    public static string Text(bool isStale, DateTime? lastSuccessUtc, DateTime nowUtc) =>
        !isStale ? string.Empty
        : lastSuccessUtc is { } at ? $"Stale · last updated {Age(at, nowUtc)}"
        : "Stale · last update unknown";
}
