using System.Diagnostics;
using CodexBarWin.Helpers;
using CodexBarWin.Services;
using CodexBarWin.ViewModels;
using CodexBarWin.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace CodexBarWin;

/// <summary>
/// The full-window dashboard: resizable, maximizable, full-screen capable, showing everything the app knows
/// at once. It listens to the main view model for new results and redraws from a fresh snapshot.
/// </summary>
public sealed partial class DashboardAppWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IDashboardService _dashboardService;
    private readonly ILogger<DashboardAppWindow> _logger;
    private readonly DashboardPage _page = new();
    private readonly DispatcherQueueTimer _redrawTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly AppWindow _appWindow;
    private bool _closed;

    public DashboardAppWindow()
    {
        InitializeComponent();

        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _dashboardService = App.Services.GetRequiredService<IDashboardService>();
        _logger = App.Services.GetRequiredService<ILogger<DashboardAppWindow>>();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindow = AppWindow;
        if (_appWindow.TitleBar != null)
        {
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        SizeAndCenter(1360, 880);
        SetupMicaBackdrop();

        PageHost.Content = _page;
        App.Windows.Add(this);
        App.ApplyTheme();
        UpdateWindowIcon();

        _page.RefreshRequested += OnRefreshRequested;
        _page.SettingsRequested += OnSettingsRequested;
        _page.TopmostChanged += OnTopmostChanged;
        _page.FullScreenChanged += OnFullScreenChanged;

        // Redraw shortly after new data arrives (several providers can land in quick succession).
        // The clock only re-evaluates relative-time bindings on existing controls.
        var queue = DispatcherQueue.GetForCurrentThread();
        _redrawTimer = queue.CreateTimer();
        _redrawTimer.Interval = TimeSpan.FromMilliseconds(250);
        _redrawTimer.IsRepeating = false;
        _redrawTimer.Tick += (_, _) => Redraw();
        _clockTimer = queue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(30);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += (_, _) =>
        {
            try { if (!_closed) _page.RefreshTimes(); }
            catch (Exception ex) { _logger.LogError(ex, "Dashboard clock update failed"); }
        };
        _clockTimer.Start();

        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
        _appWindow.Changed += OnAppWindowChanged;

        if (Content is UIElement root)
        {
            var f11 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F11 };
            f11.Invoked += (_, e) => { ToggleFullScreen(); e.Handled = true; };
            root.KeyboardAccelerators.Add(f11);
            var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            escape.Invoked += (_, e) => { if (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) { ToggleFullScreen(); e.Handled = true; } };
            root.KeyboardAccelerators.Add(escape);
        }

        _page.SetBusy(_viewModel.IsLoading);
        Redraw();

        // CLI version and similar facts come from WSL; fetch them in the background and redraw when known.
        RefreshDiagnosticsAsync().SafeFireAndForget(onError: ex => _logger.LogDebug(ex, "Dashboard diagnostics refresh failed"));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Sizes the window in device-independent units (so it looks the same at any display scale) and centres it on its display.</summary>
    private void SizeAndCenter(int widthDip, int heightDip)
    {
        var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)(widthDip * scale), area.Width);
        var height = Math.Min((int)(heightDip * scale), area.Height);
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private async Task RefreshDiagnosticsAsync()
    {
        await _dashboardService.RefreshDiagnosticsAsync();
        if (!_closed) DispatcherQueue.TryEnqueue(Redraw);
    }

    private void Redraw()
    {
        if (_closed) return;
        try
        {
            var lastRefreshUtc = _viewModel.LastUpdated == default ? (DateTime?)null : _viewModel.LastUpdated.ToUniversalTime();
            var timing = _logger.IsEnabled(LogLevel.Debug);
            var start = timing ? Stopwatch.GetTimestamp() : 0;
            var snapshot = _dashboardService.GetSnapshot(_viewModel.Providers.ToList(), lastRefreshUtc);
            var snapshotEnd = timing ? Stopwatch.GetTimestamp() : 0;
            _page.Update(snapshot);
            if (timing) _logger.LogDebug("Dashboard redraw: GetSnapshot {SnapshotMs:F2} ms, Page.Update {UpdateMs:F2} ms",
                Stopwatch.GetElapsedTime(start, snapshotEnd).TotalMilliseconds,
                Stopwatch.GetElapsedTime(snapshotEnd).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard redraw failed");
        }
    }

    private void OnProvidersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => { if (!_closed) { _redrawTimer.Stop(); _redrawTimer.Start(); } });

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.IsLoading) or nameof(MainViewModel.LastUpdated))) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _page.SetBusy(_viewModel.IsLoading);
            if (e.PropertyName == nameof(MainViewModel.IsLoading)) return;
            _redrawTimer.Stop();
            _redrawTimer.Start();
        });
    }

    private void OnRefreshRequested(object? sender, EventArgs e) =>
        _viewModel.RefreshCommand.ExecuteAsync(null).SafeFireAndForget(
            onError: ex => _logger.LogError(ex, "Dashboard refresh failed"));

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        try
        {
            var settings = App.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? new SettingsWindow();
            settings.Activate();
        }
        catch (Exception ex) { _logger.LogError(ex, "Opening Settings failed"); }
    }

    private void OnTopmostChanged(object? sender, bool on)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = on;
    }

    private void OnFullScreenChanged(object? sender, bool on) => SetFullScreen(on);

    private void ToggleFullScreen() => SetFullScreen(_appWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);

    private void SetFullScreen(bool on)
    {
        _appWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        _page.SetFullScreen(on);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange) _page.SetFullScreen(sender.Presenter.Kind == AppWindowPresenterKind.FullScreen);
    }

    private void SetupMicaBackdrop()
    {
        if (!MicaController.IsSupported()) return;
        _micaController = new MicaController { Kind = MicaKind.Base };
        _backdropConfiguration = new SystemBackdropConfiguration { IsInputActive = true };
        UpdateBackdropTheme();
        _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _micaController.AddSystemBackdropTarget(
            WinRT.CastExtensions.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>(this));
        if (Content is FrameworkElement root) root.ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateBackdropTheme();
        UpdateWindowIcon();
    }

    private void UpdateBackdropTheme()
    {
        if (_backdropConfiguration == null) return;
        _backdropConfiguration.Theme = Content is FrameworkElement root
            ? root.ActualTheme switch
            {
                ElementTheme.Light => SystemBackdropTheme.Light,
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default
            }
            : SystemBackdropTheme.Default;
    }

    private void UpdateWindowIcon()
    {
        var light = Content is FrameworkElement { ActualTheme: ElementTheme.Light };
        _appWindow.SetIcon($"Assets/{(light ? "app-dark.ico" : "app.ico")}");
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _redrawTimer.Stop();
        _clockTimer.Stop();
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _appWindow.Changed -= OnAppWindowChanged;
        if (Content is FrameworkElement root) root.ActualThemeChanged -= OnActualThemeChanged;
        App.Windows.Remove(this);
        _micaController?.Dispose();
    }
}
