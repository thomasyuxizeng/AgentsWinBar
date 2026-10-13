using CodexBarWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Controls;

/// <summary>One usage window as a single compact line (the dashboard's overview). Binds to a <see cref="DashboardWindow"/>.</summary>
public sealed partial class DashboardMiniBar : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data),
        typeof(DashboardWindow), typeof(DashboardMiniBar), new PropertyMetadata(null, OnDataChanged));

    public DashboardWindow? Data
    {
        get => (DashboardWindow?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Forwarded to the inner <see cref="UsageMeter"/>, which plays the reset animation. TODO: bind ResetDetectedAtUtc from the window model once it lands.</summary>
    public static readonly DependencyProperty ResetDetectedAtUtcProperty = DependencyProperty.Register(nameof(ResetDetectedAtUtc),
        typeof(DateTime?), typeof(DashboardMiniBar), new PropertyMetadata(null, OnResetDetectedAtUtcChanged));

    public DateTime? ResetDetectedAtUtc
    {
        get => (DateTime?)GetValue(ResetDetectedAtUtcProperty);
        set => SetValue(ResetDetectedAtUtcProperty, value);
    }

    private static void OnResetDetectedAtUtcChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DashboardMiniBar)d).Meter.ResetDetectedAtUtc = (DateTime?)e.NewValue;

    public DashboardMiniBar()
    {
        InitializeComponent();
    }

    public void RefreshTimes() => Bindings.Update();

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (DashboardMiniBar)d;
        if (control.Content is UIElement content) content.Visibility = control.Data == null ? Visibility.Collapsed : Visibility.Visible;
        control.Bindings.Update();
    }
}
