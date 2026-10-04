using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CodexBarWin.Controls;

public sealed partial class DailyCostChart : UserControl
{
    private IReadOnlyList<DailyCost> _series = [];
    private string _currency = "USD";

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series),
        typeof(IReadOnlyList<DailyCost>), typeof(DailyCostChart), new PropertyMetadata(null, OnSeriesChanged));
    public static readonly DependencyProperty CurrencyProperty = DependencyProperty.Register(nameof(Currency),
        typeof(string), typeof(DailyCostChart), new PropertyMetadata("USD", OnSeriesChanged));

    /// <summary>Bindable alternative to <see cref="SetSeries"/> (used by the dashboard).</summary>
    public IReadOnlyList<DailyCost>? Series { get => (IReadOnlyList<DailyCost>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public string Currency { get => (string)GetValue(CurrencyProperty); set => SetValue(CurrencyProperty, value); }

    /// <summary>Height of the bar area in DIPs (default 90).</summary>
    public double PlotHeight { get => Plot.Height; set { Plot.Height = value; Draw(); } }

    public DailyCostChart() => InitializeComponent();

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (DailyCostChart)d;
        chart.SetSeries(chart.Series ?? [], chart.Currency);
    }

    public void SetSeries(IReadOnlyList<DailyCost> series, string currency)
    {
        _series = series;
        _currency = currency;
        Draw();
    }

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try { Draw(); }
        catch (Exception ex) { App.Services.GetRequiredService<ILogger<DailyCostChart>>().LogError(ex, "Daily cost chart size update failed"); }
    }

    private IReadOnlyList<DailyCost>? _drawnSeries;
    private string? _drawnCurrency;
    private double _drawnWidth = -1;
    private double _drawnHeight = -1;

    private void Draw()
    {
        if (_drawnSeries != null && _drawnCurrency == _currency && _drawnWidth == Plot.ActualWidth &&
            _drawnHeight == Plot.Height && DashboardContent.SequenceEquals(_drawnSeries, _series, DashboardContent.DailyEquals)) return;
        _drawnSeries = _series;
        _drawnCurrency = _currency;
        _drawnWidth = Plot.ActualWidth;
        _drawnHeight = Plot.Height;
        Plot.Children.Clear();
        if (_series.Count == 0 || Plot.ActualWidth <= 0) return;
        var max = Math.Max(0.01m, _series.Max(d => d.TotalCost));
        var slot = Plot.ActualWidth / _series.Count;
        var baseline = new Rectangle { Width = Plot.ActualWidth, Height = 1,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 128, 128, 128)) };
        Canvas.SetTop(baseline, Plot.Height - 1);
        Plot.Children.Add(baseline);
        for (var i = 0; i < _series.Count; i++)
        {
            var day = _series[i];
            var height = Math.Max(2, (double)(day.TotalCost / max) * (Plot.Height - 8));
            var bar = new Rectangle { Width = Math.Max(1, slot - 3), Height = height, RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212)) };
            Canvas.SetLeft(bar, i * slot + 1);
            Canvas.SetTop(bar, Plot.Height - height);
            ToolTipService.SetToolTip(bar, $"{day.Date:MMM d}: {CostFormatter.Money(day.TotalCost, _currency)} estimated · {CostFormatter.Tokens(day.TotalTokens)} tokens");
            Plot.Children.Add(bar);
        }
        StartLabel.Text = _series[0].Date.ToString("MMM d");
        EndLabel.Text = _series[^1].Date.ToString("MMM d");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this,
            $"Daily estimated cost, {_series[0].Date:MMM d} to {_series[^1].Date:MMM d}. Hover bars for cost and tokens.");
    }
}
