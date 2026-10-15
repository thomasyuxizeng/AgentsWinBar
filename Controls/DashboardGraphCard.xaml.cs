using CodexBarWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Controls;

/// <summary>One provider's charts on the dashboard's graphs section: usage history per window, then the daily cost. Binds to a <see cref="DashboardProvider"/>.</summary>
public sealed partial class DashboardGraphCard : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data),
        typeof(DashboardProvider), typeof(DashboardGraphCard), new PropertyMetadata(null, OnDataChanged));

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

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (DashboardGraphCard)d;
        if (card.Content is UIElement content) content.Visibility = card.Data == null ? Visibility.Collapsed : Visibility.Visible;
        card.Bindings.Update();
    }
}
