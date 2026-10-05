namespace CodexBarWin.Helpers;

/// <summary>Chooses the delay after refreshes with unchanged usage.</summary>
public static class AdaptiveRefreshPolicy
{
    public static TimeSpan GetDelay(TimeSpan baseInterval, int unchangedStreak, bool enabled)
    {
        if (!enabled || unchangedStreak < 2) return baseInterval;

        var multiplier = unchangedStreak < 4 ? 2 : 3;
        var cap = baseInterval > TimeSpan.FromMinutes(15) ? baseInterval : TimeSpan.FromMinutes(15);
        // Compare before multiplying to avoid overflow for unusually large intervals.
        return baseInterval.Ticks > cap.Ticks / multiplier
            ? cap
            : TimeSpan.FromTicks(baseInterval.Ticks * multiplier);
    }
}
