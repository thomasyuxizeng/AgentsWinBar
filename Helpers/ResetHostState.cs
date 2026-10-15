namespace CodexBarWin.Helpers;

/// <summary>
/// What one window remembers about reset animations while its usage bars are torn down and rebuilt (the taskbar strip
/// and the overlay recreate every bar whenever their content changes). Without it each fresh bar would play the same
/// reset again, and a rebuild would cut a running animation short. Not thread-safe: UI thread only.
/// </summary>
public sealed class ResetHostState
{
    private const int MaxPlayed = 64;

    private readonly HashSet<(string Key, long Ticks)> _played = [];
    private readonly Queue<(string Key, long Ticks)> _order = new();
    private long _busyUntilMs;

    /// <summary>
    /// Records that the bar <paramref name="key"/> plays the reset detected at <paramref name="stamp"/>. False when this
    /// window already played it, so a recreated bar does not play it again.
    /// </summary>
    public bool TryMarkPlayed(string key, DateTime stamp)
    {
        var entry = (key, stamp.Ticks);
        if (!_played.Add(entry)) return false;
        _order.Enqueue(entry);
        while (_order.Count > MaxPlayed) _played.Remove(_order.Dequeue());
        return true;
    }

    /// <summary>Notes that a bar of this window starts an animation lasting <paramref name="durationMs"/>.</summary>
    public void MarkBusy(long nowMs, int durationMs) => _busyUntilMs = Math.Max(_busyUntilMs, nowMs + durationMs);

    /// <summary>How long until the animations started with <see cref="MarkBusy"/> are over; zero when none is running.</summary>
    public TimeSpan RemainingBusy(long nowMs) =>
        nowMs < _busyUntilMs ? TimeSpan.FromMilliseconds(_busyUntilMs - nowMs) : TimeSpan.Zero;
}
