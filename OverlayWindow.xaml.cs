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
public sealed class OverlayRow : InPlaceRow
{
    public string Name { get; set; } = string.Empty;
    public Microsoft.UI.Xaml.Media.Brush StatusBrush { get; set; } = null!;
    public Visibility IncidentVisibility { get; set; }
    public string StatusDescription { get; set; } = string.Empty;
    [KeepOnUpdate] public Microsoft.UI.Xaml.Media.Geometry Icon { get; set; } = null!;
    public double SessionPercent { get; set; }
    /// <summary>The selected windows, including their session-only ResetDetectedAtUtc signals.</summary>
    public UsageWindow? SessionWindow { get; set; }
    public UsageWindow? WeeklyWindow { get; set; }
    /// <summary>The reset stamps the bars may show (null while a reset is held back until the overlay is on screen again).</summary>
    [UpdateLast] public DateTime? SessionResetStamp { get; set; }
    [UpdateLast] public DateTime? WeeklyResetStamp { get; set; }
    /// <summary>The overlay's memory of the resets it played, shared by every bar it creates (see UsageMeter.ResetHost).</summary>
    public ResetHostState? ResetHost { get; set; }
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

    protected override int UpdateFromCore(InPlaceRow next)
    {
        var other = (OverlayRow)next;
        var changed = 0;
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) { Name = other.Name; changed++; RaisePropertyChanged(nameof(Name)); }
        if (!ReferenceEquals(StatusBrush, other.StatusBrush)) { StatusBrush = other.StatusBrush; changed++; RaisePropertyChanged(nameof(StatusBrush)); }
        if (IncidentVisibility != other.IncidentVisibility) { IncidentVisibility = other.IncidentVisibility; changed++; RaisePropertyChanged(nameof(IncidentVisibility)); }
        if (!string.Equals(StatusDescription, other.StatusDescription, StringComparison.Ordinal)) { StatusDescription = other.StatusDescription; changed++; RaisePropertyChanged(nameof(StatusDescription)); }
        if (!EqualityComparer<double>.Default.Equals(SessionPercent, other.SessionPercent)) { SessionPercent = other.SessionPercent; changed++; RaisePropertyChanged(nameof(SessionPercent)); }
        if (!EqualityComparer<UsageWindow?>.Default.Equals(SessionWindow, other.SessionWindow)) { SessionWindow = other.SessionWindow; changed++; RaisePropertyChanged(nameof(SessionWindow)); }
        if (!EqualityComparer<UsageWindow?>.Default.Equals(WeeklyWindow, other.WeeklyWindow)) { WeeklyWindow = other.WeeklyWindow; changed++; RaisePropertyChanged(nameof(WeeklyWindow)); }
        if (!ReferenceEquals(ResetHost, other.ResetHost)) { ResetHost = other.ResetHost; changed++; RaisePropertyChanged(nameof(ResetHost)); }
        if (!EqualityComparer<double>.Default.Equals(SessionTimePercent, other.SessionTimePercent)) { SessionTimePercent = other.SessionTimePercent; changed++; RaisePropertyChanged(nameof(SessionTimePercent)); }
        if (!string.Equals(SessionText, other.SessionText, StringComparison.Ordinal)) { SessionText = other.SessionText; changed++; RaisePropertyChanged(nameof(SessionText)); }
        if (!EqualityComparer<double>.Default.Equals(WeeklyPercent, other.WeeklyPercent)) { WeeklyPercent = other.WeeklyPercent; changed++; RaisePropertyChanged(nameof(WeeklyPercent)); }
        if (!EqualityComparer<double>.Default.Equals(WeeklyTimePercent, other.WeeklyTimePercent)) { WeeklyTimePercent = other.WeeklyTimePercent; changed++; RaisePropertyChanged(nameof(WeeklyTimePercent)); }
        if (!string.Equals(SessionPace, other.SessionPace, StringComparison.Ordinal)) { SessionPace = other.SessionPace; changed++; RaisePropertyChanged(nameof(SessionPace)); }
        if (!string.Equals(WeeklyPace, other.WeeklyPace, StringComparison.Ordinal)) { WeeklyPace = other.WeeklyPace; changed++; RaisePropertyChanged(nameof(WeeklyPace)); }
        if (SessionAccent != other.SessionAccent) { SessionAccent = other.SessionAccent; changed++; RaisePropertyChanged(nameof(SessionAccent)); }
        if (WeeklyAccent != other.WeeklyAccent) { WeeklyAccent = other.WeeklyAccent; changed++; RaisePropertyChanged(nameof(WeeklyAccent)); }
        if (!string.Equals(WeeklyText, other.WeeklyText, StringComparison.Ordinal)) { WeeklyText = other.WeeklyText; changed++; RaisePropertyChanged(nameof(WeeklyText)); }
        if (!SessionNumberColumn.Equals(other.SessionNumberColumn)) { SessionNumberColumn = other.SessionNumberColumn; changed++; RaisePropertyChanged(nameof(SessionNumberColumn)); }
        if (!WeeklyNumberColumn.Equals(other.WeeklyNumberColumn)) { WeeklyNumberColumn = other.WeeklyNumberColumn; changed++; RaisePropertyChanged(nameof(WeeklyNumberColumn)); }
        // [UpdateLast] properties copied after windows
        if (SessionResetStamp != other.SessionResetStamp) { SessionResetStamp = other.SessionResetStamp; changed++; RaisePropertyChanged(nameof(SessionResetStamp)); }
        if (WeeklyResetStamp != other.WeeklyResetStamp) { WeeklyResetStamp = other.WeeklyResetStamp; changed++; RaisePropertyChanged(nameof(WeeklyResetStamp)); }
        return changed;
    }
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
    // The lock screen covers the overlay, so noticing a lock a few ticks late changes nothing visible; the Locked event and
    // the foreground watcher still react at once, and the reset gate asks for the lock state itself
    private const int LockPollEveryTicks = 5;

    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly ISessionStateSource _sessionState;
    private readonly IntPtr _hwnd;
    private readonly object _timerLock = new();
    private readonly object _stateLock = new();
    private System.Threading.Timer? _pollTimer;
    private bool _pollEnabled;
    private int _pollBusy;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _loggedPollFailures = new();
    private int _tickCount;
    private int _lockPollCount;
    private volatile bool _hiddenForFullscreen;
    private volatile bool _wasLocked;
    private bool _shown;
    private volatile bool _closed;
    private readonly IDisplayStateSource _displayState;
    private readonly ResetPlaybackGate _resetGate = new();
    // Owned here and handed to every row a rebuild creates: which resets already played and whether one is playing now
    private readonly ResetHostState _resetHost = new();
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
            if (!_hiddenForFullscreen && !_sessionState.IsLocked && WindowZOrder.IsWindowVisible(_hwnd))
            {
                WindowZOrder.AssertTopmostIfCovered(_hwnd);
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
        !_shown || (!_closed && WindowZOrder.IsWindowVisible(_hwnd) && !_hiddenForFullscreen && !_sessionState.IsLocked && _displayState.IsOn);

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
        lock (_timerLock)
        {
            if (_closed) return;
            _pollEnabled = true;
            _pollTimer ??= new System.Threading.Timer(OnPollTimerCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _pollTimer.Change(interval, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReschedulePoll(TimeSpan interval)
    {
        lock (_timerLock)
        {
            if (_closed || !_pollEnabled || _pollTimer == null) return;
            _pollTimer.Change(interval, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnPollTimerCallback(object? _)
    {
        if (Interlocked.CompareExchange(ref _pollBusy, 1, 0) != 0) return;
        try
        {
            if (_closed) return;
            if (!WindowZOrder.IsWindowVisible(_hwnd) && !_hiddenForFullscreen)
            {
                lock (_timerLock) _pollEnabled = false;
                return;
            }

            var wasLocked = _wasLocked;
            var pollLock = wasLocked || (Interlocked.Increment(ref _lockPollCount) % LockPollEveryTicks == 0);
            if (pollLock && _sessionState.IsLocked)
            {
                if (!wasLocked)
                {
                    _wasLocked = true;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try { if (!_closed) SyncResetGate(); }
                        catch (Exception ex) { LogPollFailure(ex, "lock sync"); }
                    });
                }
                ReschedulePoll(LockedTick);
                return;
            }

            if (wasLocked)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { ResumeAfterLock(); }
                    catch (Exception ex) { LogPollFailure(ex, "resume after lock"); }
                });
                ReschedulePoll(ActiveTick);
                return;
            }

            var fullscreen = WindowZOrder.IsFullscreenAppActive();
            if (fullscreen != _hiddenForFullscreen)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { if (!_closed && !_sessionState.IsLocked) UpdateFullscreenVisibility(); }
                    catch (Exception ex) { LogPollFailure(ex, "fullscreen check"); }
                });
                ReschedulePoll(fullscreen ? HiddenTick : ActiveTick);
                return;
            }

            if (fullscreen)
            {
                ReschedulePoll(HiddenTick);
                return;
            }

            var tick = Interlocked.Increment(ref _tickCount);
            var timeTick = tick % 15 == 0;

            var needsUiWork = timeTick;
            if (!needsUiWork)
            {
                double lastScale;
                SizeInt32 lastTargetSize;
                bool hasTargetSize;
                lock (_stateLock)
                {
                    lastScale = _lastScale;
                    lastTargetSize = _lastTargetSize;
                    hasTargetSize = _hasTargetSize;
                }

                if (!hasTargetSize || WindowZOrder.GetScale(_hwnd) != lastScale ||
                    !WindowZOrder.TryGetWindowRect(_hwnd, out var rect) ||
                    rect.Right - rect.Left != lastTargetSize.Width || rect.Bottom - rect.Top != lastTargetSize.Height)
                {
                    needsUiWork = true;
                }
            }

            var needsRaise = !needsUiWork && WindowZOrder.NeedsRaise(_hwnd);
            if (needsUiWork || needsRaise)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { RunActiveMaintenance(timeTick); }
                    catch (Exception ex) { LogPollFailure(ex, "reposition"); }
                });
            }

            ReschedulePoll(ActiveTick);
        }
        catch (Exception ex)
        {
            LogPollFailure(ex, "poll");
            ReschedulePoll(ActiveTick);
        }
        finally { Volatile.Write(ref _pollBusy, 0); }
    }

    /// <summary>
    /// Release builds have no logging provider, so poll failures go to crash.log. The poll runs every 2 s: each step is
    /// recorded once per run, so a failure that repeats cannot fill the file.
    /// </summary>
    private void LogPollFailure(Exception ex, string step)
    {
        if (_loggedPollFailures.TryAdd(step, 0)) App.LogHandled($"Overlay {step} failed", ex);
    }

    private void RunActiveMaintenance(bool timeTick)
    {
        if (_closed || _wasLocked || _hiddenForFullscreen || !WindowZOrder.IsWindowVisible(_hwnd)) return;
        if (timeTick)
        {
            Rebuild();
            ClampToScreen();
        }
        ApplySize();
        WindowZOrder.AssertTopmostIfCovered(_hwnd);
    }

    /// <summary>
    /// Picks the overlay's upkeep back up once the session is unlocked (or has resumed).
    /// </summary>
    private void ResumeAfterLock()
    {
        if (_closed || _sessionState.IsLocked) return;
        if (!_hiddenForFullscreen && !WindowZOrder.IsWindowVisible(_hwnd)) return;   // hidden on purpose; ShowOverlay restarts the timer

        _wasLocked = false;
        UpdateFullscreenVisibility();
        if (_hiddenForFullscreen)
        {
            SetTickInterval(HiddenTick);
            return;
        }

        Interlocked.Exchange(ref _tickCount, 0);
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
                Interlocked.Exchange(ref _tickCount, 0);
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
                bool enabled;
                lock (_timerLock) enabled = _pollEnabled;
                if (_closed || !enabled) return;
                _wasLocked = true;
                SyncResetGate();
                SetTickInterval(LockedTick);
            }
            catch (Exception ex) { LogPollFailure(ex, "lock"); }
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
            catch (Exception ex) { LogPollFailure(ex, "unlock"); }
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

        WindowZOrder.HideFromSwitchers(AppWindow, _hwnd);
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

    internal static int RoundTimePercent(double? elapsed) => WidgetSignature.RoundTimePercent(elapsed);

    internal static string ComputeSignature(IEnumerable<UsageData> providers, DateTime nowUtc,
        Func<string, DateTime?, DateTime?>? resetStamp = null) =>
        WidgetSignature.ComputeOverlaySignature(providers, nowUtc, resetStamp);

    /// <summary>
    /// A rebuild updates or recreates the rows, which would cut a running reset animation short: while one of this window's bars
    /// plays, the rebuild waits until it is over (the bars it then creates do not replay it, see UsageMeter.ResetKey).
    /// </summary>
    private bool DeferRebuildWhileResetPlays()
    {
        var remaining = _resetHost.RemainingBusy(Environment.TickCount64);
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
        var scale = WindowZOrder.GetScale(_hwnd);
        var widest = MeasureNumber("100%", scale);
        var sessionText = rows.Select(r => MeasureNumber(r.SessionText, scale)).DefaultIfEmpty(0).Max();
        var weeklyText = rows.Select(r => MeasureNumber(r.WeeklyText, scale)).DefaultIfEmpty(0).Max();
        var sessionWidth = Math.Ceiling(Math.Max(widest, sessionText)) + 1 + NumberGapDip;
        var weeklyWidth = Math.Ceiling(Math.Max(widest, weeklyText)) + 1 + NumberGapDip;

        foreach (var row in rows)
        {
            row.ResetHost = _resetHost;
            row.SessionNumberColumn = new GridLength(sessionWidth);
            row.WeeklyNumberColumn = new GridLength(weeklyWidth);
        }
        // Same providers in the same order: update the rows already shown instead of recreating them
        InPlaceRow.Apply(Rows, rows, row => row.Name);

        // 12 padding + 1 border per side; columns = icon + (8 lead-in + 44 bar + number column) twice, where
        // each number column already includes its NumberGapDip lead-in
        _contentWidthDip = 2 * (12 + 1) + (16 + (8 + 44) + sessionWidth + (8 + 44) + weeklyWidth);

        ApplySize();
        ClampToScreen();
    }

    [ThreadStatic]
    private static Dictionary<(double Scale, string Text), double>? _measuredNumberCache;
    [ThreadStatic]
    private static Microsoft.UI.Xaml.Controls.TextBlock? _measureProbe;

    internal static double MeasureNumber(string text, double scale = 1.0)
    {
        var cache = _measuredNumberCache ??= new Dictionary<(double Scale, string Text), double>();
        var key = (scale, text);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _measureProbe ??= new Microsoft.UI.Xaml.Controls.TextBlock { FontSize = 11 };
        _measureProbe.Text = text;
        _measureProbe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = _measureProbe.DesiredSize.Width;

        if (cache.Count >= 256)
        {
            cache.Clear();
        }
        cache[key] = width;
        return width;
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
            // A plain Win32 query: this runs on every tick
            if (WindowZOrder.TryGetWindowRect(_hwnd, out var current) &&
                current.Right - current.Left == _lastTargetSize.Width && current.Bottom - current.Top == _lastTargetSize.Height)
            {
                return;
            }
            AppWindow.Resize(_lastTargetSize);
            return;
        }

        lock (_stateLock)
        {
            _lastScale = scale;
        }
        _lastApplySizeSignature = _lastSignature;

        var rows = Math.Max(1, Rows.Count);

        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var measured = RootGrid.DesiredSize;

        var widthDip = Math.Max(_contentWidthDip, measured.Width) + ClipSlackDip;
        var heightDip = Math.Max(rows * RowHeightDip + VerticalPaddingDip, measured.Height);

        var target = new SizeInt32((int)Math.Ceiling(widthDip * scale), (int)Math.Ceiling(heightDip * scale));
        lock (_stateLock)
        {
            _lastTargetSize = target;
            _hasTargetSize = true;
        }

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
        lock (_timerLock)
        {
            _pollEnabled = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
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
