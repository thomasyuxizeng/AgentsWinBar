using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Views;
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
    private bool _suppressFullRefresh;
    private int _limitBand = -1;
    private DashboardWindow? _currentData;

    public DashboardWindow? CurrentData => _currentData ?? Data;

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
        LimitBadge.Background = null;
        LimitLabelText.Foreground = null;
        _limitBand = -1;
        _currentData = null;
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
        if (_currentData != null && !ReferenceEquals(_currentData, Data))
        {
            _suppressFullRefresh = true;
            try
            {
                Data = _currentData;
            }
            finally
            {
                _suppressFullRefresh = false;
            }
            UpdateLimitBrushes(_currentData.UsedPercent);
        }
        Bindings.Update();
    }

    public void ApplyClockTick(DashboardWindow updated)
    {
        if (_disposed) return;
        _currentData = updated;
        RefreshTimesCore(updated);
    }

    public void RefreshTimes()
    {
        if (_disposed || CurrentData is not { } data) return;
        RefreshTimesCore(data);
    }

    private void RefreshTimesCore(DashboardWindow data)
    {
        var time = DashboardFormat.Time(data.ElapsedPercent);
        if (!Meter.TimePercent.Equals(time)) Meter.TimePercent = time;
        if (!string.Equals(Meter.PaceText, data.PaceSummary, StringComparison.Ordinal))
            Meter.PaceText = data.PaceSummary;

        if (ShowReset)
        {
            var reset = DashboardFormat.ResetCaption(data, GroupSize);
            if (!string.Equals(ResetCaptionText.Text, reset, StringComparison.Ordinal))
                ResetCaptionText.Text = reset;
        }

        if (data.EarlyResetAtUtc != null || EarlyDetailText.Visibility == Visibility.Visible)
        {
            var early = DashboardFormat.EarlyDetail(data);
            var earlyVis = DashboardFormat.VisText(early);
            if (EarlyDetailText.Visibility != earlyVis) EarlyDetailText.Visibility = earlyVis;
            if (!string.Equals(EarlyDetailText.Text, early, StringComparison.Ordinal))
                EarlyDetailText.Text = early;
        }

        var alertVis = DashboardFormat.VisPaceAlert(data);
        if (PaceAlertPanel.Visibility != alertVis) PaceAlertPanel.Visibility = alertVis;
        if (alertVis == Visibility.Visible && !string.Equals(PaceAlertText.Text, data.PaceSummary, StringComparison.Ordinal))
            PaceAlertText.Text = data.PaceSummary;

        if (ShowDetails)
        {
            var detailVis = DashboardFormat.VisPaceDetail(data, ShowDetails);
            if (PaceDetailText.Visibility != detailVis) PaceDetailText.Visibility = detailVis;
            if (detailVis == Visibility.Visible && !string.Equals(PaceDetailText.Text, data.PaceSummary, StringComparison.Ordinal))
                PaceDetailText.Text = data.PaceSummary;
            var windowDetail = DashboardFormat.WindowDetail(data);
            if (!string.Equals(WindowDetailText.Text, windowDetail, StringComparison.Ordinal))
                WindowDetailText.Text = windowDetail;
        }
    }

    private void UpdateLimitBrushes(double percent)
    {
        var band = percent >= UsageThresholds.Critical ? 2
            : percent >= UsageThresholds.Warning ? 1 : 0;
        if (band == _limitBand) return;
        _limitBand = band;
        if (band == 0)
        {
            LimitBadge.Background = null;
            LimitLabelText.Foreground = null;
            return;
        }
        LimitBadge.Background = DashboardFormat.LimitBrush(percent);
        LimitLabelText.Foreground = DashboardFormat.LimitForeground(percent);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (DashboardWindowRow)d;
        if (control._disposed || control._suppressFullRefresh) return;
        control._currentData = control.Data;
        if (control.Content is UIElement content) content.Visibility = control.Data == null ? Visibility.Collapsed : Visibility.Visible;
        control.UpdateLimitBrushes(control.Data?.UsedPercent ?? 0);
        control.Bindings.Update();
    }
}
