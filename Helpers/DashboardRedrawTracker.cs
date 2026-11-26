namespace AgentsWinBar.Helpers;

/// <summary>
/// Decides whether the dashboard's 30-second clock tick may take the cheap path (only the relative times on the current
/// snapshot, see <see cref="DashboardClockUpdater"/>) or must take a fresh snapshot. A full redraw that was skipped (locked
/// screen, hidden window) or failed leaves one pending, and the next clock tick that runs does it, so nothing that
/// arrived meanwhile stays off screen. The lock state is polled, not signalled, so the tick itself is the catch-up point.
/// </summary>
public sealed class DashboardRedrawTracker
{
    /// <summary>True while a full redraw was skipped or failed and no full redraw has succeeded since.</summary>
    public bool RedrawPending { get; private set; }

    /// <summary>A full redraw was skipped or failed: the next clock tick must take a fresh snapshot.</summary>
    public void RedrawMissed() => RedrawPending = true;

    /// <summary>A full redraw succeeded.</summary>
    public void RedrawDone() => RedrawPending = false;

    /// <summary>Called once per clock tick that runs; true when this tick must do a full redraw.</summary>
    public bool ClockTickNeedsFullRedraw() => RedrawPending;
}
