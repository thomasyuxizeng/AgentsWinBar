namespace AgentsWinBar.Helpers;

/// <summary>
/// Plot geometry for the usage-history chart (no WinUI types, so it can be tested): where a percentage sits in the plot,
/// and how much of a projection to draw.
/// </summary>
public static class BurndownGeometry
{
    /// <summary>The top of the plot: a window's limit.</summary>
    public const double Ceiling = 100;

    /// <summary>
    /// The y of <paramref name="percent"/> in a plot <paramref name="height"/> tall that keeps <paramref name="inset"/>
    /// free at the top and bottom, so a line at 0% or 100% is drawn whole instead of half outside the plot.
    /// Values outside 0-100% are drawn on the nearest edge line.
    /// </summary>
    public static double Y(double percent, double height, double inset)
    {
        if (double.IsNaN(percent)) percent = 0;
        inset = Math.Clamp(inset, 0, Math.Max(0, height) / 2);
        var usable = Math.Max(0, height - 2 * inset);
        return inset + usable * (1 - Math.Clamp(percent, 0, Ceiling) / Ceiling);
    }

    /// <summary>
    /// The part of a projection up to where it first reaches <see cref="Ceiling"/>, so the dashed line ends on the plot's
    /// top line (where the limit would be hit) instead of running off the chart. The last point is that crossing when
    /// <paramref name="crossed"/> is true. A projection that stays flat at the limit is kept (it runs along the top line);
    /// one that starts above the limit, or at it and still rising, has nothing left to show and comes back empty, as does
    /// one with fewer than two points.
    /// </summary>
    public static IReadOnlyList<BurndownPoint> ClipAtCeiling(IReadOnlyList<BurndownPoint> points, out bool crossed)
    {
        crossed = false;
        if (points.Count < 2 || !(points[0].UsedPercent <= Ceiling)) return [];
        var kept = new List<BurndownPoint>(points.Count) { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            var point = points[i];
            if (point.UsedPercent <= Ceiling)
            {
                kept.Add(point);
                continue;
            }
            var previous = kept[^1];
            if (previous.UsedPercent < Ceiling)
            {
                // Linear interpolation to the crossing, so the slope stays the observed pace
                var t = (Ceiling - previous.UsedPercent) / (point.UsedPercent - previous.UsedPercent);
                kept.Add(new BurndownPoint(previous.ElapsedFraction + t * (point.ElapsedFraction - previous.ElapsedFraction), Ceiling));
                crossed = true;
            }
            break;
        }
        return kept.Count < 2 ? [] : kept;
    }
}
