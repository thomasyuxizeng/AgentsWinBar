using CodexBarWin.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace CodexBarWin.Controls;

public sealed partial class BurndownChart : UserControl
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series),
        typeof(BurndownSeries), typeof(BurndownChart), new PropertyMetadata(null, OnDataChanged));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent),
        typeof(WindowAccent), typeof(BurndownChart), new PropertyMetadata(WindowAccent.None, OnDataChanged));
    public BurndownSeries? Series { get => (BurndownSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public WindowAccent Accent { get => (WindowAccent)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public BurndownChart() => InitializeComponent();

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((BurndownChart)d).Draw();
    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e) => Draw();

    private void Draw()
    {
        if (Plot == null) return;
        Plot.Children.Clear();
        var series = Series ?? new BurndownSeries();
        var width = Plot.ActualWidth; var height = Plot.Height;
        Plot.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, width), height) };
        var percent = series.Usage.LastOrDefault()?.UsedPercent ?? 0;
        var usageBrush = UsageMeter.GetBarBrush(percent, Accent);
        var guideBrush = UsageMeter.GetTimeBrush(Accent);
        UsageLegend.Foreground = usageBrush;
        GuideLegend.Foreground = guideBrush;
        Placeholder.Text = series.IsReady ? string.Empty : $"{series.Message} · {series.SampleCount} sample{(series.SampleCount == 1 ? "" : "s")}";
        CaptureText.Text = series.CapturedAt.HasValue
            ? $"Captured {series.CapturedAt.Value.ToLocalTime():g} · {series.SampleCount} samples" : "No samples in this window yet";
        if (series.ProjectedAtReset is double projected)
            CaptureText.Text += projected > 100 ? " · Observed pace would exhaust the window before reset"
                : $" · Observed pace: {projected:F0}% by reset";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, $"Usage history. {Placeholder.Text} {CaptureText.Text}");
        if (width <= 0) return;
        AddLine(series.Guide, guideBrush, width, height, dashed: false, thickness: 1);
        AddLine(series.Usage, usageBrush, width, height, dashed: false, thickness: 2);
        AddLine(series.Projection, usageBrush, width, height, dashed: true, thickness: 1);
    }

    private void AddLine(IReadOnlyList<BurndownPoint> points, Brush brush, double width, double height, bool dashed, double thickness)
    {
        if (points.Count < 2) return;
        var line = new Polyline { Stroke = brush, StrokeThickness = thickness };
        // Leave projections above 100% outside the canvas so clipping keeps their slope accurate.
        foreach (var point in points) line.Points.Add(new Point(point.ElapsedFraction * width, height * (1 - point.UsedPercent / 100)));
        if (dashed) { line.StrokeDashArray = new DoubleCollection { 3, 3 }; line.Opacity = 0.6; }
        Plot.Children.Add(line);
    }
}
