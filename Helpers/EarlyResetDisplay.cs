namespace CodexBarWin.Helpers;

/// <summary>
/// Pure string helpers that turn an early-reset marker on a window into the detail line and tooltip text.
/// Everything returns an empty string when the window was not reset early.
/// </summary>
public static class EarlyResetDisplay
{
    /// <summary>"Reset early 2h ago · 79% to 3%", or empty when there is no active early reset.</summary>
    public static string Detail(DateTime? atUtc, double? fromPercent, double currentPercent, DateTime nowUtc)
    {
        if (atUtc is not { } at || fromPercent is not { } from) return string.Empty;
        var to = double.IsFinite(currentPercent) ? Math.Max(0, currentPercent) : 0;
        var utcAt = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc);
        return EarlyResetCaption.Detail(double.IsFinite(from) ? from : 0, to, utcAt, nowUtc);
    }

    /// <summary>Appends the early-reset detail to an existing tooltip on its own line.</summary>
    public static string AppendToTip(string? tip, string? detail)
    {
        var text = tip ?? string.Empty;
        return string.IsNullOrWhiteSpace(detail) ? text : text.Length == 0 ? detail! : text + "\n" + detail;
    }

    /// <summary>The chip text when an early reset is active, otherwise empty.</summary>
    public static string Chip(DateTime? atUtc) => atUtc.HasValue ? EarlyResetCaption.Chip() : string.Empty;
}
