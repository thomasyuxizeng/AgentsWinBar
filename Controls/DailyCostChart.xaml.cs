using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CodexBarWin.Controls;

public sealed partial class DailyCostChart : UserControl
{
    private IReadOnlyList<DailyCost> _series = [];
    private string _currency = "USD";

    public DailyCostChart() => InitializeComponent();

    public void SetSeries(IReadOnlyList<DailyCost> series, string currency)
    {
        _series = series;
        _currency = currency;
        Draw();
    }

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e) => Draw();

    private void Draw()
    {
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
