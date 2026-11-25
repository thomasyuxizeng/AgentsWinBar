using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.ViewModels;
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

namespace CodexBarWin;

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
    private readonly DispatcherTimer _keepOnTopTimer;
    private int _tickCount;
    private int _lockPollCount;
    private TaskbarChangeWatcher? _taskbarChangeWatcher;
    private bool _taskbarBoundsStale = true;
    private (int Left, int Top, int Right, int Bottom, WindowZOrder.TaskbarEdge Edge)? _taskbarBounds;
    private bool _roundedRightEnd;
    private bool _hiddenForFullscreen;
    private bool _wasLocked;
    private bool _shown;
    private bool _closed;
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

        // The taskbar re-raises itself when clicked, so keep reasserting our position and z-order.
        _keepOnTopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _keepOnTopTimer.Tick += (_, _) =>
        {
            if (_closed || (!AppWindow.IsVisible && !_hiddenForFullscreen))
            {
                _keepOnTopTimer.Stop();
                return;
            }

            // Unlock events don't reliably reach the app, so a locked screen slows the timer down instead of stopping it
            var pollLock = _wasLocked || ++_lockPollCount % LockPollEveryTicks == 0;
            if (pollLock && _sessionState.IsLocked)
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
            }
            if (_tickCount % TaskbarPollEveryTicks == 0 || _taskbarChangeWatcher is not { IsActive: true }) _taskbarBoundsStale = true;

            Reposition();
            WindowZOrder.AssertTopmostIfCovered(_hwnd);
        };

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
            if (!_hiddenForFullscreen && !_sessionState.IsLocked && AppWindow.IsVisible)
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
        !_shown || (!_closed && AppWindow.IsVisible && !_hiddenForFullscreen && !_sessionState.IsLocked && _displayState.IsOn);

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
        if (_keepOnTopTimer.Interval != interval) _keepOnTopTimer.Interval = interval;
        if (!_keepOnTopTimer.IsEnabled) _keepOnTopTimer.Start();
    }

    /// <summary>
    /// Picks the strip's upkeep back up once the session is unlocked (or has resumed).
    /// </summary>
    private void ResumeAfterLock()
    {
        if (_closed || _sessionState.IsLocked) return;
        if (!_hiddenForFullscreen && !AppWindow.IsVisible) return;   // hidden on purpose; ShowStrip restarts the timer

        _wasLocked = false;
        UpdateFullscreenVisibility();
        if (_hiddenForFullscreen)
        {
            SetTickInterval(HiddenTick);
            return;
        }

        _tickCount = 0;
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
                _tickCount = 0;
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
                if (_closed || !_keepOnTopTimer.IsEnabled) return;
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

        WindowZOrder.HideFromSwitchers(AppWindow, _hwnd);
        AppWindow.Title = "CodexBarWin taskbar display";

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

    internal static int RoundTimePercent(double? elapsed)
    {
        if (elapsed is not { } value || !double.IsFinite(value) || value < 0)
        {
            return -1;
        }
        return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
    }

    private static int WarningBrushKind(UsageWindow? window) => window?.Percent switch
    {
        >= UsageThresholds.Critical => 2,
        >= UsageThresholds.Warning => 1,
        _ => 0
    };

    internal static string ComputeSignature(IEnumerable<UsageData> providers, bool isLight, DateTime nowUtc,
        Func<string, DateTime?, DateTime?>? resetStamp = null)
    {
        DateTime? Stamp(string bar, DateTime? detectedAtUtc) => resetStamp == null ? detectedAtUtc : resetStamp(bar, detectedAtUtc);
        var sb = new System.Text.StringBuilder();
        sb.Append(isLight ? 'L' : 'D');
        foreach (var data in providers)
        {
            var bars = WidgetBarSelector.Pick(data, nowUtc);
            var first = bars.ElementAtOrDefault(0);
            var second = bars.ElementAtOrDefault(1);
            var firstText = Cell(first?.Window, data.IsLoading);
            var secondText = Cell(second?.Window, data.IsLoading);

            sb.Append('|').Append(data.Provider)
              .Append(';').Append(data.IsLoading)
              .Append(';').Append(data.ServiceStatus?.Indicator)
              .Append(';').Append(StatusDetailCaption.Tooltip(data.ServiceStatus, nowUtc))
              .Append(';').Append(first?.Window.Percent ?? 0)
              .Append(';').Append(firstText)
              .Append(';').Append(RoundTimePercent(first?.Window.ElapsedPercent))
              .Append(';').Append(first?.TooltipText)
              .Append(';').Append(first?.Accent)
              .Append(';').Append(first?.Window.ResetFromUsedPercent)
              .Append(';').Append(Stamp(data.Provider + "|first", first?.Window.ResetDetectedAtUtc)?.Ticks)
              .Append(';').Append(BarLabel(data, first))
              .Append(';').Append(WarningBrushKind(first?.Window))
              .Append(';').Append(second?.Window.Percent ?? 0)
              .Append(';').Append(secondText)
              .Append(';').Append(RoundTimePercent(second?.Window.ElapsedPercent))
              .Append(';').Append(second?.TooltipText)
              .Append(';').Append(second?.Accent)
              .Append(';').Append(second?.Window.ResetFromUsedPercent)
              .Append(';').Append(Stamp(data.Provider + "|second", second?.Window.ResetDetectedAtUtc)?.Ticks)
              .Append(';').Append(BarLabel(data, second))
              .Append(';').Append(WarningBrushKind(second?.Window));
        }
        return sb.ToString();
    }

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
        var numberColumn = new GridLength(Math.Ceiling(OverlayWindow.MeasureNumber("100%")) + 1);

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

        _lastScale = scale;
        _lastTaskbarBounds = bar;
        _lastScreenBounds = screen;
        _lastRepositionSignature = _lastSignature;

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

        _lastTarget = target;
        _hasTarget = true;

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
        _keepOnTopTimer.Stop();
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
