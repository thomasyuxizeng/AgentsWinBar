using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

/// <summary>Which end of the taskbar the strip sits at.</summary>
public enum TaskbarStripSide
{
    /// <summary>Flush with the taskbar's left end (the strip's own overhang is added by the window).</summary>
    Left = 0,

    /// <summary>Just left of the system tray.</summary>
    Right = 1
}

/// <summary>The side the strip goes to and the screen X of its left edge, in physical pixels.</summary>
public readonly record struct TaskbarStripPlacement(TaskbarStripSide Side, int X);

/// <summary>
/// Decides where the taskbar strip goes on a taskbar docked at the bottom of the screen. Pure: every input is a plain
/// value read by the caller (all rectangles in physical screen pixels), so the rules can be tested without a taskbar.
/// </summary>
public static class TaskbarPlacement
{
    /// <summary>Space between the strip's right end and the system tray, in DIP.</summary>
    public const double TrayGapDip = 4;

    /// <summary>First Windows 11 build. Before it (Windows 10) Start is always at the taskbar's left end.</summary>
    public const int FirstWindows11Build = 22000;

    /// <summary>The Start button counts as "at the left end" when its left edge is within this many of its own widths of the taskbar's left edge.</summary>
    public const int StartNearLeftWidths = 2;

    /// <summary>The gap between the strip and the tray in physical pixels at the given display scale.</summary>
    public static int TrayGap(double scale) =>
        (int)Math.Ceiling(TrayGapDip * (double.IsFinite(scale) && scale > 0 ? scale : 1.0));

    /// <summary>
    /// Picks the side and X position.
    /// <list type="bullet">
    /// <item>"left": the taskbar's left edge.</item>
    /// <item>"right": the tray's left edge minus the strip width and a small gap; the left end instead when there is
    /// no tray, or when the strip would then overlap the Start button or start left of the taskbar.</item>
    /// <item>"auto" (and any other value): "right" when the Start button is at the taskbar's left end (with no Start
    /// rectangle: Windows 10, or Windows 11 with left alignment, TaskbarAl = 0), otherwise "left".</item>
    /// </list>
    /// </summary>
    /// <param name="taskbar">The taskbar's rectangle.</param>
    /// <param name="start">The Start button's rectangle, or null when it could not be read (an empty one counts as null).</param>
    /// <param name="tray">The system tray's rectangle (clock included), or null (an empty one counts as null).</param>
    /// <param name="taskbarAlignment">The TaskbarAl registry value (0 = left, 1 = center), or null when it is not set.</param>
    /// <param name="windowsBuild">The Windows build number.</param>
    /// <param name="stripWidth">The strip's width for the right end (no overhang), in physical pixels.</param>
    /// <param name="scale">The display scale (1.0 = 100%).</param>
    /// <param name="setting">The TaskbarDisplayPosition setting; normalized here.</param>
    public static TaskbarStripPlacement Decide(
        ScreenRect taskbar,
        ScreenRect? start,
        ScreenRect? tray,
        int? taskbarAlignment,
        int windowsBuild,
        int stripWidth,
        double scale,
        string? setting)
    {
        var startRect = start is { IsEmpty: false } s ? s : (ScreenRect?)null;
        var trayRect = tray is { IsEmpty: false } t ? t : (ScreenRect?)null;
        var left = new TaskbarStripPlacement(TaskbarStripSide.Left, taskbar.Left);

        var position = TaskbarDisplayPositions.Normalize(setting);
        var wantsRight = position switch
        {
            TaskbarDisplayPositions.Left => false,
            TaskbarDisplayPositions.Right => true,
            _ => IsStartAtLeftEnd(taskbar, startRect, taskbarAlignment, windowsBuild)
        };
        if (!wantsRight || trayRect is not { } trayBounds) return left;

        var width = Math.Max(0, stripWidth);
        var x = trayBounds.Left - width - TrayGap(scale);
        if (x < taskbar.Left) return left;
        if (startRect is { } startBounds && x < startBounds.Right && startBounds.Left < x + width) return left;
        return new TaskbarStripPlacement(TaskbarStripSide.Right, x);
    }

    /// <summary>
    /// True when the Start button sits at the taskbar's left end. The Start rectangle decides when there is one;
    /// without it, Windows 10 (always left) or a Windows 11 taskbar set to left alignment (TaskbarAl = 0).
    /// </summary>
    public static bool IsStartAtLeftEnd(ScreenRect taskbar, ScreenRect? start, int? taskbarAlignment, int windowsBuild)
    {
        if (start is { IsEmpty: false } startBounds)
        {
            var startWidth = startBounds.Right - startBounds.Left;
            return startBounds.Left - taskbar.Left <= StartNearLeftWidths * startWidth;
        }
        return windowsBuild < FirstWindows11Build || taskbarAlignment == 0;
    }
}
