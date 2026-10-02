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
    public double SessionPercent { get; set; }
    public string SessionText { get; set; } = string.Empty;
    public double WeeklyPercent { get; set; }
    public string WeeklyText { get; set; } = string.Empty;
    public double RowHeight => OverlayWindow.RowHeightDip;
}

/// <summary>
/// Minimal always-on-top usage readout. Never takes focus, can be dragged,
/// click opens the full flyout, right-click for the menu.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    internal const double RowHeightDip = 22;
    private const double WidthDip = 274;
    private const double VerticalPaddingDip = 20;
    private const double ScreenMarginDip = 16;

    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _topmostTimer;
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
        _topmostTimer.Tick += (_, _) => WindowZOrder.AssertTopmost(_hwnd);

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

        _foregroundWatcher ??= new WindowZOrder.ForegroundWatcher(() => WindowZOrder.AssertTopmost(_hwnd));
        _topmostTimer.Start();
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
        AppWindow.Title = string.Empty;

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
        Rows.Clear();
        foreach (var data in _viewModel.Providers)
        {
            Rows.Add(ToRow(data));
        }

        ApplySize();
        ClampToScreen();
    }

    private static OverlayRow ToRow(UsageData data)
    {
        var name = data.Provider.Length == 0
            ? "?"
            : char.ToUpperInvariant(data.Provider[0]) + data.Provider[1..];

        (double Percent, string Text) Cell(UsageWindow? window) =>
            window != null ? (window.Percent, window.PercentText) : (0, data.IsLoading ? "…" : "–");

        var session = Cell(data.Session);
        var weekly = Cell(data.Weekly);
        return new OverlayRow
        {
            Name = name,
            SessionPercent = session.Percent,
            SessionText = session.Text,
            WeeklyPercent = weekly.Percent,
            WeeklyText = weekly.Text
        };
    }

    private void ApplySize()
    {
        var scale = WindowZOrder.GetScale(_hwnd);
        var rows = Math.Max(1, Rows.Count);
        AppWindow.Resize(new SizeInt32(
            (int)(WidthDip * scale),
            (int)((rows * RowHeightDip + VerticalPaddingDip) * scale)));
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
