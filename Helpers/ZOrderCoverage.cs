namespace CodexBarWin.Helpers;

/// <summary>A window rectangle in physical screen pixels (right and bottom exclusive, like a Win32 RECT).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public bool Intersects(ScreenRect other) =>
        !IsEmpty && !other.IsEmpty && Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;
}

/// <summary>One window above ours in the z-order. <see cref="IsCloaked"/> is only asked for when it matters.</summary>
public readonly record struct WindowAbove(bool IsVisible, ScreenRect Bounds, Func<bool> IsCloaked);

/// <summary>
/// Decides whether an always-on-top window (the taskbar strip, the overlay) has to be raised again. Raising it moves it to
/// the top of the topmost band; that only changes what is on screen when it lost its topmost style or a visible, uncloaked
/// window above it overlaps it. Otherwise the raise (and the window messages and redraw it causes) can be skipped.
/// </summary>
public static class ZOrderCoverage
{
    /// <summary>Past this many windows above ours, raise without looking further (a cheap bound on the walk).</summary>
    public const int MaxWindowsAbove = 256;

    public static bool OverlapsVisible(bool isVisible, ScreenRect aboveBounds, ScreenRect ownBounds) =>
        isVisible && aboveBounds.Intersects(ownBounds);

    public static bool NeedsRaise(bool isTopmost, ScreenRect bounds, IEnumerable<WindowAbove> windowsAbove)
    {
        if (!isTopmost || bounds.IsEmpty) return true;
        var count = 0;
        foreach (var above in windowsAbove)
        {
            if (++count > MaxWindowsAbove) return true;
            if (OverlapsVisible(above.IsVisible, above.Bounds, bounds) && !above.IsCloaked()) return true;
        }
        return false;
    }
}
