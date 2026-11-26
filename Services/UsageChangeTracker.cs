using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

/// <summary>
/// Remembers when each usage window last changed, by comparing a fresh reading with the previous one.
/// </summary>
public static class UsageChangeTracker
{
    /// <summary>A move of at least this many points counts as a change (ignores rounding noise).</summary>
    public const double ChangeThresholdPoints = 0.5;

    /// <summary>
    /// Returns <paramref name="incoming"/> with <see cref="UsageWindow.LastChangedAt"/> filled in on each
    /// window: set to now when the usage moved since <paramref name="previous"/>, otherwise carried over.
    /// </summary>
    public static UsageData Annotate(UsageData incoming, UsageData? previous, DateTime nowUtc)
    {
        if (previous == null)
        {
            return incoming;
        }

        return incoming with
        {
            Session = Track(incoming.Session, previous.Session, nowUtc),
            Weekly = Track(incoming.Weekly, previous.Weekly, nowUtc),
            Tertiary = Track(incoming.Tertiary, previous.Tertiary, nowUtc),
            ExtraWindows = incoming.ExtraWindows
                .Select(extra => extra with
                {
                    Window = Track(extra.Window, previous.ExtraWindows.FirstOrDefault(p => p.Title == extra.Title)?.Window, nowUtc)!
                })
                .ToList()
        };
    }

    private static UsageWindow? Track(UsageWindow? current, UsageWindow? before, DateTime nowUtc)
    {
        if (current == null)
        {
            return null;
        }

        if (before == null)
        {
            // First time we see this window: nothing to compare with yet
            return current;
        }

        var changed = Math.Abs(current.Percent - before.Percent) >= ChangeThresholdPoints;
        return current with { LastChangedAt = changed ? nowUtc : before.LastChangedAt };
    }
}
