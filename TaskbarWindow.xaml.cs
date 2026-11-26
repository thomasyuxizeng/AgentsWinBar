using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentsWinBar;

/// <summary>
/// One provider in the taskbar strip.
/// </summary>
public sealed class TaskbarItem : InPlaceRow
{
    public string Name { get; set; } = string.Empty;
    public Microsoft.UI.Xaml.Media.Brush StatusBrush { get; set; } = null!;
    public Visibility IncidentVisibility { get; set; }
    public string StatusDescription { get; set; } = string.Empty;
    [KeepOnUpdate] public Geometry Icon { get; set; } = null!;
    public double SessionPercent { get; set; }
    public double WeeklyPercent { get; set; }
    /// <summary>The selected windows, including their session-only ResetDetectedAtUtc signals.</summary>
    public UsageWindow? SessionWindow { get; set; }
    public UsageWindow? WeeklyWindow { get; set; }
    /// <summary>The reset stamps the bars may show (null while a reset is held back until the strip is on screen again).</summary>
    [UpdateLast] public DateTime? SessionResetStamp { get; set; }
    [UpdateLast] public DateTime? WeeklyResetStamp { get; set; }
    /// <summary>The strip's memory of the resets it played, shared by every bar it creates (see UsageMeter.ResetHost).</summary>
    public ResetHostState? ResetHost { get; set; }
    /// <summary>Identifies each bar across rebuilds, so a reset the strip already played is not played again.</summary>
    public string SessionResetKey => Name + "|first";
    public string WeeklyResetKey => Name + "|second";
    public double SessionTimePercent { get; set; } = -1;
    public double WeeklyTimePercent { get; set; } = -1;
    public string SessionPace { get; set; } = string.Empty;
    public string WeeklyPace { get; set; } = string.Empty;
    public WindowAccent SessionAccent { get; set; }
    public WindowAccent WeeklyAccent { get; set; }
    public string SessionText { get; set; } = string.Empty;
    public string WeeklyText { get; set; } = string.Empty;
    public Brush SessionBrush { get; set; } = null!;
    public Brush WeeklyBrush { get; set; } = null!;
    public string ToolTip { get; set; } = string.Empty;
    /// <summary>Width of the number column: always enough for "100%", so the strip never resizes with the digits.</summary>
    public GridLength NumberColumn { get; set; }

    protected override int UpdateFromCore(InPlaceRow next)
    {
        var other = (TaskbarItem)next;
        var changed = 0;
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) { Name = other.Name; changed++; RaisePropertyChanged(nameof(Name)); }
        if (!ReferenceEquals(StatusBrush, other.StatusBrush)) { StatusBrush = other.StatusBrush; changed++; RaisePropertyChanged(nameof(StatusBrush)); }
        if (IncidentVisibility != other.IncidentVisibility) { IncidentVisibility = other.IncidentVisibility; changed++; RaisePropertyChanged(nameof(IncidentVisibility)); }
        if (!string.Equals(StatusDescription, other.StatusDescription, StringComparison.Ordinal)) { StatusDescription = other.StatusDescription; changed++; RaisePropertyChanged(nameof(StatusDescription)); }
        if (!EqualityComparer<double>.Default.Equals(SessionPercent, other.SessionPercent)) { SessionPercent = other.SessionPercent; changed++; RaisePropertyChanged(nameof(SessionPercent)); }
        if (!EqualityComparer<double>.Default.Equals(WeeklyPercent, other.WeeklyPercent)) { WeeklyPercent = other.WeeklyPercent; changed++; RaisePropertyChanged(nameof(WeeklyPercent)); }
        if (!EqualityComparer<UsageWindow?>.Default.Equals(SessionWindow, other.SessionWindow)) { SessionWindow = other.SessionWindow; changed++; RaisePropertyChanged(nameof(SessionWindow)); }
        if (!EqualityComparer<UsageWindow?>.Default.Equals(WeeklyWindow, other.WeeklyWindow)) { WeeklyWindow = other.WeeklyWindow; changed++; RaisePropertyChanged(nameof(WeeklyWindow)); }
        if (!ReferenceEquals(ResetHost, other.ResetHost)) { ResetHost = other.ResetHost; changed++; RaisePropertyChanged(nameof(ResetHost)); }
        if (!EqualityComparer<double>.Default.Equals(SessionTimePercent, other.SessionTimePercent)) { SessionTimePercent = other.SessionTimePercent; changed++; RaisePropertyChanged(nameof(SessionTimePercent)); }
        if (!EqualityComparer<double>.Default.Equals(WeeklyTimePercent, other.WeeklyTimePercent)) { WeeklyTimePercent = other.WeeklyTimePercent; changed++; RaisePropertyChanged(nameof(WeeklyTimePercent)); }
        if (!string.Equals(SessionPace, other.SessionPace, StringComparison.Ordinal)) { SessionPace = other.SessionPace; changed++; RaisePropertyChanged(nameof(SessionPace)); }
        if (!string.Equals(WeeklyPace, other.WeeklyPace, StringComparison.Ordinal)) { WeeklyPace = other.WeeklyPace; changed++; RaisePropertyChanged(nameof(WeeklyPace)); }
        if (SessionAccent != other.SessionAccent) { SessionAccent = other.SessionAccent; changed++; RaisePropertyChanged(nameof(SessionAccent)); }
        if (WeeklyAccent != other.WeeklyAccent) { WeeklyAccent = other.WeeklyAccent; changed++; RaisePropertyChanged(nameof(WeeklyAccent)); }
        if (!string.Equals(SessionText, other.SessionText, StringComparison.Ordinal)) { SessionText = other.SessionText; changed++; RaisePropertyChanged(nameof(SessionText)); }
        if (!string.Equals(WeeklyText, other.WeeklyText, StringComparison.Ordinal)) { WeeklyText = other.WeeklyText; changed++; RaisePropertyChanged(nameof(WeeklyText)); }
        if (!ReferenceEquals(SessionBrush, other.SessionBrush)) { SessionBrush = other.SessionBrush; changed++; RaisePropertyChanged(nameof(SessionBrush)); }
        if (!ReferenceEquals(WeeklyBrush, other.WeeklyBrush)) { WeeklyBrush = other.WeeklyBrush; changed++; RaisePropertyChanged(nameof(WeeklyBrush)); }
        if (!string.Equals(ToolTip, other.ToolTip, StringComparison.Ordinal)) { ToolTip = other.ToolTip; changed++; RaisePropertyChanged(nameof(ToolTip)); }
        if (!NumberColumn.Equals(other.NumberColumn)) { NumberColumn = other.NumberColumn; changed++; RaisePropertyChanged(nameof(NumberColumn)); }
        // [UpdateLast] properties copied after windows
        if (SessionResetStamp != other.SessionResetStamp) { SessionResetStamp = other.SessionResetStamp; changed++; RaisePropertyChanged(nameof(SessionResetStamp)); }
        if (WeeklyResetStamp != other.WeeklyResetStamp) { WeeklyResetStamp = other.WeeklyResetStamp; changed++; RaisePropertyChanged(nameof(WeeklyResetStamp)); }
        return changed;
    }
}

/// <summary>
/// Root grid of <see cref="TaskbarWindow"/> exposing UI Automation <see cref="IInvokeProvider"/>
/// so UI automation callers can toggle the flyout directly.
/// </summary>
public sealed class TaskbarStripGrid : Microsoft.UI.Xaml.Controls.Grid
{
    public event EventHandler? AutomationInvoked;

    protected override AutomationPeer OnCreateAutomationPeer() => new TaskbarStripAutomationPeer(this);

    private sealed class TaskbarStripAutomationPeer(TaskbarStripGrid owner)
        : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override object? GetPatternCore(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

        public void Invoke()
        {
            try
            {
                if (Owner is TaskbarStripGrid grid)
                {
                    grid.AutomationInvoked?.Invoke(grid, EventArgs.Empty);
                }
            }
            catch
            {
            }
        }
    }
}

/// <summary>
/// Compact usage strip shown on the taskbar, bottom-left corner. Never takes focus;
/// click opens the flyout, right-click for the menu.
/// </summary>
public sealed partial class TaskbarWindow : Window
{
    // Item = 16 icon + 10 gap + 56 bar + 6 gap + about 28 number (the number column is always as wide as
    // "100%", so the strip never changes width with the digits); strip = 18 padding per side, no border.
    // These give a lower bound only: Reposition() grows the strip to the measured content if needed.
    // The strip sits flush: full taskbar height, starting at the taskbar's left edge.
    private const double ClipSlackDip = 2;
    private const double ItemWidthDip = 116;
    private const double ItemSpacingDip = 24;
    private const double SidePaddingDip = 18;
    private const double HorizontalPaddingDip = 2 * SidePaddingDip;
    private const double OverhangDip = 12;   // how far the left end is pushed off-screen to hide its rounded corners
    private const double FallbackHeightDip = 48;
    private const double TopLineDip = 1;   // the thin line along the taskbar's top edge
    private static readonly TimeSpan ActiveTick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HiddenTick = TimeSpan.FromSeconds(5);   // hidden for a fullscreen app
    private static readonly TimeSpan LockedTick = TimeSpan.FromSeconds(30);   // locked screen: only watch for the unlock
    // The lock screen covers the strip, so noticing a lock a few ticks late changes nothing visible; the Locked event and
    // the foreground watcher still react at once, and the reset gate asks for the lock state itself
    private const int LockPollEveryTicks = 5;
    // The taskbar position is re-read when Windows reports a change (TaskbarChangeWatcher), on a foreground change and,
    // as a fallback, every 15 ticks (about 30 s)
    private const int TaskbarPollEveryTicks = 15;

    private readonly MainViewModel _viewModel;
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
    private TaskbarChangeWatcher? _taskbarChangeWatcher;
    private volatile bool _taskbarWatcherActive;
    private volatile bool _taskbarBoundsStale = true;
    private (int Left, int Top, int Right, int Bottom, WindowZOrder.TaskbarEdge Edge)? _taskbarBounds;
    private bool _roundedRightEnd;
    private volatile bool _hiddenForFullscreen;
    private volatile bool _wasLocked;
    private bool _shown;
    private volatile bool _closed;
    private readonly IDisplayStateSource _displayState;
    private readonly ResetPlaybackGate _resetGate = new();
    // Owned here and handed to every bar a rebuild creates: which resets already played and whether one is playing now
    private readonly ResetHostState _resetHost = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _rebuildHold;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _releaseTimer;
    private WindowZOrder.ForegroundWatcher? _foregroundWatcher;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private string? _lastSignature;
    private string? _lastRepositionSignature;
    private double _lastScale;
    private (int, int, int, int, WindowZOrder.TaskbarEdge)? _lastTaskbarBounds;
    private RectInt32 _lastScreenBounds;
    private RectInt32 _lastTarget;
    private bool _hasTarget;

    public ObservableCollection<TaskbarItem> Items { get; } = [];

    /// <summary>
    /// Raised when the user clicks the strip or picks "Open details".
    /// </summary>
    public event EventHandler? OpenDetailsRequested;

    /// <summary>
    /// Raised when the user picks "Open dashboard".
    /// </summary>
    public event EventHandler? OpenDashboardRequested;

    /// <summary>
    /// Raised when the user picks "Hide taskbar display".
    /// </summary>
    public event EventHandler? HideRequested;

    public TaskbarWindow()
    {
        InitializeComponent();

        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _sessionState = App.Services.GetRequiredService<ISessionStateSource>();
        _displayState = App.Services.GetRequiredService<IDisplayStateSource>();
        _hwnd = WindowNative.GetWindowHandle(this);

        ConfigureWindow();
        SetupAcrylicBackdrop();

        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        RootGrid.ActualThemeChanged += (_, _) => Rebuild();
        RootGrid.AutomationInvoked += OnStripAutomationInvoked;
        _sessionState.Locked += OnSessionLocked;
        _sessionState.Unlocked += OnSessionUnlocked;
        _sessionState.Resumed += OnSessionUnlocked;
        _displayState.Changed += OnDisplayStateChanged;
        Rebuild();

        App.Windows.Add(this);
        App.ApplyTheme();
        Closed += OnClosed;
    }

    /// <summary>
    /// Shows the strip without stealing focus.
    /// </summary>
    public void ShowStrip()
    {
        // WinUI only renders content after the first Activate(); hand the foreground straight back.
        var previousForeground = WindowZOrder.GetForeground();
        Activate();
        WindowZOrder.MakeNonActivatingToolWindow(_hwnd);
        WindowZOrder.RemoveSystemBorder(_hwnd);
        WindowZOrder.RestoreForeground(previousForeground);

        _taskbarChangeWatcher ??= new TaskbarChangeWatcher(_hwnd, () => _taskbarBoundsStale = true);
        _taskbarWatcherActive = _taskbarChangeWatcher.IsActive;
        _taskbarBoundsStale = true;
        Reposition();
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
                _taskbarBoundsStale = true;
                Reposition();
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
    /// Whether the user can see the strip right now. Until it is first shown it counts as on screen, so that a strip
    /// switched on late does not hold back (and later replay) resets that were already on the bars.
    /// </summary>
    private bool IsOnScreen() =>
        !_shown || (!_closed && WindowZOrder.IsWindowVisible(_hwnd) && !_hiddenForFullscreen && !_sessionState.IsLocked && _displayState.IsOn);

    /// <summary>
    /// Tells the reset gate whether the strip is on screen. Coming back with resets held releases them after the gate's
    /// delay, and the rebuild that follows hands them to the bars, which play the animation once.
    /// </summary>
    private void SyncResetGate()
    {
        if (_resetGate.SetOnScreen(IsOnScreen(), DateTime.UtcNow)) ScheduleReleaseRebuild();
    }

    /// <summary>Rebuilds the strip once the gate lets a held reset through, one delay after the strip came back.</summary>
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
                catch (Exception ex) { App.Services.GetRequiredService<ILogger<TaskbarWindow>>().LogError(ex, "Taskbar release rebuild failed"); }
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
            catch (Exception ex) { App.Services.GetRequiredService<ILogger<TaskbarWindow>>().LogError(ex, "Taskbar display state change failed"); }
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
            var pollTaskbar = tick % TaskbarPollEveryTicks == 0 || !_taskbarWatcherActive || _taskbarBoundsStale;

            var needsUiWork = timeTick || pollTaskbar;
            if (!needsUiWork)
            {
                double lastScale;
                RectInt32 lastTarget;
                bool hasTarget;
                lock (_stateLock)
                {
                    lastScale = _lastScale;
                    lastTarget = _lastTarget;
                    hasTarget = _hasTarget;
                }

                if (!hasTarget || WindowZOrder.GetScale(_hwnd) != lastScale ||
                    !WindowZOrder.TryGetWindowRect(_hwnd, out var rect) ||
                    rect.Left != lastTarget.X || rect.Top != lastTarget.Y ||
                    rect.Right - rect.Left != lastTarget.Width || rect.Bottom - rect.Top != lastTarget.Height)
                {
                    needsUiWork = true;
                }
            }

            var needsRaise = !needsUiWork && WindowZOrder.NeedsRaise(_hwnd);
            if (needsUiWork || needsRaise)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { RunActiveMaintenance(timeTick, pollTaskbar); }
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
        if (_loggedPollFailures.TryAdd(step, 0)) App.LogHandled($"Taskbar {step} failed", ex);
    }

    private void RunActiveMaintenance(bool timeTick, bool pollTaskbar)
    {
        if (_closed || _wasLocked || _hiddenForFullscreen || !WindowZOrder.IsWindowVisible(_hwnd)) return;
        if (timeTick)
        {
            Rebuild();
        }
        if (pollTaskbar)
        {
            _taskbarBoundsStale = true;
        }
        Reposition();
        WindowZOrder.AssertTopmostIfCovered(_hwnd);
    }

    /// <summary>
    /// Picks the strip's upkeep back up once the session is unlocked (or has resumed).
    /// </summary>
    private void ResumeAfterLock()
    {
        if (_closed || _sessionState.IsLocked) return;
        if (!_hiddenForFullscreen && !WindowZOrder.IsWindowVisible(_hwnd)) return;   // hidden on purpose; ShowStrip restarts the timer

        _wasLocked = false;
        UpdateFullscreenVisibility();
        if (_hiddenForFullscreen)
        {
            SetTickInterval(HiddenTick);
            return;
        }

        Interlocked.Exchange(ref _tickCount, 0);
        _taskbarBoundsStale = true;
        Rebuild();
        Reposition();
        WindowZOrder.AssertTopmost(_hwnd);
        SetTickInterval(ActiveTick);
    }

    /// <summary>
    /// Hides the strip while a fullscreen game, video or presentation is running, and brings it back after.
    /// </summary>
    private void UpdateFullscreenVisibility()
    {
        var fullscreen = WindowZOrder.IsFullscreenAppActive();
        if (fullscreen && !_hiddenForFullscreen)
        {
            _hiddenForFullscreen = true;
            AppWindow.Hide();
            SyncResetGate();
            // Keep a slow tick so the strip comes back when the fullscreen app ends without a foreground change
            SetTickInterval(HiddenTick);
        }
        else if (!fullscreen && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            AppWindow.Show(false);
            if (!_closed && !_sessionState.IsLocked)
            {
                Interlocked.Exchange(ref _tickCount, 0);
                _taskbarBoundsStale = true;
                Rebuild();
                Reposition();
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
        AppWindow.Title = "AgentsWinBar taskbar display";

        // Flush with the taskbar: square corners (no rounding) and, via the XAML, no border
        WindowZOrder.DisableRoundedCorners(_hwnd);
    }

    private void SetupAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported()) return;

        _acrylicController = new DesktopAcrylicController();

        // Never the active window, so force the "active" look.
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

    internal static string ComputeSignature(IEnumerable<UsageData> providers, bool isLight, DateTime nowUtc,
        Func<string, DateTime?, DateTime?>? resetStamp = null) =>
        WidgetSignature.ComputeTaskbarSignature(providers, isLight, nowUtc, resetStamp);

    /// <summary>
    /// A rebuild updates or recreates the bars, which would cut a running reset animation short: while one of this window's bars
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
                catch (Exception ex) { App.Services.GetRequiredService<ILogger<TaskbarWindow>>().LogError(ex, "Deferred taskbar rebuild failed"); }
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
        var isLight = RootGrid.ActualTheme == ElementTheme.Light;
        var nowUtc = DateTime.UtcNow;
        var signature = ComputeSignature(_viewModel.Providers, isLight, nowUtc, (bar, stamp) => _resetGate.Resolve(bar, stamp, nowUtc));
        if (_lastSignature != null && _lastSignature == signature)
        {
            return;
        }
        _lastSignature = signature;

        var primary = isLight ? LightPrimaryBrush : DarkPrimaryBrush;

        // Always wide enough for "100%" (numbers are right-aligned), so a value reaching 100% or dropping back
        // after a reset does not change the strip's width
        var numberColumn = new GridLength(Math.Ceiling(OverlayWindow.MeasureNumber("100%", WindowZOrder.GetScale(_hwnd))) + 1);

        var next = new List<TaskbarItem>(_viewModel.Providers.Count);
        // Same providers in the same order: the bars on the strip are updated in place and keep their icons
        var names = _viewModel.Providers.Select(p => ProviderIcons.GetDisplayName(p.Provider)).ToList();
        var inPlace = names.Count > 0 && Items.Select(item => item.Name).SequenceEqual(names);
        foreach (var data in _viewModel.Providers)
        {
            var name = ProviderIcons.GetDisplayName(data.Provider);

            // The two most relevant bars: normally session + weekly, but a more relevant window
            // (at/near its limit, ahead of schedule, or recently changed) can take a slot
            var bars = WidgetBarSelector.Pick(data, nowUtc);
            var first = bars.ElementAtOrDefault(0);
            var second = bars.ElementAtOrDefault(1);
            var firstText = Cell(first?.Window, data.IsLoading);
            var secondText = Cell(second?.Window, data.IsLoading);

            next.Add(new TaskbarItem
            {
                Name = name,
                Icon = inPlace ? Items[next.Count].Icon : ProviderIcons.GetGeometry(data.Provider),
                StatusBrush = Controls.ProviderStatusVisuals.Brush(data.ServiceStatus),
                IncidentVisibility = ProviderStatusPresentation.IsIncident(data.ServiceStatus) ? Visibility.Visible : Visibility.Collapsed,
                StatusDescription = StatusDetailCaption.Tooltip(data.ServiceStatus, nowUtc),
                SessionPercent = first?.Window.Percent ?? 0,
                WeeklyPercent = second?.Window.Percent ?? 0,
                SessionWindow = first?.Window,
                WeeklyWindow = second?.Window,
                ResetHost = _resetHost,
                SessionResetStamp = _resetGate.Resolve(data.Provider + "|first", first?.Window.ResetDetectedAtUtc, nowUtc),
                WeeklyResetStamp = _resetGate.Resolve(data.Provider + "|second", second?.Window.ResetDetectedAtUtc, nowUtc),
                SessionTimePercent = first?.Window.ElapsedPercent ?? -1,
                WeeklyTimePercent = second?.Window.ElapsedPercent ?? -1,
                SessionPace = first?.TooltipText ?? string.Empty,
                WeeklyPace = second?.TooltipText ?? string.Empty,
                SessionAccent = first?.Accent ?? WindowAccent.None,
                WeeklyAccent = second?.Accent ?? WindowAccent.None,
                SessionText = firstText,
                WeeklyText = secondText,
                SessionBrush = WarningBrush(first?.Window) ?? primary,
                WeeklyBrush = WarningBrush(second?.Window) ?? primary,
                ToolTip = $"{name}: {BarLabel(data, first)} {firstText}, {BarLabel(data, second)} {secondText}",
                NumberColumn = numberColumn
            });
        }

        InPlaceRow.Apply(Items, next, item => item.Name);

        Reposition();
    }

    /// <summary>
    /// Name of a bar for the tooltip: its own title for extra windows, otherwise the session/weekly label.
    /// </summary>
    private static string BarLabel(UsageData data, WidgetBar? bar) =>
        bar == null ? string.Empty
        : bar.Title ?? (ReferenceEquals(bar.Window, data.Session) ? data.SessionLabel : data.WeeklyLabel);

    private static string Cell(UsageWindow? window, bool isLoading) =>
        window?.PercentText ?? (isLoading ? "…" : "–");

    /// <summary>
    /// Same thresholds and colours as the usage meters: amber from 75%, red from 90% (UsageThresholds).
    /// </summary>
    private static SolidColorBrush? WarningBrush(UsageWindow? window) => window?.Percent switch
    {
        >= UsageThresholds.Critical => _criticalBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 232, 17, 35)),
        >= UsageThresholds.Warning => _warningBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 255, 185, 0)),
        _ => null
    };

    // Created on first use (on the UI thread) and shared by every rebuild, so an update in place keeps the same brush
    private static SolidColorBrush? _criticalBrush;
    private static SolidColorBrush? _warningBrush;
    private static SolidColorBrush? _lightPrimaryBrush;
    private static SolidColorBrush? _darkPrimaryBrush;
    private static SolidColorBrush LightPrimaryBrush => _lightPrimaryBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 32, 32, 32));
    private static SolidColorBrush DarkPrimaryBrush => _darkPrimaryBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 240, 240, 240));

    /// <summary>
    /// Places the strip over the bottom-left of the taskbar (or the screen's bottom-left if the
    /// taskbar isn't docked at the bottom). Only moves the window when something changed.
    /// </summary>
    private void Reposition()
    {
        var scale = WindowZOrder.GetScale(_hwnd);
        if (_taskbarBoundsStale)
        {
            _taskbarBounds = WindowZOrder.GetTaskbarBounds();
            _taskbarBoundsStale = false;
        }
        var bar = _taskbarBounds is { Edge: WindowZOrder.TaskbarEdge.Bottom } b ? b : ((int, int, int, int, WindowZOrder.TaskbarEdge)?)null;
        var screen = bar == null ? DisplayArea.Primary.OuterBounds : default;

        var contentChanged = _lastRepositionSignature != _lastSignature;
        var environmentChanged = scale != _lastScale ||
            !Equals(bar, _lastTaskbarBounds) ||
            (bar == null && (screen.X != _lastScreenBounds.X || screen.Y != _lastScreenBounds.Y || screen.Width != _lastScreenBounds.Width || screen.Height != _lastScreenBounds.Height));

        if (!contentChanged && !environmentChanged && _hasTarget)
        {
            // A plain Win32 query: this runs on every tick
            if (WindowZOrder.TryGetWindowRect(_hwnd, out var current) &&
                current.Left == _lastTarget.X && current.Top == _lastTarget.Y &&
                current.Right - current.Left == _lastTarget.Width && current.Bottom - current.Top == _lastTarget.Height)
            {
                return;
            }
            AppWindow.MoveAndResize(_lastTarget);
            return;
        }

        _lastTaskbarBounds = bar;
        _lastScreenBounds = screen;
        _lastRepositionSignature = _lastSignature;
        lock (_stateLock)
        {
            _lastScale = scale;
        }

        var count = Math.Max(1, Items.Count);

        // Rounded right end, square left end: DWM rounds all four corners or none, so when nothing is
        // to the left of the taskbar we push the window's left end just past the screen edge, where
        // its (rounded) left corners are off-screen, and add the same amount of left padding.
        var overhangDip = 0.0;
        if (bar is { } probe)
        {
            var probePoint = new PointInt32(probe.Item1 - 1, probe.Item2 + (probe.Item4 - probe.Item2) / 2);
            if (DisplayArea.GetFromPoint(probePoint, DisplayAreaFallback.None) == null)
            {
                overhangDip = OverhangDip;
            }
        }

        var rounded = overhangDip > 0;
        if (rounded != _roundedRightEnd)
        {
            _roundedRightEnd = rounded;
            if (rounded) WindowZOrder.UseSmallRoundedCorners(_hwnd); else WindowZOrder.DisableRoundedCorners(_hwnd);
        }

        RootGrid.Padding = new Thickness(SidePaddingDip + overhangDip, 0, SidePaddingDip, 0);

        // Never narrower than the content, so the right edge can't be clipped (e.g. after a display scale change)
        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var contentDip = Math.Max(
            count * ItemWidthDip + (count - 1) * ItemSpacingDip + HorizontalPaddingDip + overhangDip,
            RootGrid.DesiredSize.Width);
        var width = (int)Math.Ceiling((contentDip + ClipSlackDip) * scale);
        var overhang = (int)Math.Ceiling(overhangDip * scale);

        // Flush with the taskbar's left and bottom edges, but starting just below the thin line that
        // runs along the taskbar's top edge so the strip never covers it
        var topLine = (int)Math.Ceiling(TopLineDip * scale);
        RectInt32 target;
        if (bar is { } taskbar)
        {
            target = new RectInt32(taskbar.Item1 - overhang, taskbar.Item2 + topLine, width,
                Math.Max(1, taskbar.Item4 - taskbar.Item2 - topLine));
        }
        else
        {
            var height = (int)(FallbackHeightDip * scale);
            target = new RectInt32(screen.X, screen.Y + screen.Height - height, width, height);
        }

        lock (_stateLock)
        {
            _lastTarget = target;
            _hasTarget = true;
        }

        var currentPos = AppWindow.Position;
        var currentSize = AppWindow.Size;
        if (currentPos.X != target.X || currentPos.Y != target.Y || currentSize.Width != target.Width || currentSize.Height != target.Height)
        {
            AppWindow.MoveAndResize(target);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed)
        {
            OpenDetailsRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnStripAutomationInvoked(object? sender, EventArgs e)
    {
        try
        {
            if (_closed) return;
            OpenDetailsRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private void OnOpenDetailsClick(object sender, RoutedEventArgs e)
    {
        OpenDetailsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenDashboardClick(object sender, RoutedEventArgs e)
    {
        OpenDashboardRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
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
        _taskbarChangeWatcher?.Dispose();
        _taskbarChangeWatcher = null;
        RootGrid.AutomationInvoked -= OnStripAutomationInvoked;
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        _acrylicController?.Dispose();
        _acrylicController = null;
        App.Windows.Remove(this);
    }
}
