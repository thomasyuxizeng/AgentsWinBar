using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
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
    public Geometry Icon { get; set; } = null!;
    public double SessionPercent { get; set; }
    public double WeeklyPercent { get; set; }
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
    // Item = 16 icon + 10 gap + 56 bar + 8 gap + 26 number; strip = 18 padding + 1 border on each side.
    // These give a lower bound only: Reposition() grows the strip to the measured content if needed.
    private const double ClipSlackDip = 2;
    private const double ItemWidthDip = 116;
    private const double ItemSpacingDip = 24;
    private const double HorizontalPaddingDip = 38;
    private const double LeftMarginDip = 8;
    private const double VerticalInsetDip = 4;
    private const double FallbackHeightDip = 40;

    private readonly MainViewModel _viewModel;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _keepOnTopTimer;
    private WindowZOrder.ForegroundWatcher? _foregroundWatcher;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;

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
        _hwnd = WindowNative.GetWindowHandle(this);

        ConfigureWindow();
        SetupAcrylicBackdrop();

        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        RootGrid.ActualThemeChanged += (_, _) => Rebuild();
        Rebuild();

        // The taskbar re-raises itself when clicked, so keep reasserting our position and z-order.
        _keepOnTopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _keepOnTopTimer.Tick += (_, _) =>
        {
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
            Reposition();
            WindowZOrder.AssertTopmost(_hwnd);
        });
        _keepOnTopTimer.Start();
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

    private void Rebuild()
    {
        var isLight = RootGrid.ActualTheme == ElementTheme.Light;
        var primary = new SolidColorBrush(isLight ? ColorHelper.FromArgb(255, 32, 32, 32) : ColorHelper.FromArgb(255, 240, 240, 240));

        Items.Clear();
        foreach (var data in _viewModel.Providers)
        {
            var name = ProviderIcons.GetDisplayName(data.Provider);
            var session = Cell(data.Session, data.IsLoading);
            var weekly = Cell(data.Weekly, data.IsLoading);

            Items.Add(new TaskbarItem
            {
                Name = name,
                Icon = ProviderIcons.GetGeometry(data.Provider),
                SessionPercent = data.Session?.Percent ?? 0,
                WeeklyPercent = data.Weekly?.Percent ?? 0,
                SessionText = session,
                WeeklyText = weekly,
                SessionBrush = WarningBrush(data.Session) ?? primary,
                WeeklyBrush = WarningBrush(data.Weekly) ?? primary,
                ToolTip = $"{name}: {data.SessionLabel} {session}, {data.WeeklyLabel} {weekly}"
            });
        }

        Reposition();
    }

    private static string Cell(UsageWindow? window, bool isLoading) =>
        window?.PercentText ?? (isLoading ? "…" : "–");

    /// <summary>
    /// Same thresholds and colours as the usage meters: amber from 80%, red from 95%.
    /// </summary>
    private static SolidColorBrush? WarningBrush(UsageWindow? window) => window?.Percent switch
    {
        >= 95 => new SolidColorBrush(ColorHelper.FromArgb(255, 232, 17, 35)),
        >= 80 => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 185, 0)),
        _ => null
    };

    /// <summary>
    /// Places the strip over the bottom-left of the taskbar (or the screen's bottom-left if the
    /// taskbar isn't docked at the bottom). Only moves the window when something changed.
    /// </summary>
    private void Reposition()
    {
        var scale = WindowZOrder.GetScale(_hwnd);
        var count = Math.Max(1, Items.Count);
        // Never narrower than the content, so the right edge can't be clipped (e.g. after a display scale change)
        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var contentDip = Math.Max(
            count * ItemWidthDip + (count - 1) * ItemSpacingDip + HorizontalPaddingDip,
            RootGrid.DesiredSize.Width);
        var width = (int)Math.Ceiling((contentDip + ClipSlackDip) * scale);
        var margin = (int)(LeftMarginDip * scale);
        var inset = (int)(VerticalInsetDip * scale);

        RectInt32 target;
        if (WindowZOrder.GetTaskbarBounds() is { Edge: WindowZOrder.TaskbarEdge.Bottom } bar)
        {
            target = new RectInt32(
                bar.Left + margin,
                bar.Top + inset,
                width,
                Math.Max(1, bar.Bottom - bar.Top - 2 * inset));
        }
        else
        {
            var screen = DisplayArea.Primary.OuterBounds;
            var height = (int)(FallbackHeightDip * scale);
            target = new RectInt32(screen.X + margin, screen.Y + screen.Height - height - inset, width, height);
        }

        var position = AppWindow.Position;
        var size = AppWindow.Size;
        if (position.X != target.X || position.Y != target.Y || size.Width != target.Width || size.Height != target.Height)
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
        _keepOnTopTimer.Stop();
        _foregroundWatcher?.Dispose();
        _foregroundWatcher = null;
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        _acrylicController?.Dispose();
        _acrylicController = null;
        App.Windows.Remove(this);
    }
}
