using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBarWin;

/// <summary>
/// One line of the always-on overlay.
/// </summary>
public sealed class OverlayRow
{
    public string Name { get; set; } = string.Empty;
    public Microsoft.UI.Xaml.Media.Brush StatusBrush { get; set; } = null!;
    public Visibility IncidentVisibility { get; set; }
    public string StatusDescription { get; set; } = string.Empty;
    public Microsoft.UI.Xaml.Media.Geometry Icon { get; set; } = null!;
    public double SessionPercent { get; set; }
    /// <summary>The selected windows, including their session-only ResetDetectedAtUtc signals.</summary>
    public UsageWindow? SessionWindow { get; set; }
    public UsageWindow? WeeklyWindow { get; set; }
    /// <summary>The reset stamps the bars may show (null while a reset is held back until the overlay is on screen again).</summary>
    public DateTime? SessionResetStamp { get; set; }
    public DateTime? WeeklyResetStamp { get; set; }
    /// <summary>Identifies each bar across rebuilds, so a reset the overlay already played is not played again.</summary>
    public string SessionResetKey => Name + "|first";
    public string WeeklyResetKey => Name + "|second";
    public double SessionTimePercent { get; set; } = -1;
    public string SessionText { get; set; } = string.Empty;
    public double WeeklyPercent { get; set; }
    public double WeeklyTimePercent { get; set; } = -1;
    public string SessionPace { get; set; } = string.Empty;
    public string WeeklyPace { get; set; } = string.Empty;
    public WindowAccent SessionAccent { get; set; }
    public WindowAccent WeeklyAccent { get; set; }
    public string WeeklyText { get; set; } = string.Empty;
    public Microsoft.UI.Xaml.GridLength SessionNumberColumn { get; set; }
    public Microsoft.UI.Xaml.GridLength WeeklyNumberColumn { get; set; }
    public double RowHeight => OverlayWindow.RowHeightDip;
}

/// <summary>
/// Minimal always-on-top usage readout. Never takes focus, can be dragged,
/// click opens the full flyout, right-click for the menu.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    internal const double RowHeightDip = 22;
    // Spacing (DIP): 12 horizontal and 8 vertical padding + 1 border on every side, 8 between an element and
    // the bar that follows it, and NumberGapDip between a bar and its number. The width is worked out in
    // Rebuild() from the width of "100%"; ApplySize() also grows the window to the measured content if that
    // is ever larger.
    internal const double NumberGapDip = 6;
    private double _contentWidthDip = 214;
    private const double VerticalPaddingDip = 8 * 2 + 2;
    private const double ScreenMarginDip = 16;
    private const double ClipSlackDip = 2;
    private static readonly TimeSpan ActiveTick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HiddenTick = TimeSpan.FromSeconds(5);   // hidden for a fullscreen app
    private static readonly TimeSpan LockedTick = TimeSpan.FromSeconds(30);   // locked screen: only watch for the unlock

    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly ISessionStateSource _sessionState;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _topmostTimer;
    private int _tickCount;
    private bool _hiddenForFullscreen;
    private bool _wasLocked;
    private bool _shown;
    private bool _closed;
    private readonly IDisplayStateSource _displayState;
    private readonly ResetPlaybackGate _resetGate = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _rebuildHold;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _releaseTimer;
    private WindowZOrder.ForegroundWatcher? _foregroundWatcher;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private string? _lastSignature;
    private string? _lastApplySizeSignature;
    private double _lastScale;
    private SizeInt32 _lastTargetSize;
    private bool _hasTargetSize;

    public ObservableCollection<OverlayRow> Rows { get; } = [];

    /// <summary>
    /// Raised when the user clicks the overlay or picks "Open details".
    /// </summary>
    public event EventHandler? OpenDetailsRequested;

    /// <summary>
    /// Raised when the user picks "Hide overlay".
    /// </summary>
    public event EventHandler? HideRequested;

    public OverlayWindow()
    {
        InitializeComponent();

        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _sessionState = App.Services.GetRequiredService<ISessionStateSource>();
        _displayState = App.Services.GetRequiredService<IDisplayStateSource>();
        _hwnd = WindowNative.GetWindowHandle(this);

        ConfigureWindow();
        SetupAcrylicBackdrop();

        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        _sessionState.Locked += OnSessionLocked;
        _sessionState.Unlocked += OnSessionUnlocked;
        _sessionState.Resumed += OnSessionUnlocked;
        _displayState.Changed += OnDisplayStateChanged;
        Rebuild();
        PositionWindow();

        // Reassert topmost periodically as a safety net; the foreground watcher handles the immediate cases.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) =>
        {
            if (_closed || (!AppWindow.IsVisible && !_hiddenForFullscreen))
            {
                _topmostTimer.Stop();
                return;
            }

            // Unlock events don't reliably reach the app, so a locked screen slows the timer down instead of stopping it
            if (_sessionState.IsLocked)
            {
                _wasLocked = true;
                SyncResetGate();
                SetTickInterval(LockedTick);
                return;
            }

            if (_wasLocked)
            {
                ResumeAfterLock();
                return;
            }

            UpdateFullscreenVisibility();
            if (_hiddenForFullscreen) return;

            // Time keeps passing between data refreshes, so refresh the time layer about every 30s
            if (++_tickCount % 15 == 0)
            {
                Rebuild();
                ClampToScreen();   // a monitor change can strand the overlay off-screen, and Rebuild skips it for an unchanged signature
            }

            ApplySize();   // follows display scale changes (e.g. remote desktop)
            WindowZOrder.AssertTopmost(_hwnd);
        };

        App.Windows.Add(this);
        App.ApplyTheme();
        Closed += OnClosed;
    }

    /// <summary>
    /// Shows the overlay without stealing focus.
    /// </summary>
    public void ShowOverlay()
    {
        // WinUI only renders a window's content after its first Activate(), so activate once,
        // then hand the foreground straight back and stop the window from taking focus again.
        var previousForeground = WindowZOrder.GetForeground();
        Activate();
        WindowZOrder.MakeNonActivatingToolWindow(_hwnd);
        WindowZOrder.RemoveSystemBorder(_hwnd);   // the first show can reset the DWM border
        WindowZOrder.RestoreForeground(previousForeground);
        WindowZOrder.AssertTopmost(_hwnd);

        _foregroundWatcher ??= new WindowZOrder.ForegroundWatcher(() =>
        {
            // Unlocking moves the foreground, which is the quickest way to notice it
            if (_wasLocked && !_sessionState.IsLocked)
            {
                ResumeAfterLock();
                return;
            }

            UpdateFullscreenVisibility();
            if (!_hiddenForFullscreen && !_sessionState.IsLocked && AppWindow.IsVisible)
            {
                WindowZOrder.AssertTopmost(_hwnd);
            }
        });
        _shown = true;
        SyncResetGate();
        if (_sessionState.IsLocked)
        {
            _wasLocked = true;
            SetTickInterval(LockedTick);
        }
        else
        {
            SetTickInterval(ActiveTick);
        }
    }

    /// <summary>
    /// Whether the user can see the overlay right now. Until it is first shown it counts as on screen, so that an overlay
    /// switched on late does not hold back (and later replay) resets that were already on the bars.
    /// </summary>
    private bool IsOnScreen() =>
        !_shown || (!_closed && AppWindow.IsVisible && !_hiddenForFullscreen && !_sessionState.IsLocked && _displayState.IsOn);

    /// <summary>
    /// Tells the reset gate whether the overlay is on screen. Coming back with resets held releases them after the gate's
    /// delay, and the rebuild that follows hands them to the bars, which play the animation once.
    /// </summary>
    private void SyncResetGate()
    {
        if (_resetGate.SetOnScreen(IsOnScreen(), DateTime.UtcNow)) ScheduleReleaseRebuild();
    }

    /// <summary>Rebuilds the overlay once the gate lets a held reset through, one delay after the overlay came back.</summary>
    private void ScheduleReleaseRebuild()
    {
        if (_closed) return;
        if (_releaseTimer == null)
        {
            _releaseTimer = DispatcherQueue.CreateTimer();
            _releaseTimer.IsRepeating = false;
            _releaseTimer.Tick += (timer, _) =>
            {
                timer.Stop();
                try { if (!_closed) Rebuild(); }
                catch (Exception ex) { App.Services.GetRequiredService<ILogger<OverlayWindow>>().LogError(ex, "Overlay release rebuild failed"); }
            };
        }
        _releaseTimer.Interval = _resetGate.ReleaseDelay + TimeSpan.FromMilliseconds(50);
        _releaseTimer.Start();
    }

    private void OnDisplayStateChanged(object? sender, EventArgs e)
    {
        // Raised on a power-notification thread
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_closed) return;
                if (_displayState.IsOn) ResumeAfterLock();
                else SyncResetGate();
            }
            catch (Exception ex) { App.Services.GetRequiredService<ILogger<OverlayWindow>>().LogError(ex, "Overlay display state change failed"); }
        });
    }

    private void SetTickInterval(TimeSpan interval)
    {
        if (_closed) return;
        if (_topmostTimer.Interval != interval) _topmostTimer.Interval = interval;
        if (!_topmostTimer.IsEnabled) _topmostTimer.Start();
    }

    /// <summary>
    /// Picks the overlay's upkeep back up once the session is unlocked (or has resumed).
    /// </summary>
    private void ResumeAfterLock()
    {
        if (_closed || _sessionState.IsLocked) return;
        if (!_hiddenForFullscreen && !AppWindow.IsVisible) return;   // hidden on purpose; ShowOverlay restarts the timer

        _wasLocked = false;
        UpdateFullscreenVisibility();
        if (_hiddenForFullscreen)
        {
            SetTickInterval(HiddenTick);
            return;
        }

        _tickCount = 0;
        Rebuild();
        ApplySize();
        WindowZOrder.AssertTopmost(_hwnd);
        SetTickInterval(ActiveTick);
    }

    /// <summary>
    /// Hides the overlay while a fullscreen game, video or presentation is running, and brings it back after.
    /// </summary>
    private void UpdateFullscreenVisibility()
    {
        var fullscreen = WindowZOrder.IsFullscreenAppActive();
        if (fullscreen && !_hiddenForFullscreen)
        {
            _hiddenForFullscreen = true;
            AppWindow.Hide();
            SyncResetGate();
            // Keep a slow tick so the overlay comes back when the fullscreen app ends without a foreground change
            SetTickInterval(HiddenTick);
        }
        else if (!fullscreen && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            AppWindow.Show(false);
            if (!_closed && !_sessionState.IsLocked)
            {
                _tickCount = 0;
                Rebuild();
                ApplySize();
                WindowZOrder.AssertTopmost(_hwnd);
                SetTickInterval(ActiveTick);
            }
        }
    }

    private void OnSessionLocked(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_closed || !_topmostTimer.IsEnabled) return;
                _wasLocked = true;
                SetTickInterval(LockedTick);
            }
            catch { }
        });
    }

    private void OnSessionUnlocked(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ResumeAfterLock();
            }
            catch { }
        });
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "CodexBarWin overlay";

        NativeMethods.ApplyRoundedCorners(_hwnd);
        WindowZOrder.RemoveSystemBorder(_hwnd);
    }

    private void SetupAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported()) return;

        _acrylicController = new DesktopAcrylicController();

        // The overlay is never the active window, so force the "active" look.
        _backdropConfiguration = new SystemBackdropConfiguration { IsInputActive = true };
        UpdateBackdropTheme();

        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _acrylicController.AddSystemBackdropTarget(
            WinRT.CastExtensions.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>(this));

        RootGrid.ActualThemeChanged += (_, _) => UpdateBackdropTheme();
    }

    private void UpdateBackdropTheme()
    {
        if (_backdropConfiguration == null) return;

        _backdropConfiguration.Theme = RootGrid.ActualTheme switch
        {
            ElementTheme.Light => SystemBackdropTheme.Light,
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            _ => SystemBackdropTheme.Default
        };
    }

    private void OnProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(Rebuild);
    }

    internal static int RoundTimePercent(double? elapsed)
    {
        if (elapsed is not { } value || !double.IsFinite(value) || value < 0)
        {
            return -1;
        }
        return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
    }

    internal static string ComputeSignature(IEnumerable<UsageData> providers, DateTime nowUtc,
        Func<string, DateTime?, DateTime?>? resetStamp = null)
    {
        DateTime? Stamp(string bar, DateTime? detectedAtUtc) => resetStamp == null ? detectedAtUtc : resetStamp(bar, detectedAtUtc);
        var sb = new System.Text.StringBuilder();
        foreach (var data in providers)
        {
            var bars = WidgetBarSelector.Pick(data, nowUtc);
            var first = bars.ElementAtOrDefault(0);
            var second = bars.ElementAtOrDefault(1);

            (double Percent, string Text) Cell(WidgetBar? bar) =>
                bar != null ? (bar.Window.Percent, bar.Window.PercentText) : (0, data.IsLoading ? "…" : "–");

            var firstCell = Cell(first);
            var secondCell = Cell(second);

            sb.Append('|').Append(data.Provider)
              .Append(';').Append(data.IsLoading)
              .Append(';').Append(data.ServiceStatus?.Indicator)
              .Append(';').Append(StatusDetailCaption.Tooltip(data.ServiceStatus, nowUtc))
              .Append(';').Append(firstCell.Percent)
              .Append(';').Append(firstCell.Text)
              .Append(';').Append(RoundTimePercent(first?.Window.ElapsedPercent))
              .Append(';').Append(first?.TooltipText)
              .Append(';').Append(first?.Accent)
              .Append(';').Append(first?.Window.ResetFromUsedPercent)
              .Append(';').Append(Stamp(data.Provider + "|first", first?.Window.ResetDetectedAtUtc)?.Ticks)
              .Append(';').Append(secondCell.Percent)
              .Append(';').Append(secondCell.Text)
              .Append(';').Append(RoundTimePercent(second?.Window.ElapsedPercent))
              .Append(';').Append(second?.TooltipText)
              .Append(';').Append(second?.Accent)
              .Append(';').Append(second?.Window.ResetFromUsedPercent)
              .Append(';').Append(Stamp(data.Provider + "|second", second?.Window.ResetDetectedAtUtc)?.Ticks);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A rebuild recreates every row, which would cut a running reset animation short: while one of this window's bars
    /// plays, the rebuild waits until it is over (the bars it then creates do not replay it, see UsageMeter.ResetKey).
    /// </summary>
    private bool DeferRebuildWhileResetPlays()
    {
        var remaining = BarResetAnimation.RemainingBusy(RootGrid.XamlRoot);
        if (_closed || remaining <= TimeSpan.Zero) return false;

        if (_rebuildHold == null)
        {
            _rebuildHold = DispatcherQueue.CreateTimer();
            _rebuildHold.IsRepeating = false;
            _rebuildHold.Tick += (timer, _) =>
            {
                timer.Stop();
                try { if (!_closed) Rebuild(); }
                catch (Exception ex) { App.Services.GetRequiredService<ILogger<OverlayWindow>>().LogError(ex, "Deferred overlay rebuild failed"); }
            };
        }
        _rebuildHold.Interval = remaining + TimeSpan.FromMilliseconds(100);
        _rebuildHold.Start();
        return true;
    }

    private void Rebuild()
    {
        SyncResetGate();
        if (DeferRebuildWhileResetPlays()) return;
        var nowUtc = DateTime.UtcNow;
        Func<string, DateTime?, DateTime?> resetStamp = (bar, stamp) => _resetGate.Resolve(bar, stamp, nowUtc);
        var signature = ComputeSignature(_viewModel.Providers, nowUtc, resetStamp);
        if (_lastSignature != null && _lastSignature == signature)
        {
            return;
        }
        _lastSignature = signature;

        var rows = _viewModel.Providers.Select(p => ToRow(p, nowUtc, resetStamp)).ToList();

        // Number columns are always wide enough for "100%", so the window keeps its size whatever the digits
        // are (a value reaching 100% or dropping back after a reset must not make the panel jump). Numbers are
        // right-aligned, so the "%" signs line up and the gap to the bar is NumberGapDip next to a 3-digit
        // value and one digit wider next to a 2-digit one. Only a text wider than "100%" (never produced by
        // the percent formatting) could still widen a column.
        var widest = MeasureNumber("100%");
        var sessionText = rows.Select(r => MeasureNumber(r.SessionText)).DefaultIfEmpty(0).Max();
        var weeklyText = rows.Select(r => MeasureNumber(r.WeeklyText)).DefaultIfEmpty(0).Max();
        var sessionWidth = Math.Ceiling(Math.Max(widest, sessionText)) + 1 + NumberGapDip;
        var weeklyWidth = Math.Ceiling(Math.Max(widest, weeklyText)) + 1 + NumberGapDip;

        Rows.Clear();
        foreach (var row in rows)
        {
            row.SessionNumberColumn = new GridLength(sessionWidth);
            row.WeeklyNumberColumn = new GridLength(weeklyWidth);
            Rows.Add(row);
        }

        // 12 padding + 1 border per side; columns = icon + (8 lead-in + 44 bar + number column) twice, where
        // each number column already includes its NumberGapDip lead-in
        _contentWidthDip = 2 * (12 + 1) + (16 + (8 + 44) + sessionWidth + (8 + 44) + weeklyWidth);

        ApplySize();
        ClampToScreen();
    }

    internal static double MeasureNumber(string text)
    {
        var block = new Microsoft.UI.Xaml.Controls.TextBlock { Text = text, FontSize = 11 };
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    private static OverlayRow ToRow(UsageData data, DateTime nowUtc, Func<string, DateTime?, DateTime?> resetStamp)
    {
        // The two most relevant bars: normally session + weekly, but a more relevant window
        // (at/near its limit, ahead of schedule, or recently changed) can take a slot
        var bars = WidgetBarSelector.Pick(data, nowUtc);
        var first = bars.ElementAtOrDefault(0);
        var second = bars.ElementAtOrDefault(1);

        (double Percent, string Text) Cell(WidgetBar? bar) =>
            bar != null ? (bar.Window.Percent, bar.Window.PercentText) : (0, data.IsLoading ? "…" : "–");

        var firstCell = Cell(first);
        var secondCell = Cell(second);
        return new OverlayRow
        {
            Name = ProviderIcons.GetDisplayName(data.Provider),
            Icon = ProviderIcons.GetGeometry(data.Provider),
            StatusBrush = Controls.ProviderStatusVisuals.Brush(data.ServiceStatus),
            IncidentVisibility = ProviderStatusPresentation.IsIncident(data.ServiceStatus) ? Visibility.Visible : Visibility.Collapsed,
            StatusDescription = StatusDetailCaption.Tooltip(data.ServiceStatus, nowUtc),
            SessionPercent = firstCell.Percent,
            SessionWindow = first?.Window,
            WeeklyWindow = second?.Window,
            SessionResetStamp = resetStamp(data.Provider + "|first", first?.Window.ResetDetectedAtUtc),
            WeeklyResetStamp = resetStamp(data.Provider + "|second", second?.Window.ResetDetectedAtUtc),
            SessionTimePercent = first?.Window.ElapsedPercent ?? -1,
            SessionText = firstCell.Text,
            WeeklyPercent = secondCell.Percent,
            WeeklyTimePercent = second?.Window.ElapsedPercent ?? -1,
            SessionPace = first?.TooltipText ?? string.Empty,
            WeeklyPace = second?.TooltipText ?? string.Empty,
            SessionAccent = first?.Accent ?? WindowAccent.None,
            WeeklyAccent = second?.Accent ?? WindowAccent.None,
            WeeklyText = secondCell.Text
        };
    }

    /// <summary>
    /// Sizes the window to its content. Uses the computed width as a minimum and the measured content size
    /// when that is larger, so the right edge can never be clipped (e.g. after a display scale change).
    /// Only resizes when the size actually changed.
    /// </summary>
    private void ApplySize()
    {
        var scale = WindowZOrder.GetScale(_hwnd);
        var contentChanged = _lastApplySizeSignature != _lastSignature;
        var scaleChanged = scale != _lastScale;

        if (!contentChanged && !scaleChanged && _hasTargetSize)
        {
            var current = AppWindow.Size;
            if (current.Width == _lastTargetSize.Width && current.Height == _lastTargetSize.Height)
            {
                return;
            }
            AppWindow.Resize(_lastTargetSize);
            return;
        }

        _lastScale = scale;
        _lastApplySizeSignature = _lastSignature;

        var rows = Math.Max(1, Rows.Count);

        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var measured = RootGrid.DesiredSize;

        var widthDip = Math.Max(_contentWidthDip, measured.Width) + ClipSlackDip;
        var heightDip = Math.Max(rows * RowHeightDip + VerticalPaddingDip, measured.Height);

        var target = new SizeInt32((int)Math.Ceiling(widthDip * scale), (int)Math.Ceiling(heightDip * scale));
        _lastTargetSize = target;
        _hasTargetSize = true;

        var currentSize = AppWindow.Size;
        if (currentSize.Width != target.Width || currentSize.Height != target.Height)
        {
            AppWindow.Resize(target);
        }
    }

    private void PositionWindow()
    {
        var settings = _settingsService.Settings;
        var size = AppWindow.Size;

        PointInt32 position;
        if (settings.OverlayX is int x && settings.OverlayY is int y)
        {
            position = new PointInt32(x, y);
        }
        else
        {
            var workArea = DisplayArea.Primary.WorkArea;
            var margin = (int)(ScreenMarginDip * WindowZOrder.GetScale(_hwnd));
            position = new PointInt32(
                workArea.X + workArea.Width - size.Width - margin,
                workArea.Y + margin);
        }

        AppWindow.Move(position);
        ClampToScreen();
    }

    /// <summary>
    /// Keeps the overlay fully on a visible display (e.g. after a monitor was unplugged).
    /// </summary>
    private void ClampToScreen()
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var area = DisplayArea.GetFromPoint(position, DisplayAreaFallback.Nearest);
        if (area == null) return;

        var work = area.WorkArea;
        var x = Math.Clamp(position.X, work.X, Math.Max(work.X, work.X + work.Width - size.Width));
        var y = Math.Clamp(position.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - size.Height));

        if (x != position.X || y != position.Y)
        {
            AppWindow.Move(new PointInt32(x, y));
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;

        var before = AppWindow.Position;
        WindowZOrder.BeginDrag(_hwnd);   // blocks until the button is released
        var after = AppWindow.Position;

        if (before.X == after.X && before.Y == after.Y)
        {
            // Plain click, not a drag
            OpenDetailsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ClampToScreen();
        after = AppWindow.Position;

        var settings = _settingsService.Settings;
        settings.OverlayX = after.X;
        settings.OverlayY = after.Y;
        _settingsService.SaveQuietAsync().SafeFireAndForget(onError: _ => { });
    }

    private void OnOpenDetailsClick(object sender, RoutedEventArgs e)
    {
        OpenDetailsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHideOverlayClick(object sender, RoutedEventArgs e)
    {
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _topmostTimer.Stop();
        _rebuildHold?.Stop();
        _releaseTimer?.Stop();
        _sessionState.Locked -= OnSessionLocked;
        _sessionState.Unlocked -= OnSessionUnlocked;
        _sessionState.Resumed -= OnSessionUnlocked;
        _displayState.Changed -= OnDisplayStateChanged;
        _foregroundWatcher?.Dispose();
        _foregroundWatcher = null;
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        _acrylicController?.Dispose();
        _acrylicController = null;
        App.Windows.Remove(this);
    }
}
