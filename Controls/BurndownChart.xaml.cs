using AgentsWinBar.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace AgentsWinBar.Controls;

public sealed partial class BurndownChart : UserControl
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series),
        typeof(BurndownSeries), typeof(BurndownChart), new PropertyMetadata(null, OnDataChanged));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent),
        typeof(WindowAccent), typeof(BurndownChart), new PropertyMetadata(WindowAccent.None, OnDataChanged));
    /// <summary>Plot only (no title, axis labels or legend) with a shorter plot, for dense layouts such as the dashboard.</summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact),
        typeof(bool), typeof(BurndownChart), new PropertyMetadata(false, OnCompactChanged));
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public BurndownSeries? Series { get => (BurndownSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    private double? _plotHeight;

    /// <summary>Overrides the plot height (the default is 72 compact, 100 full) for layouts with room for a taller chart.</summary>
    public double PlotHeight
    {
        get => Plot.Height;
        set { _plotHeight = value; Plot.Height = value; RequestDraw(); }
    }
    public WindowAccent Accent { get => (WindowAccent)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    private bool _disposed;
    private bool _drawQueued;
    private BurndownSeries? _drawnSeries;
    private (double Width, double Height, WindowAccent Accent, bool Compact, ElementTheme Theme)? _drawnSignature;

    public BurndownChart()
    {
        InitializeComponent();
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>Detaches event handlers, clears plotted shapes, and releases series references on window close.</summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        ActualThemeChanged -= OnActualThemeChanged;
        Plot.SizeChanged -= OnPlotSizeChanged;
        Plot.Children.Clear();
        Plot.Clip = null;
        UsageKey.Stroke = GuideKey.Stroke = ProjectionKey.Stroke = null;
        _drawnSeries = null;
        _drawnSignature = null;
        SetValue(SeriesProperty, null);
        DataContext = null;
    }

    private static void OnCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (BurndownChart)d;
        if (chart._disposed) return;
        var compact = chart.IsCompact;
        chart.TitleText.Visibility = chart.AxisLabels.Visibility = chart.LegendPanel.Visibility =
            compact ? Visibility.Collapsed : Visibility.Visible;
        chart.CompactBackdrop.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        chart.Plot.Height = chart._plotHeight ?? (compact ? 72 : 100);
        chart.RequestDraw();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (BurndownChart)d;
        if (!chart._disposed) chart.RequestDraw();
    }

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        try { RequestDraw(); }
        catch (Exception ex) { App.Services.GetRequiredService<ILogger<BurndownChart>>().LogError(ex, "Usage history size update failed"); }
    }
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_disposed) return;
        try { RequestDraw(); }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<BurndownChart>>().LogError(ex, "Usage history theme update failed");
        }
    }

    private void RequestDraw()
    {
        if (_disposed || _drawQueued) return;
        _drawQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _drawQueued = false;
            if (_disposed) return;
            try { Draw(); }
            catch (Exception ex)
            {
                App.Services.GetRequiredService<ILogger<BurndownChart>>().LogError(ex, "Usage history draw failed");
            }
        });
    }

    private void Draw()
    {
        if (_disposed || Plot == null) return;
        var signature = (Plot.ActualWidth, Plot.Height, Accent, IsCompact, ActualTheme);
        if (_drawnSignature == signature && DashboardContent.SeriesEquals(_drawnSeries, Series)) return;
        _drawnSignature = signature;
        _drawnSeries = Series;
        Plot.Children.Clear();
        var series = Series ?? new BurndownSeries();
        var width = Plot.ActualWidth; var height = Plot.Height;
        Plot.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, width), height) };
        var percent = series.Usage.LastOrDefault()?.UsedPercent ?? 0;
        var usageBrush = UsageMeter.GetBarBrush(percent, Accent);
        // A chart line needs more contrast than the translucent time layer behind a usage bar.
        var guideBrush = Accent == WindowAccent.None
            ? new SolidColorBrush(ActualTheme == ElementTheme.Light
                ? Color.FromArgb(255, 0x65, 0x51, 0xA3) : Color.FromArgb(255, 0xB9, 0xAA, 0xFF))
            : UsageMeter.GetBarBrush(0, Accent);
        // The legend keys are drawn like the lines; the legend text itself stays neutral so it reads in any colour state
        UsageKey.Stroke = ProjectionKey.Stroke = usageBrush;
        GuideKey.Stroke = guideBrush;
        var sampleText = $"{series.SampleCount} sample{(series.SampleCount == 1 ? "" : "s")}";
        Placeholder.Text = series.IsReady ? string.Empty : $"{series.Message} · {sampleText}";
        CaptureText.Text = series.CapturedAt.HasValue
            ? $"Captured {TimestampFormatter.FormatLocal(series.CapturedAt.Value)} · {sampleText}" : "No samples in this window yet";
        if (series.ProjectedAtReset is double projected)
            CaptureText.Text += projected > 100 ? " · Observed pace would exhaust the window before reset"
                : $" · Observed pace: {projected:F0}% by reset";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, $"Usage history. {Placeholder.Text} {CaptureText.Text}");
        if (width <= 0 || !series.IsReady) return;
        // The even-use guide is only a reference: keep it faint so the observed usage line reads first.
        AddLine(series.Guide, guideBrush, width, height, dashed: false, thickness: 1.5, opacity: GuideOpacity);
        AddLine(series.Usage, usageBrush, width, height, dashed: false, thickness: 2);
        // A projection past the limit stops where it reaches 100% (on the top line) with a dot, instead of leaving the plot
        var projection = BurndownGeometry.ClipAtCeiling(series.Projection, out var hitsLimit);
        AddLine(projection, usageBrush, width, height, dashed: true, thickness: 1);
        if (hitsLimit) AddLimitDot(projection[^1], usageBrush, width, height);
    }

    /// <summary>Space kept free above 100% and below 0%, so a line at either edge (2 DIP thick) is drawn whole.</summary>
    private const double PlotInset = 4;
    private const double GuideOpacity = 0.4;
    private const double ProjectionOpacity = 0.6;
    private const double LimitDotSize = 5;

    private static Point ToPlot(BurndownPoint point, double width, double height) =>
        new(Math.Clamp(point.ElapsedFraction, 0, 1) * width, BurndownGeometry.Y(point.UsedPercent, height, PlotInset));

    private void AddLine(IReadOnlyList<BurndownPoint> points, Brush brush, double width, double height, bool dashed, double thickness, double opacity = 1)
    {
        if (points.Count < 2) return;
        var line = new Polyline { Stroke = brush, StrokeThickness = thickness, Opacity = opacity };
        foreach (var point in points) line.Points.Add(ToPlot(point, width, height));
        if (dashed) { line.StrokeDashArray = new DoubleCollection { 3, 3 }; line.Opacity = ProjectionOpacity; }
        Plot.Children.Add(line);
    }

    /// <summary>Marks where the projected pace reaches the limit.</summary>
    private void AddLimitDot(BurndownPoint point, Brush brush, double width, double height)
    {
        var at = ToPlot(point, width, height);
        var dot = new Ellipse { Width = LimitDotSize, Height = LimitDotSize, Fill = brush, Opacity = ProjectionOpacity + 0.2 };
        // Kept inside the plot horizontally so the dot is never cut in half at the reset edge
        Canvas.SetLeft(dot, Math.Clamp(at.X - LimitDotSize / 2, 0, Math.Max(0, width - LimitDotSize)));
        Canvas.SetTop(dot, at.Y - LimitDotSize / 2);
        Plot.Children.Add(dot);
    }
}
