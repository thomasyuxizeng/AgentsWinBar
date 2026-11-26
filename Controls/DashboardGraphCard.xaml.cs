using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentsWinBar.Controls;

/// <summary>One provider's charts on the dashboard's graphs section: usage history per window, then the daily cost. Binds to a <see cref="DashboardProvider"/>.</summary>
public sealed partial class DashboardGraphCard : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data),
        typeof(DashboardProvider), typeof(DashboardGraphCard), new PropertyMetadata(null, OnDataChanged));

    private sealed class WindowChartSlot
    {
        public required StackPanel Root { get; init; }
        public required TextBlock TitleText { get; init; }
        public required TextBlock UsedText { get; init; }
        public required BurndownChart Chart { get; init; }
        public required TextBlock MissingText { get; init; }
        public DashboardWindow? LastWindow { get; set; }
    }

    private readonly Dictionary<string, WindowChartSlot> _slots = new(StringComparer.Ordinal);
    private string? _iconProviderId;
    private string _windowsOrder = string.Empty;
    private bool _disposed;

    public DashboardProvider? Data
    {
        get => (DashboardProvider?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool HasCostChart => Data?.Cost is { Daily.Count: > 0 };

    /// <summary>True when the provider has neither a window to chart nor daily cost.</summary>
    public bool IsEmpty => Data is { Windows.Count: 0 } && !HasCostChart;

    public DashboardGraphCard()
    {
        InitializeComponent();
    }

    /// <summary>Detaches child charts, slots, and property references when removed or closed.</summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots.Values) slot.Chart.Cleanup();
        _slots.Clear();
        WindowsChartsHost.Children.Clear();
        CostChart.Cleanup();
        HeaderIcon.Data = null;
        _iconProviderId = null;
        _windowsOrder = string.Empty;
        SetValue(DataProperty, null);
        DataContext = null;
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (DashboardGraphCard)d;
        if (card._disposed) return;
        card.Apply(card.Data);
    }

    private void Apply(DashboardProvider? data)
    {
        if (Content is UIElement content)
            content.Visibility = data == null ? Visibility.Collapsed : Visibility.Visible;
        if (data == null) return;

        if (!string.Equals(_iconProviderId, data.Id, StringComparison.Ordinal))
        {
            _iconProviderId = data.Id;
            HeaderIcon.Data = DashboardFormat.Icon(data.Id);
        }
        if (!string.Equals(HeaderTitle.Text, data.DisplayName, StringComparison.Ordinal))
            HeaderTitle.Text = data.DisplayName;

        var activeKeys = new HashSet<string>(data.Windows.Count, StringComparer.Ordinal);
        foreach (var w in data.Windows) activeKeys.Add(w.Key);

        foreach (var staleKey in _slots.Keys.Where(k => !activeKeys.Contains(k)).ToList())
        {
            if (_slots.Remove(staleKey, out var staleSlot))
            {
                WindowsChartsHost.Children.Remove(staleSlot.Root);
                staleSlot.Chart.Cleanup();
            }
        }

        foreach (var window in data.Windows)
        {
            if (!_slots.TryGetValue(window.Key, out var slot))
            {
                slot = CreateSlot();
                _slots[window.Key] = slot;
            }
            if (!DashboardContent.GraphWindowEquals(slot.LastWindow, window))
            {
                slot.LastWindow = window;
                if (!string.Equals(slot.TitleText.Text, window.Title, StringComparison.Ordinal))
                    slot.TitleText.Text = window.Title;
                var used = DashboardFormat.Used(window.UsedPercent);
                if (!string.Equals(slot.UsedText.Text, used, StringComparison.Ordinal))
                    slot.UsedText.Text = used;
                if (slot.Chart.Accent != window.Accent)
                    slot.Chart.Accent = window.Accent;
                slot.Chart.Series = window.History;
                var readyVis = DashboardFormat.VisSeriesReady(window.History);
                if (slot.Chart.Visibility != readyVis)
                    slot.Chart.Visibility = readyVis;
                var missingText = DashboardFormat.NoSeriesText(window.History);
                if (!string.Equals(slot.MissingText.Text, missingText, StringComparison.Ordinal))
                    slot.MissingText.Text = missingText;
                var missingVis = DashboardFormat.VisSeriesMissing(window.History);
                if (slot.MissingText.Visibility != missingVis)
                    slot.MissingText.Visibility = missingVis;
            }
        }

        var orderKey = string.Join('|', data.Windows.Select(w => $"{w.Key.Length}:{w.Key}"));
        if (!string.Equals(orderKey, _windowsOrder, StringComparison.Ordinal))
        {
            _windowsOrder = orderKey;
            WindowsChartsHost.Children.Clear();
            foreach (var window in data.Windows)
                WindowsChartsHost.Children.Add(_slots[window.Key].Root);
        }

        var hasCost = HasCostChart;
        var costVis = DashboardFormat.Vis(hasCost);
        if (CostChartSection.Visibility != costVis)
            CostChartSection.Visibility = costVis;
        if (hasCost)
            CostChart.SetSeries(data.Cost!.Daily, data.Cost.Currency);
        else
            CostChart.SetSeries([], "USD");

        var emptyVis = DashboardFormat.Vis(IsEmpty);
        if (EmptyMessage.Visibility != emptyVis)
            EmptyMessage.Visibility = emptyVis;
    }

    private WindowChartSlot CreateSlot()
    {
        var root = new StackPanel { Spacing = 6 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock { Style = (Style)Resources["ChartTitleStyle"] };
        var usedText = new TextBlock { Style = (Style)Resources["ChartUsedStyle"] };
        Grid.SetColumn(usedText, 1);
        header.Children.Add(titleText);
        header.Children.Add(usedText);

        var chart = new BurndownChart { IsCompact = true, PlotHeight = 88 };
        var missingText = new TextBlock { Style = (Style)Resources["ChartMissingStyle"] };

        root.Children.Add(header);
        root.Children.Add(chart);
        root.Children.Add(missingText);

        return new WindowChartSlot
        {
            Root = root,
            TitleText = titleText,
            UsedText = usedText,
            Chart = chart,
            MissingText = missingText
        };
    }
}
