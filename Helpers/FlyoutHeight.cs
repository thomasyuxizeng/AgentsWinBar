namespace AgentsWinBar.Helpers;

/// <summary>How tall the tray flyout window is, in physical pixels (the unit AppWindow sizes and moves in).</summary>
public static class FlyoutHeight
{
    /// <summary>
    /// The window height that shows <paramref name="contentDip"/> of content (DIPs at <paramref name="scale"/>, plus the
    /// window's own <paramref name="chromePx"/> border) without scrolling: never below <paramref name="minPx"/> (the
    /// flyout's original fixed height), and never above <paramref name="maxPx"/> (what the work area leaves), which wins
    /// on a screen too short for even the minimum. An unusable scale or content keeps the minimum.
    /// </summary>
    public static int Fit(double contentDip, double scale, int chromePx, int minPx, int maxPx)
    {
        var max = Math.Max(1, maxPx);
        var min = Math.Clamp(minPx, 1, max);
        if (!(scale > 0) || !double.IsFinite(scale) || !(contentDip > 0) || !double.IsFinite(contentDip)) return min;
        // One pixel of slack so rounding inside the layout never brings back a scroll bar
        var wanted = Math.Ceiling(contentDip * scale) + Math.Max(0, chromePx) + 1;
        return (int)Math.Clamp(wanted, min, max);
    }

    /// <summary>The tallest the flyout can be: the work area less the gap kept at the bottom and a top gap of <paramref name="topGapDip"/>.</summary>
    public static int Max(int workAreaHeightPx, int bottomMarginPx, double topGapDip, double scale) =>
        Math.Max(1, workAreaHeightPx - Math.Max(0, bottomMarginPx) - (int)Math.Ceiling(Math.Max(0, topGapDip) * (scale > 0 ? scale : 1)));
}
