namespace AgentsWinBar.Helpers;

/// <summary>
/// Formats a usage-window reset time as a short "Resets in ..." string.
/// </summary>
public static class ResetTimeFormatter
{
    /// <summary>
    /// Returns how much of a usage window has elapsed (0-100): 0 right after a reset,
    /// 100 when the next reset is due. Null when the window length or reset time is unknown.
    /// </summary>
    public static double? ElapsedPercent(DateTime? resetAt, int windowMinutes, DateTime nowUtc)
    {
        if (resetAt is not { } value || windowMinutes <= 0)
        {
            return null;
        }

        var resetUtc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

        var remainingMinutes = (resetUtc - nowUtc).TotalMinutes;
        var elapsed = (windowMinutes - remainingMinutes) / windowMinutes * 100;
        return Math.Clamp(elapsed, 0, 100);
    }

    /// <summary>
    /// Returns e.g. "Resets in 12m", "Resets in 4h 59m" or "Resets in 2d 3h".
    /// Returns null when the reset time is unknown.
    /// </summary>
    public static string? Format(DateTime? resetAt, DateTime nowUtc)
    {
        if (resetAt is not { } value)
        {
            return null;
        }

        // Values without a kind come from JSON without an offset; treat them as UTC.
        var resetUtc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

        var remaining = resetUtc - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return "Resetting now";
        }

        var totalMinutes = (long)remaining.TotalMinutes;
        if (totalMinutes < 1)
        {
            return "Resets in <1m";
        }

        if (totalMinutes < 60)
        {
            return $"Resets in {totalMinutes}m";
        }

        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes % (24 * 60) / 60;
        var minutes = totalMinutes % 60;

        return days > 0
            ? $"Resets in {days}d {hours}h"
            : $"Resets in {hours}h {minutes}m";
    }
}
