using AgentsWinBar.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentsWinBar.Controls;

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

    private bool _disposed;

    private static void OnResetFromPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (DashboardWindowRow)d;
        if (!row._disposed) row.Meter.ResetFromPercent = (double?)e.NewValue;
    }

    private static void OnResetDetectedAtUtcChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (DashboardWindowRow)d;
        if (!row._disposed) row.Meter.ResetDetectedAtUtc = (DateTime?)e.NewValue;
    }

    public DashboardWindowRow()
    {
        InitializeComponent();
    }

    /// <summary>Stops child meter listeners/animations and detaches compiled bindings when the row is discarded or closed.</summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        Meter.Cleanup();
        Bindings?.StopTracking();
        SetValue(DataProperty, null);
        DataContext = null;
    }

    /// <summary>How many consecutive windows (ending with this one) share this window's reset; the caption names the group.</summary>
    public int GroupSize { get; private set; } = 1;

    /// <summary>False for a window whose group's reset caption is shown under a later row.</summary>
    public bool ShowReset { get; private set; } = true;

    /// <summary>Pace projection and window facts (the panel's "Show details").</summary>
    public bool ShowDetails { get; private set; }

    /// <summary>Sets the reset grouping and the details state together, redrawing once when either changed.</summary>
    public void SetLayout(bool showReset, int groupSize, bool showDetails)
    {
        if (_disposed) return;
        if (ShowReset == showReset && GroupSize == groupSize && ShowDetails == showDetails) return;
        ShowReset = showReset;
        GroupSize = Math.Max(1, groupSize);
        ShowDetails = showDetails;
        Bindings.Update();
    }

    public void RefreshTimes()
    {
        if (!_disposed) Bindings.Update();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (DashboardWindowRow)d;
        if (control._disposed) return;
        if (control.Content is UIElement content) content.Visibility = control.Data == null ? Visibility.Collapsed : Visibility.Visible;
        control.Bindings.Update();
    }
}
