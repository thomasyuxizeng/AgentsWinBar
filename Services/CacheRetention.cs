namespace CodexBarWin.Services;

/// <summary>
/// Retention and clock-skew policies for cached last-known-good usage and cost data.
/// </summary>
public static class CacheRetention
{
    /// <summary>
    /// Maximum age of last-known-good cache data before it expires and is treated as absent (7 days).
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Maximum clock drift into the future permitted before cache data is discarded (5 minutes).
    /// </summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);
}
