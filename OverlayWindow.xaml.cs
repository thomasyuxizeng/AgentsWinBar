using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
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
    // Spacing (DIP): 12 horizontal and 8 vertical padding + 1 border on every side, uniform 8 between
    // columns. The width is worked out in Rebuild() from the actual number widths; ApplySize() also
    // grows the window to the measured content if that is ever larger.
    private double _contentWidthDip = 214;
    private const double VerticalPaddingDip = 8 * 2 + 2;
    private const double ScreenMarginDip = 16;
    private const double ClipSlackDip = 2;

    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _topmostTimer;
    private int _tickCount;
    private bool _hiddenForFullscreen;
    private WindowZOrder.ForegroundWatcher? _foregroundWatcher;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;

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
        _hwnd = WindowNative.GetWindowHandle(this);

        ConfigureWindow();
        SetupAcrylicBackdrop();

        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        Rebuild();
        PositionWindow();

        // Reassert topmost periodically as a safety net; the foreground watcher handles the immediate cases.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) =>
        {
            UpdateFullscreenVisibility();
            WindowZOrder.AssertTopmost(_hwnd);
            ApplySize();   // follows display scale changes (e.g. remote desktop)

            // Time keeps passing between data refreshes, so refresh the time layer about every 30s
            if (++_tickCount % 15 == 0)
            {
                Rebuild();
            }
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
            UpdateFullscreenVisibility();
            WindowZOrder.AssertTopmost(_hwnd);
        });
        _topmostTimer.Start();
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
        }
        else if (!fullscreen && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            AppWindow.Show(false);
        }
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

    private void Rebuild()
    {
        var rows = _viewModel.Providers.Select(ToRow).ToList();

        // Number columns are sized from the actual text, with two digits ("88%") as the typical minimum,
        // so 1-2 digit values sit snugly next to their bars and "%" signs line up; a rare "100%" just
        // widens the columns (and the window) for as long as it is shown.
        var typical = MeasureNumber("88%");
        var sessionWidth = Math.Ceiling(Math.Max(typical, rows.Select(r => MeasureNumber(r.SessionText)).DefaultIfEmpty(0).Max())) + 1;
        var weeklyWidth = Math.Ceiling(Math.Max(typical, rows.Select(r => MeasureNumber(r.WeeklyText)).DefaultIfEmpty(0).Max())) + 1;

        Rows.Clear();
        foreach (var row in rows)
        {
            row.SessionNumberColumn = new GridLength(sessionWidth);
            row.WeeklyNumberColumn = new GridLength(weeklyWidth);
            Rows.Add(row);
        }

        // 12 padding + 1 border per side, 4 gaps of 8; columns = icon + bar + number + bar + number
        _contentWidthDip = 2 * (12 + 1) + (16 + 44 + sessionWidth + 44 + weeklyWidth) + 4 * 8;

        ApplySize();
        ClampToScreen();
    }

    private static double MeasureNumber(string text)
    {
        var block = new Microsoft.UI.Xaml.Controls.TextBlock { Text = text, FontSize = 11 };
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    private static OverlayRow ToRow(UsageData data)
    {
        // The two most relevant bars: normally session + weekly, but a more relevant window
        // (at/near its limit, ahead of schedule, or recently changed) can take a slot
        var bars = WidgetBarSelector.Pick(data, DateTime.UtcNow);
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
            StatusDescription = ProviderStatusPresentation.Description(data.ServiceStatus),
            SessionPercent = firstCell.Percent,
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
        var rows = Math.Max(1, Rows.Count);

        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var measured = RootGrid.DesiredSize;

        var widthDip = Math.Max(_contentWidthDip, measured.Width) + ClipSlackDip;
        var heightDip = Math.Max(rows * RowHeightDip + VerticalPaddingDip, measured.Height);

        var target = new SizeInt32((int)Math.Ceiling(widthDip * scale), (int)Math.Ceiling(heightDip * scale));
        var current = AppWindow.Size;
        if (current.Width != target.Width || current.Height != target.Height)
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
        _topmostTimer.Stop();
        _foregroundWatcher?.Dispose();
        _foregroundWatcher = null;
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        _acrylicController?.Dispose();
        _acrylicController = null;
        App.Windows.Remove(this);
    }
}
