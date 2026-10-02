namespace CodexBarWin.Helpers;

/// <summary>
/// What the current usage rate means for the rest of the window.
/// </summary>
public enum PaceKind
{
    /// <summary>Window length or reset time unknown, or the window has already ended.</summary>
    Unknown,

    /// <summary>Too little usage or time has passed to say anything useful.</summary>
    NotStarted,

    /// <summary>Already at 100%.</summary>
    LimitReached,

    /// <summary>At the current rate the limit is hit before the reset.</summary>
    WillRunOut,

    /// <summary>At the current rate the window ends before the limit is reached.</summary>
    OnTrack
}

/// <param name="Kind">The projection outcome.</param>
/// <param name="MinutesToLimit">Minutes from now until 100% at the current rate (WillRunOut only).</param>
/// <param name="MinutesBeforeReset">How long before the reset the limit would be hit (WillRunOut only).</param>
/// <param name="ProjectedUsedAtReset">Projected usage when the window resets (OnTrack only).</param>
/// <param name="MinutesUntilReset">Minutes from now until the reset (0 when unknown).</param>
public sealed record PaceProjection(
    PaceKind Kind,
    double MinutesToLimit = 0,
    double MinutesBeforeReset = 0,
    double ProjectedUsedAtReset = 0,
    double MinutesUntilReset = 0);

/// <summary>
/// Projects usage forward from the rate so far in the current window, and describes it in plain English.
/// </summary>
public static class PaceCalculator
{
    private const double MinElapsedMinutes = 5;
    private const double MinElapsedFraction = 0.02;

    public static PaceProjection Project(double usedPercent, int windowMinutes, DateTime? resetAt, DateTime nowUtc)
    {
        if (resetAt is not { } value || windowMinutes <= 0)
        {
            return new PaceProjection(PaceKind.Unknown);
        }

        var resetUtc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

        var remaining = (resetUtc - nowUtc).TotalMinutes;
        if (remaining <= 0)
        {
            return new PaceProjection(PaceKind.Unknown);
        }

        if (usedPercent >= 100)
        {
            return new PaceProjection(PaceKind.LimitReached, MinutesUntilReset: remaining);
        }

        var elapsed = Math.Max(0, windowMinutes - remaining);
        if (usedPercent <= 0 || elapsed < Math.Max(MinElapsedMinutes, windowMinutes * MinElapsedFraction))
        {
            return new PaceProjection(PaceKind.NotStarted, MinutesUntilReset: remaining);
        }

        var ratePerMinute = usedPercent / elapsed;
        var minutesToLimit = (100 - usedPercent) / ratePerMinute;

        return minutesToLimit < remaining
            ? new PaceProjection(PaceKind.WillRunOut, MinutesToLimit: minutesToLimit,
                MinutesBeforeReset: remaining - minutesToLimit, MinutesUntilReset: remaining)
            : new PaceProjection(PaceKind.OnTrack, ProjectedUsedAtReset: usedPercent + ratePerMinute * remaining,
                MinutesUntilReset: remaining);
    }

    /// <summary>
    /// One sentence describing the projection, or an empty string when there is nothing to say.
    /// </summary>
    public static string Summarize(PaceProjection projection) => projection.Kind switch
    {
        PaceKind.LimitReached => $"Limit reached, resets in {FormatDuration(projection.MinutesUntilReset)}",
        PaceKind.WillRunOut =>
            $"At this pace you'll hit the limit in {FormatDuration(projection.MinutesToLimit)}, " +
            $"{FormatDuration(projection.MinutesBeforeReset)} before the reset",
        PaceKind.OnTrack => $"At this pace you'll use about {Math.Round(projection.ProjectedUsedAtReset):F0}% by the reset",
        PaceKind.NotStarted => "Not enough usage yet to project a pace",
        _ => string.Empty
    };

    /// <summary>
    /// "&lt;1m", "12m", "4h 59m" or "2d 3h".
    /// </summary>
    public static string FormatDuration(double minutes)
    {
        var total = (int)Math.Max(0, minutes);
        if (total < 1) return "<1m";
        if (total < 60) return $"{total}m";

        var days = total / (24 * 60);
        var hours = total % (24 * 60) / 60;
        var mins = total % 60;
        return days > 0 ? $"{days}d {hours}h" : $"{hours}h {mins}m";
    }
}
