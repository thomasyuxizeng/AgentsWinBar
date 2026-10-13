namespace CodexBarWin.Helpers;

/// <summary>
/// Pure maths for the reset animation's "old fill" ghost when the previous usage comes from the data layer
/// rather than from the meter's own memory.
/// </summary>
internal static class BarResetGhost
{
    /// <summary>The previous used percent as the live fill shows it (0-100), or null when unknown or not a number.</summary>
    public static double? VisualPercent(double? fromPercent) =>
        fromPercent is { } value && double.IsFinite(value) ? DashboardValues.Percent(value) : null;

    /// <summary>The previous used percent for colour thresholds (negative clamps to 0), or null when unknown or not a number.</summary>
    public static double? ColourPercent(double? fromPercent) =>
        fromPercent is { } value && double.IsFinite(value) ? Math.Max(0, value) : null;

    /// <summary>Ghost width on a track of the given width, using the same mapping as the live fill; null when the percent is unknown.</summary>
    public static double? Width(double trackWidth, double? fromPercent) =>
        VisualPercent(fromPercent) is { } percent ? trackWidth * (percent / 100.0) : null;
}
