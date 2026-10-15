using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBarWin;

/// <summary>
/// One provider in the taskbar strip.
/// </summary>
public sealed class TaskbarItem
{
    public string Name { get; set; } = string.Empty;
    public Microsoft.UI.Xaml.Media.Brush StatusBrush { get; set; } = null!;
    public Visibility IncidentVisibility { get; set; }
    public string StatusDescription { get; set; } = string.Empty;
    public Geometry Icon { get; set; } = null!;
    public double SessionPercent { get; set; }
    public double WeeklyPercent { get; set; }
    /// <summary>The selected windows, including their session-only ResetDetectedAtUtc signals.</summary>
    public UsageWindow? SessionWindow { get; set; }
    public UsageWindow? WeeklyWindow { get; set; }
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
}

/// <summary>
/// Compact usage strip shown on the taskbar, bottom-left corner. Never takes focus;
/// click opens the flyout, right-click for the menu.
/// </summary>
public sealed partial class TaskbarWindow : Window
{
    // Item = 16 icon + 10 gap + 56 bar + 8 gap + 22 number (a typical 1-2 digit value; the number column
    // sizes itself to its text, so a rare "100%" just widens it); strip = 18 padding per side, no border.
    // These give a lower bound only: Reposition() grows the strip to the measured content if needed.
    // The strip sits flush: full taskbar height, starting at the taskbar's left edge.
    private const double ClipSlackDip = 2;
    private const double ItemWidthDip = 112;
    private const double ItemSpacingDip = 24;
    private const double SidePaddingDip = 18;
    private const double HorizontalPaddingDip = 2 * SidePaddingDip;
    private const double OverhangDip = 12;   // how far the left end is pushed off-screen to hide its rounded corners
    private const double FallbackHeightDip = 48;
    private const double TopLineDip = 1;   // the thin line along the taskbar's top edge

    private readonly MainViewModel _viewModel;
    private readonly ISessionStateSource _sessionState;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _keepOnTopTimer;
    private int _tickCount;
    private bool _roundedRightEnd;
    private bool _hiddenForFullscreen;
    private bool _closed;
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
    /// Raised when the user picks "Hide taskbar display".
    /// </summary>
    public event EventHandler? HideRequested;

    public TaskbarWindow()
    {
        InitializeComponent();

        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _sessionState = App.Services.GetRequiredService<ISessionStateSource>();
        _hwnd = WindowNative.GetWindowHandle(this);

        ConfigureWindow();
        SetupAcrylicBackdrop();

        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        RootGrid.ActualThemeChanged += (_, _) => Rebuild();
        _sessionState.Locked += OnSessionLocked;
        _sessionState.Unlocked += OnSessionUnlocked;
        _sessionState.Resumed += OnSessionUnlocked;
        Rebuild();

        // The taskbar re-raises itself when clicked, so keep reasserting our position and z-order.
        _keepOnTopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _keepOnTopTimer.Tick += (_, _) =>
        {
            if (_closed || !AppWindow.IsVisible || _sessionState.IsLocked)
            {
                _keepOnTopTimer.Stop();
                return;
            }

            UpdateFullscreenVisibility();
            if (_hiddenForFullscreen) return;

            // Time keeps passing between data refreshes, so refresh the time layer about every 30s
            if (++_tickCount % 15 == 0)
            {
                Rebuild();
            }

            Reposition();
            WindowZOrder.AssertTopmost(_hwnd);
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

        Reposition();
        WindowZOrder.AssertTopmost(_hwnd);

        _foregroundWatcher ??= new WindowZOrder.ForegroundWatcher(() =>
        {
            UpdateFullscreenVisibility();
            if (!_hiddenForFullscreen && !_sessionState.IsLocked && AppWindow.IsVisible)
            {
                Reposition();
                WindowZOrder.AssertTopmost(_hwnd);
            }
        });
        if (!_sessionState.IsLocked)
        {
            _keepOnTopTimer.Start();
        }
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
            _keepOnTopTimer.Stop();
        }
        else if (!fullscreen && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            AppWindow.Show(false);
            if (!_closed && !_sessionState.IsLocked)
            {
                _tickCount = 0;
                Rebuild();
                Reposition();
                WindowZOrder.AssertTopmost(_hwnd);
                _keepOnTopTimer.Start();
            }
        }
    }

    private void OnSessionLocked(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _keepOnTopTimer.Stop();
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
                if (_closed || !AppWindow.IsVisible || _sessionState.IsLocked) return;

                UpdateFullscreenVisibility();
                if (_hiddenForFullscreen) return;

                _tickCount = 0;
                Rebuild();
                Reposition();
                WindowZOrder.AssertTopmost(_hwnd);

                _keepOnTopTimer.Start();
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

    internal static string ComputeSignature(IEnumerable<UsageData> providers, bool isLight, DateTime nowUtc)
    {
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
              .Append(';').Append(first?.Window.ResetDetectedAtUtc?.Ticks)
              .Append(';').Append(BarLabel(data, first))
              .Append(';').Append(WarningBrushKind(first?.Window))
              .Append(';').Append(second?.Window.Percent ?? 0)
              .Append(';').Append(secondText)
              .Append(';').Append(RoundTimePercent(second?.Window.ElapsedPercent))
              .Append(';').Append(second?.TooltipText)
              .Append(';').Append(second?.Accent)
              .Append(';').Append(second?.Window.ResetFromUsedPercent)
              .Append(';').Append(second?.Window.ResetDetectedAtUtc?.Ticks)
              .Append(';').Append(BarLabel(data, second))
              .Append(';').Append(WarningBrushKind(second?.Window));
        }
        return sb.ToString();
    }

    private void Rebuild()
    {
        var isLight = RootGrid.ActualTheme == ElementTheme.Light;
        var nowUtc = DateTime.UtcNow;
        var signature = ComputeSignature(_viewModel.Providers, isLight, nowUtc);
        if (_lastSignature != null && _lastSignature == signature)
        {
            return;
        }
        _lastSignature = signature;

        var primary = new SolidColorBrush(isLight ? ColorHelper.FromArgb(255, 32, 32, 32) : ColorHelper.FromArgb(255, 240, 240, 240));

        Items.Clear();
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

            Items.Add(new TaskbarItem
            {
                Name = name,
                Icon = ProviderIcons.GetGeometry(data.Provider),
                StatusBrush = Controls.ProviderStatusVisuals.Brush(data.ServiceStatus),
                IncidentVisibility = ProviderStatusPresentation.IsIncident(data.ServiceStatus) ? Visibility.Visible : Visibility.Collapsed,
                StatusDescription = StatusDetailCaption.Tooltip(data.ServiceStatus, nowUtc),
                SessionPercent = first?.Window.Percent ?? 0,
                WeeklyPercent = second?.Window.Percent ?? 0,
                SessionWindow = first?.Window,
                WeeklyWindow = second?.Window,
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
                ToolTip = $"{name}: {BarLabel(data, first)} {firstText}, {BarLabel(data, second)} {secondText}"
            });
        }

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
        >= UsageThresholds.Critical => new SolidColorBrush(ColorHelper.FromArgb(255, 232, 17, 35)),
        >= UsageThresholds.Warning => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 185, 0)),
        _ => null
    };

    /// <summary>
    /// Places the strip over the bottom-left of the taskbar (or the screen's bottom-left if the
    /// taskbar isn't docked at the bottom). Only moves the window when something changed.
    /// </summary>
    private void Reposition()
    {
        var scale = WindowZOrder.GetScale(_hwnd);
        var bar = WindowZOrder.GetTaskbarBounds() is { Edge: WindowZOrder.TaskbarEdge.Bottom } b ? b : ((int, int, int, int, WindowZOrder.TaskbarEdge)?)null;
        var screen = bar == null ? DisplayArea.Primary.OuterBounds : default;

        var contentChanged = _lastRepositionSignature != _lastSignature;
        var environmentChanged = scale != _lastScale ||
            !Equals(bar, _lastTaskbarBounds) ||
            (bar == null && (screen.X != _lastScreenBounds.X || screen.Y != _lastScreenBounds.Y || screen.Width != _lastScreenBounds.Width || screen.Height != _lastScreenBounds.Height));

        if (!contentChanged && !environmentChanged && _hasTarget)
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            if (position.X == _lastTarget.X && position.Y == _lastTarget.Y &&
                size.Width == _lastTarget.Width && size.Height == _lastTarget.Height)
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

    private void OnOpenDetailsClick(object sender, RoutedEventArgs e)
    {
        OpenDetailsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _keepOnTopTimer.Stop();
        _sessionState.Locked -= OnSessionLocked;
        _sessionState.Unlocked -= OnSessionUnlocked;
        _sessionState.Resumed -= OnSessionUnlocked;
        _foregroundWatcher?.Dispose();
        _foregroundWatcher = null;
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        _acrylicController?.Dispose();
        _acrylicController = null;
        App.Windows.Remove(this);
    }
}
