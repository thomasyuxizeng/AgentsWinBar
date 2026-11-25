namespace CodexBarWin.Services.Native;

/// <summary>
/// Shared "last good reading" behavior of the native clients: when a read fails, the client shows its previous
/// successful reading (marked cached) instead of an empty failure, and a window that has reset since that reading is
/// reported as unused rather than with its old percentage.
/// </summary>
public static class NativeLastGood
{
    /// <summary>
    /// Failures that keep the last good reading: an expired sign-in (the provider's own app renews it) and transient
    /// failures (network error, timeout, 5xx, 429). A missing or rejected sign-in does not.
    /// </summary>
    public static bool ServesLastGood(NativeUsageStatus status) =>
        status is NativeUsageStatus.SignInExpired or NativeUsageStatus.Failed or NativeUsageStatus.RateLimited;

    /// <summary>True when any window of <paramref name="result"/> has a <c>ResetsAt</c> at or before <paramref name="now"/>.</summary>
    public static bool HasPastReset(NativeUsageResult result, DateTimeOffset now) =>
        new[] { result.Primary, result.Secondary, result.Tertiary }.Concat(result.ExtraWindows.Select(e => e.Window))
            .Any(w => w?.ResetsAt is { } r && r <= now);

    /// <summary>
    /// Reports every window whose reset time has passed as 0% used with no reset time (and no stale reset text),
    /// because the numbers it carried belong to the window that has just ended. Other windows are untouched.
    /// </summary>
    public static NativeUsageResult ClearPastResets(NativeUsageResult result, DateTimeOffset now)
    {
        if (!HasPastReset(result, now)) return result;
        return result with
        {
            Primary = Cleared(result.Primary, now),
            Secondary = Cleared(result.Secondary, now),
            Tertiary = Cleared(result.Tertiary, now),
            ExtraWindows = result.ExtraWindows.Select(e => e with { Window = Cleared(e.Window, now)! }).ToList()
        };
    }

    /// <summary>
    /// When a last good reading exists, show it (marked cached, past-reset windows cleared) and keep the failure's
    /// status and message so the app can still tell what went wrong; otherwise return the failure alone.
    /// </summary>
    public static NativeUsageResult FromCacheOr(NativeUsageResult? lastGood, NativeUsageResult failure, DateTimeOffset now)
    {
        if (lastGood == null) return failure;
        return ClearPastResets(lastGood, now) with
        {
            Status = failure.Status,
            Message = failure.Message,
            HttpStatus = failure.HttpStatus,
            ExpiredFor = failure.ExpiredFor,
            RetryAfter = failure.RetryAfter,
            NetworkCalls = failure.NetworkCalls,
            Cached = true,
            CacheUpdatedAt = lastGood.UpdatedAt
        };
    }

    private static NativeUsageWindow? Cleared(NativeUsageWindow? window, DateTimeOffset now) =>
        window?.ResetsAt is { } reset && reset <= now ? window with { UsedPercent = 0, ResetsAt = null, ResetDescription = null } : window;
}

/// <summary>Remembers a client's last successful reading in memory and serves it for the failures that allow it.</summary>
public sealed class NativeLastGoodStore
{
    private readonly Lock _gate = new();
    private NativeUsageResult? _lastGood;

    /// <summary>
    /// Records <paramref name="result"/> when it is a fresh reading; for an expired sign-in or a transient failure
    /// returns the last good reading marked cached; any other result is returned unchanged.
    /// </summary>
    public NativeUsageResult Apply(NativeUsageResult result, DateTimeOffset now)
    {
        if (result is { Status: NativeUsageStatus.Ok, HasUsage: true, Cached: false })
        {
            lock (_gate) _lastGood = result;
            return result;
        }
        if (!NativeLastGood.ServesLastGood(result.Status))
        {
            // A missing or rejected sign-in also forgets the reading it produced, so a later failure cannot bring it back.
            if (result.Status is NativeUsageStatus.SignInMissing or NativeUsageStatus.Unauthorized)
                lock (_gate) _lastGood = null;
            return result;
        }
        NativeUsageResult? lastGood;
        lock (_gate) lastGood = _lastGood;
        return NativeLastGood.FromCacheOr(lastGood, result, now);
    }
}
