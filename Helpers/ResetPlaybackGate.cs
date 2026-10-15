namespace CodexBarWin.Helpers;

/// <summary>
/// Decides which reset stamp a bar of the taskbar strip or the overlay may show. A reset the user could not see (the
/// window was hidden for a fullscreen app, the PC was locked or the display was off) is held back; once the window is
/// on screen again it is released with the time of the return as its stamp, so the bar plays the animation once, as it
/// asserts itself, instead of playing unseen. A reset first seen while the window is on screen passes through unchanged
/// (the bar's own age rule decides, see <see cref="BarResetAnimationPolicy"/>). Not thread-safe: UI thread only.
/// </summary>
public sealed class ResetPlaybackGate
{
    private const int MaxEntries = 64;

    private enum Fate { Live, Held, Released }

    private readonly Dictionary<(string Key, long Ticks), (Fate Fate, long ReleasedTicks)> _seen = [];
    private readonly Queue<(string Key, long Ticks)> _order = new();
    private bool _onScreen = true;

    /// <summary>Whether the owner's bars are visible to the user right now (true until told otherwise).</summary>
    public bool OnScreen => _onScreen;

    /// <summary>
    /// Tells the gate whether the window is on screen. True when this brought the window back and released held
    /// resets: the owner must then rebuild its bars so that they pick the stamps up.
    /// </summary>
    public bool SetOnScreen(bool onScreen, DateTime nowUtc)
    {
        if (onScreen == _onScreen) return false;
        _onScreen = onScreen;
        if (!onScreen) return false;

        var released = false;
        foreach (var id in _seen.Where(e => e.Value.Fate == Fate.Held).Select(e => e.Key).ToList())
        {
            _seen[id] = (Fate.Released, nowUtc.Ticks);
            released = true;
        }
        return released;
    }

    /// <summary>
    /// The stamp the bar <paramref name="barKey"/> may show for the reset detected at <paramref name="detectedAtUtc"/>:
    /// the detection time itself, null while the reset is held back, or the time it was released. Stable for a given
    /// bar and reset, so rebuilding the bars never changes it.
    /// </summary>
    public DateTime? Resolve(string barKey, DateTime? detectedAtUtc)
    {
        if (detectedAtUtc is not { } stamp) return null;
        var id = (barKey, stamp.Ticks);
        if (!_seen.TryGetValue(id, out var entry))
        {
            entry = (_onScreen ? Fate.Live : Fate.Held, 0);
            _seen[id] = entry;
            _order.Enqueue(id);
            while (_order.Count > MaxEntries) _seen.Remove(_order.Dequeue());
        }

        return entry.Fate switch
        {
            Fate.Live => stamp,
            Fate.Held => null,
            _ => new DateTime(entry.ReleasedTicks, DateTimeKind.Utc)
        };
    }
}
