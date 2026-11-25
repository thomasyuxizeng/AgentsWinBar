using CodexBarWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexBarWin.Controls;

/// <summary>One provider's charts on the dashboard's graphs section: usage history per window, then the daily cost. Binds to a <see cref="DashboardProvider"/>.</summary>
public sealed partial class DashboardGraphCard : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data),
        typeof(DashboardProvider), typeof(DashboardGraphCard), new PropertyMetadata(null, OnDataChanged));

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

    /// <summary>Detaches child charts, item sources, and compiled bindings when removed or closed.</summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupBurndownCharts(WindowsChartsList);
        WindowsChartsList.ItemsSource = null;
        WindowsChartsList.ItemTemplate = null;
        CostChart.Cleanup();
        Bindings?.StopTracking();
        SetValue(DataProperty, null);
        DataContext = null;
    }

    private static void CleanupBurndownCharts(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is BurndownChart chart) chart.Cleanup();
            else CleanupBurndownCharts(child);
        }
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (DashboardGraphCard)d;
        if (card._disposed) return;
        if (card.Content is UIElement content) content.Visibility = card.Data == null ? Visibility.Collapsed : Visibility.Visible;
        card.Bindings.Update();
    }
}
