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
    private readonly ISettingsService _settings;
    private readonly ILogger<DashboardAppWindow> _logger;
    private DashboardPage? _page = new();
    private readonly DispatcherQueueTimer _redrawTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly AppWindow _appWindow;
    private volatile bool _closed;
    private bool _redrawing;
    /// <summary>True while the window is minimised or hidden: nothing is drawn and no timer runs.</summary>
    private bool _hidden;
    private bool _topmost;
    private IDisposable? _costDemand;
    private IDisposable? _visibility;
    private readonly List<KeyboardAccelerator> _accelerators = [];

    public DashboardAppWindow()
    {
        InitializeComponent();

        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _dashboardService = App.Services.GetRequiredService<IDashboardService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
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
        _page.GraphVisibilityChanged += OnGraphVisibilityChanged;
        _settings.SettingsChanged += OnSettingsChanged;

        // Redraw shortly after new data arrives (several providers can land in quick succession).
        // Every 30 seconds: take a fresh snapshot (the page skips whatever did not change and just refreshes the
        // relative times), so nothing the change notifications missed stays stale.
        var queue = DispatcherQueue.GetForCurrentThread();
        _redrawTimer = queue.CreateTimer();
        _redrawTimer.Interval = TimeSpan.FromMilliseconds(250);
        _redrawTimer.IsRepeating = false;
        _redrawTimer.Tick += OnTimerTick;
        _clockTimer = queue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(30);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += OnTimerTick;
        _clockTimer.Start();

        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
        Activated += OnActivated;
        _appWindow.Changed += OnAppWindowChanged;

        if (Content is UIElement root)
        {
            // Window-wide shortcuts: without this WinUI shows the key as a tooltip on whatever is hovered
            root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
            var f11 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F11 };
            f11.Invoked += OnF11Invoked;
            _accelerators.Add(f11);
            root.KeyboardAccelerators.Add(f11);
            var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            escape.Invoked += OnEscapeInvoked;
            _accelerators.Add(escape);
            root.KeyboardAccelerators.Add(escape);
        }

        _page.SetBusy(_viewModel.IsLoading);
        Redraw();
        _costDemand = _viewModel.AcquireCostDemand();

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
        Post(Redraw);
    }

    private void Redraw()
    {
        // Nobody sees a locked screen; the 30-second clock redraws once it is unlocked (a cheap query, not a snapshot)
        if (_closed || _redrawing || _hidden || _page == null || SessionStateNative.IsCurrentSessionLocked()) return;
        _redrawing = true;
        try
        {
            var lastRefreshUtc = _viewModel.LastUpdated == default ? (DateTime?)null : _viewModel.LastUpdated.ToUniversalTime();
            var timing = _logger.IsEnabled(LogLevel.Debug);
            var start = timing ? Stopwatch.GetTimestamp() : 0;
            var snapshot = _dashboardService.GetSnapshot(_viewModel.Providers.ToList(), lastRefreshUtc,
                _viewModel.NextRefreshUtc, _viewModel.EffectiveRefreshInterval);
            var snapshotEnd = timing ? Stopwatch.GetTimestamp() : 0;
            _page.SetHiddenGraphs(GraphVisibility.HiddenIds(_settings.Settings.Providers));
            _page.Update(snapshot);
            if (timing) _logger.LogDebug("Dashboard redraw: GetSnapshot {SnapshotMs:F2} ms, Page.Update {UpdateMs:F2} ms",
                Stopwatch.GetElapsedTime(start, snapshotEnd).TotalMilliseconds,
                Stopwatch.GetElapsedTime(snapshotEnd).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard redraw failed");
        }
        finally { _redrawing = false; }
    }

    private void OnProvidersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Post(() => { _redrawTimer.Stop(); _redrawTimer.Start(); });

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.IsLoading) or nameof(MainViewModel.LastUpdated)
            or nameof(MainViewModel.NextRefreshUtc) or nameof(MainViewModel.EffectiveRefreshInterval))) return;
        Post(() =>
        {
            _page?.SetBusy(_viewModel.IsLoading);
            if (e.PropertyName == nameof(MainViewModel.IsLoading)) return;
            _redrawTimer.Stop();
            _redrawTimer.Start();
        });
    }

    private void OnRefreshRequested(object? sender, EventArgs e) => HandleEvent(() =>
        _viewModel.RefreshCommand.ExecuteAsync(null).SafeFireAndForget(
            onError: ex => _logger.LogError(ex, "Dashboard refresh failed")));

    private void OnSettingsRequested(object? sender, EventArgs e) => HandleEvent(() =>
    {
        var settings = App.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? new SettingsWindow();
        settings.Activate();
    });

    /// <summary>A graphs toggle on the dashboard: kept in the provider's settings (quietly, nothing needs a data refresh).</summary>
    private void OnGraphVisibilityChanged(object? sender, GraphVisibilityChange change) => HandleEvent(() =>
    {
        if (!GraphVisibility.TrySet(_settings.Settings.Providers, change.ProviderId, change.Visible)) return;
        _settings.SaveQuietAsync().SafeFireAndForget(onError: ex => _logger.LogWarning(ex, "Saving the graphs setting failed"));
    });

    /// <summary>Settings were saved elsewhere (the Settings window): pick up the graphs choices.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e) => Post(Redraw);

    private void OnTopmostChanged(object? sender, bool on) => HandleEvent(() =>
    {
        _topmost = on;
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = on;
    });

    private void OnFullScreenChanged(object? sender, bool on) => HandleEvent(() => SetFullScreen(on));

    private void ToggleFullScreen() => SetFullScreen(_appWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);

    private void SetFullScreen(bool on)
    {
        _appWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = _topmost;
        _page?.SetFullScreen(on);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => HandleEvent(() =>
    {
        if (args.DidPresenterChange) _page?.SetFullScreen(sender.Presenter.Kind == AppWindowPresenterKind.FullScreen);
        UpdateVisibility();
    });

    private void OnActivated(object sender, WindowActivatedEventArgs args) => HandleEvent(() =>
    {
        // Opening or restoring refreshes stale data; plain re-activation (alt-tab) must not wake WSL again
        UpdateVisibility();
    });

    private bool UpdateVisibility()
    {
        var visible = _appWindow.IsVisible && _appWindow.Presenter is not
            OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (visible && _visibility == null)
        {
            _visibility = _viewModel.AcquireDashboardVisibility();
            // Back from the taskbar: take up the cost demand and the 30-second clock again, and catch the display up
            _costDemand ??= _viewModel.AcquireCostDemand();
            var wasHidden = _hidden;
            _hidden = false;
            _clockTimer.Start();
            if (wasHidden) Redraw();
            RefreshIfStale();
            return true;
        }
        if (!visible)
        {
            _visibility?.Dispose();
            _visibility = null;
            // A minimised dashboard shows nothing: stop its clock and release its cost demand so it cannot keep
            // the WSL VM busy with cost scans or redraw behind the user's back.
            _hidden = true;
            _clockTimer.Stop();
            _redrawTimer.Stop();
            _costDemand?.Dispose();
            _costDemand = null;
        }
        return false;
    }

    private void RefreshIfStale() =>
        _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2)).SafeFireAndForget(
            onError: ex => _logger.LogError(ex, "Dashboard stale refresh failed"));

    private void Post(Action action) => HandleEvent(() =>
        DispatcherQueue.TryEnqueue(() => HandleEvent(action)));

    private void HandleEvent(Action action)
    {
        if (_closed) return;
        try { action(); }
        catch (Exception ex) { _logger.LogError(ex, "Dashboard event failed"); }
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args) => Redraw();

    private void OnF11Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => HandleEvent(() =>
    {
        ToggleFullScreen();
        args.Handled = true;
    });

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => HandleEvent(() =>
    {
        if (_appWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen) return;
        ToggleFullScreen();
        args.Handled = true;
    });

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

    private void OnActualThemeChanged(FrameworkElement sender, object args) => HandleEvent(() =>
    {
        UpdateBackdropTheme();
        UpdateWindowIcon();
    });

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
        if (_closed) return;
        _closed = true;
        Closed -= OnClosed;
        Activated -= OnActivated;
        _costDemand?.Dispose();
        _costDemand = null;
        _visibility?.Dispose();
        _visibility = null;
        _redrawTimer.Tick -= OnTimerTick;
        _clockTimer.Tick -= OnTimerTick;
        if (_page != null)
        {
            _page.RefreshRequested -= OnRefreshRequested;
            _page.SettingsRequested -= OnSettingsRequested;
            _page.TopmostChanged -= OnTopmostChanged;
            _page.FullScreenChanged -= OnFullScreenChanged;
            _page.GraphVisibilityChanged -= OnGraphVisibilityChanged;
        }
        _settings.SettingsChanged -= OnSettingsChanged;
        foreach (var accelerator in _accelerators)
        {
            accelerator.Invoked -= OnF11Invoked;
            accelerator.Invoked -= OnEscapeInvoked;
        }
        _accelerators.Clear();
        try { _redrawTimer.Stop(); _clockTimer.Stop(); }
        catch (Exception ex) { _logger.LogError(ex, "Dashboard timer cleanup failed"); }
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        try { _page?.Cleanup(); }
        catch (Exception ex) { _logger.LogError(ex, "Dashboard page cleanup failed"); }
        _page = null;
        try { PageHost.Content = null; }
        catch (Exception ex) { _logger.LogError(ex, "Dashboard page host detach failed"); }
        _appWindow.Changed -= OnAppWindowChanged;
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged -= OnActualThemeChanged;
            if (root is UIElement rootUi) rootUi.KeyboardAccelerators.Clear();
        }
        try { Content = null; }
        catch (Exception ex) { _logger.LogError(ex, "Dashboard content detach failed"); }
        App.Windows.Remove(this);
        try
        {
            _micaController?.RemoveAllSystemBackdropTargets();
            _micaController?.Dispose();
        }
        catch (Exception ex) { _logger.LogError(ex, "Dashboard backdrop cleanup failed"); }
        _micaController = null;
        _backdropConfiguration = null;
    }
}
