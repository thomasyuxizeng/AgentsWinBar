namespace AgentsWinBar.Helpers;

/// <summary>
/// Column arithmetic for laying a short list out in equal-width columns, filled row by row (see
/// <c>Controls.ColumnsLayout</c>). Every input a layout pass can produce (zero, negative, NaN or infinite sizes, no items)
/// gives a usable answer: at least one column and never a division by zero.
/// </summary>
public static class ColumnLayoutMath
{
    /// <summary>
    /// How many columns at least <paramref name="minItemWidth"/> wide, with <paramref name="spacing"/> between them, fit
    /// in <paramref name="width"/>: from 1 (also for an unusable width) up to <paramref name="itemCount"/>.
    /// </summary>
    public static int Columns(double width, double minItemWidth, double spacing, int itemCount)
    {
        if (itemCount <= 1 || !double.IsFinite(width) || width <= 0) return 1;
        var gap = Usable(spacing);
        // A column needs at least 1 DIP, so an unusable minimum width cannot produce unlimited columns
        var step = Math.Max(1, Usable(minItemWidth) + gap);
        var fit = Math.Floor((width + gap) / step);
        return fit >= itemCount ? itemCount : Math.Max(1, (int)fit);
    }

    /// <summary>The width of each of <paramref name="columns"/> equal columns sharing <paramref name="width"/>; never negative.</summary>
    public static double ColumnWidth(double width, int columns, double spacing)
    {
        if (!double.IsFinite(width) || width <= 0) return 0;
        var count = Math.Max(1, columns);
        return Math.Max(0, (width - Usable(spacing) * (count - 1)) / count);
    }

    private static double Usable(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
