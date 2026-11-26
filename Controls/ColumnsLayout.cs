using AgentsWinBar.Helpers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentsWinBar.Controls;

/// <summary>
/// Lays an ItemsRepeater's items out in equal columns at least <see cref="MinItemWidth"/> wide, filled row by row; each
/// row is as tall as its tallest item. It replaces UniformGridLayout on the dashboard: resizing the dashboard window made
/// that layout's native measure throw DivideByZeroException, which crashed the app.
/// </summary>
public sealed partial class ColumnsLayout : NonVirtualizingLayout
{
    public double MinItemWidth { get; set; } = 380;
    public double ColumnSpacing { get; set; } = 16;
    public double RowSpacing { get; set; }

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var children = context.Children;
        var count = children.Count;
        if (count == 0) return new Size(0, 0);
        if (!double.IsFinite(availableSize.Width))
        {
            // Unbounded width: one column at the items' natural width
            double widest = 0, height = 0;
            for (var i = 0; i < count; i++)
            {
                children[i].Measure(availableSize);
                widest = Math.Max(widest, children[i].DesiredSize.Width);
                height += children[i].DesiredSize.Height + (i > 0 ? RowSpacing : 0);
            }
            return new Size(widest, height);
        }

        var columns = ColumnLayoutMath.Columns(availableSize.Width, MinItemWidth, ColumnSpacing, count);
        var itemWidth = ColumnLayoutMath.ColumnWidth(availableSize.Width, columns, ColumnSpacing);
        double total = 0;
        for (var start = 0; start < count; start += columns)
        {
            double rowHeight = 0;
            for (var i = start; i < Math.Min(count, start + columns); i++)
            {
                children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            }
            total += rowHeight + (start > 0 ? RowSpacing : 0);
        }
        return new Size(availableSize.Width, total);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var children = context.Children;
        var count = children.Count;
        if (count == 0) return finalSize;
        var columns = ColumnLayoutMath.Columns(finalSize.Width, MinItemWidth, ColumnSpacing, count);
        var itemWidth = ColumnLayoutMath.ColumnWidth(finalSize.Width, columns, ColumnSpacing);
        double y = 0;
        for (var start = 0; start < count; start += columns)
        {
            var end = Math.Min(count, start + columns);
            double rowHeight = 0;
            for (var i = start; i < end; i++) rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            for (var i = start; i < end; i++)
            {
                children[i].Arrange(new Rect((i - start) * (itemWidth + ColumnSpacing), y, itemWidth, rowHeight));
            }
            y += rowHeight + RowSpacing;
        }
        return finalSize;
    }
}
