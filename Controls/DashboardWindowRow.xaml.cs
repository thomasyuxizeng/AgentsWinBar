using CodexBarWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Controls;

/// <summary>One usage window on a dashboard provider panel. Binds to a <see cref="DashboardWindow"/>.</summary>
public sealed partial class DashboardWindowRow : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data),
        typeof(DashboardWindow), typeof(DashboardWindowRow), new PropertyMetadata(null, OnDataChanged));

    public DashboardWindow? Data
    {
        get => (DashboardWindow?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Forwarded to the inner <see cref="UsageMeter"/>, which plays the reset animation.</summary>
    public static readonly DependencyProperty ResetDetectedAtUtcProperty = DependencyProperty.Register(nameof(ResetDetectedAtUtc),
        typeof(DateTime?), typeof(DashboardWindowRow), new PropertyMetadata(null, OnResetDetectedAtUtcChanged));

    public DateTime? ResetDetectedAtUtc
    {
        get => (DateTime?)GetValue(ResetDetectedAtUtcProperty);
        set => SetValue(ResetDetectedAtUtcProperty, value);
    }

    public static readonly DependencyProperty ResetFromPercentProperty = DependencyProperty.Register(nameof(ResetFromPercent),
        typeof(double?), typeof(DashboardWindowRow), new PropertyMetadata(null, OnResetFromPercentChanged));

    public double? ResetFromPercent
    {
        get => (double?)GetValue(ResetFromPercentProperty);
        set => SetValue(ResetFromPercentProperty, value);
    }

    private static void OnResetFromPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DashboardWindowRow)d).Meter.ResetFromPercent = (double?)e.NewValue;

    private static void OnResetDetectedAtUtcChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DashboardWindowRow)d).Meter.ResetDetectedAtUtc = (DateTime?)e.NewValue;

    public DashboardWindowRow()
    {
        InitializeComponent();
    }

    public void RefreshTimes() => Bindings.Update();

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (DashboardWindowRow)d;
        if (control.Content is UIElement content) content.Visibility = control.Data == null ? Visibility.Collapsed : Visibility.Visible;
        control.Bindings.Update();
    }
}
