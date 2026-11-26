namespace AgentsWinBar.Helpers;

/// <summary>Chooses the delay after refreshes with unchanged usage.</summary>
public static class AdaptiveRefreshPolicy
{
    /// <summary>Longest delay the battery saver stretch reaches (a base interval above this is kept).</summary>
    public static readonly TimeSpan BatterySaverCap = TimeSpan.FromMinutes(30);

    public static TimeSpan GetDelay(TimeSpan baseInterval, int unchangedStreak, bool enabled, bool batterySaver = false)
    {
        var delay = GetAdaptiveDelay(baseInterval, unchangedStreak, enabled);
        if (!batterySaver) return delay;

        // Windows asked apps to save power: poll half as often, up to the cap, whatever the adaptive setting says.
        var cap = baseInterval > BatterySaverCap ? baseInterval : BatterySaverCap;
        var stretched = delay.Ticks > cap.Ticks / 2 ? cap : TimeSpan.FromTicks(delay.Ticks * 2);
        return stretched > delay ? stretched : delay;
    }

    private static TimeSpan GetAdaptiveDelay(TimeSpan baseInterval, int unchangedStreak, bool enabled)
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
