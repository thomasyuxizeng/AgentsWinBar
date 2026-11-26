using System.Diagnostics;
using AgentsWinBar.Helpers;
using AgentsWinBar.Services;
using AgentsWinBar.ViewModels;
using AgentsWinBar.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace AgentsWinBar;

/// <summary>
/// The full-window dashboard: resizable, maximizable, full-screen capable, showing everything the app knows
/// at once. It listens to the main view model for new results and redraws from a fresh snapshot.
/// </summary>
/// <remarks>
/// The window is created on the first open and then kept for the life of the app: closing it ends the session (the
/// page, its subscriptions, timers and cost demand are released) and hides the window, and the next open starts a
/// new session on the same window. Destroying the window instead leaked about 30 GDI objects and 40 handles per
/// open (mostly the WinUI custom title bar), so a long-running app that opened the dashboard a few hundred times
/// would reach the 10,000 GDI object limit.
/// </remarks>
public sealed partial class DashboardAppWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IDashboardService _dashboardService;
    private readonly ISettingsService _settings;
    private readonly ILogger<DashboardAppWindow> _logger;
    /// <summary>The page of the current session; null while the window is closed (hidden) or destroyed.</summary>
    private DashboardPage? _page;
    /// <summary>True from the open that started a session until the close that ended it.</summary>
    private bool _sessionActive;
    /// <summary>Set by a close: the next open leaves full screen and always on top.</summary>
    private bool _resetPresenterOnShow;
    private readonly DispatcherQueueTimer _redrawTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly AppWindow _appWindow;
    private volatile bool _closed;
    private bool _redrawing;
    private readonly DashboardRedrawTracker _redrawTracker = new();
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

        App.Windows.Add(this);
        App.ApplyTheme();
        UpdateWindowIcon();

        // Redraw shortly after new data arrives (several providers can land in quick succession).
        // Every 30 seconds: refresh the relative times on the current snapshot (DashboardClockUpdater). A fresh snapshot
        // is taken instead when a boundary was crossed (reset, midnight, expiry) or a full redraw was skipped or failed
        // since the last one, and on every 10th tick (5 minutes) without one, so values that change without a notification
        // cannot stay stale for long (DashboardRedrawTracker); the page then skips whatever did not change.
        var queue = DispatcherQueue.GetForCurrentThread();
        _redrawTimer = queue.CreateTimer();
        _redrawTimer.Interval = TimeSpan.FromMilliseconds(250);
        _redrawTimer.IsRepeating = false;
        _redrawTimer.Tick += OnRedrawTimerTick;
        _clockTimer = queue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(30);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += OnClockTimerTick;

        Closed += OnClosed;
        Activated += OnActivated;
        _appWindow.Changed += OnAppWindowChanged;
        _appWindow.Closing += OnAppWindowClosing;

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

        BeginSession();
    }

    /// <summary>
    /// Opens the dashboard: starts a session on this window if it was closed (hidden), then shows and activates it.
    /// </summary>
    public void ShowDashboard()
    {
        if (_closed) return;
        BeginSession();
        if (_resetPresenterOnShow)
        {
            // Reopened after a close: start in a normal window, as the new page shows its full-screen and on-top
            // toggles off
            _resetPresenterOnShow = false;
            try { ResetPresenter(); }
            catch (Exception ex) { _logger.LogError(ex, "Dashboard presenter reset failed"); }
        }
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    /// <summary>Creates the page and subscribes to the data it shows. Does nothing while a session is open.</summary>
    private void BeginSession()
    {
        if (_closed || _sessionActive) return;
        _sessionActive = true;
        _page = new DashboardPage();
        PageHost.Content = _page;
        _page.RefreshRequested += OnRefreshRequested;
        _page.SettingsRequested += OnSettingsRequested;
        _page.TopmostChanged += OnTopmostChanged;
        _page.FullScreenChanged += OnFullScreenChanged;
        _page.GraphVisibilityChanged += OnGraphVisibilityChanged;
        _settings.SettingsChanged += OnSettingsChanged;
        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ApiCreditsRefreshed += OnApiCreditsRefreshed;

        _clockTimer.Start();
        _page.SetBusy(_viewModel.IsLoading);
        Redraw();
        _costDemand ??= _viewModel.AcquireCostDemand();

        // CLI version and similar facts come from WSL; fetch them in the background and redraw when known.
        RefreshDiagnosticsAsync().SafeFireAndForget(onError: ex => _logger.LogDebug(ex, "Dashboard diagnostics refresh failed"));
    }

    /// <summary>
    /// Releases everything a session holds: timers, cost demand, visibility, subscriptions and the page. The window
    /// itself stays. Safe to call when no session is open.
    /// </summary>
    private void EndSession()
    {
        if (!_sessionActive) return;
        _sessionActive = false;
        try { _redrawTimer.Stop(); _clockTimer.Stop(); }
        catch (Exception ex) { _logger.LogError(ex, "Dashboard timer cleanup failed"); }
        _visibility?.Dispose();
        _visibility = null;
        _costDemand?.Dispose();
        _costDemand = null;
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ApiCreditsRefreshed -= OnApiCreditsRefreshed;
        _settings.SettingsChanged -= OnSettingsChanged;
        var page = _page;
        _page = null;
        if (page != null)
        {
            page.RefreshRequested -= OnRefreshRequested;
            page.SettingsRequested -= OnSettingsRequested;
            page.TopmostChanged -= OnTopmostChanged;
            page.FullScreenChanged -= OnFullScreenChanged;
            page.GraphVisibilityChanged -= OnGraphVisibilityChanged;
            try { page.Cleanup(); }
            catch (Exception ex)
            {
                // app.log keeps only the exception type; the crash log keeps its stack frames, which name the failing step
                _logger.LogError(ex, "Dashboard page cleanup failed");
                App.LogHandled("Dashboard page cleanup", ex);
            }
        }
        try { PageHost.Content = null; }
        catch (Exception ex) { _logger.LogError(ex, "Dashboard page host detach failed"); }
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
        if (_closed) return;
        // Nobody sees a locked screen (a cheap query, not a snapshot). A skipped redraw stays pending: the first 30-second
        // clock tick after the unlock (or after the window is shown again) takes the fresh snapshot instead.
        if (_redrawing || _hidden || _page == null || SessionStateNative.IsCurrentSessionLocked())
        {
            _redrawTracker.RedrawMissed();
            return;
        }
        _redrawing = true;
        try
        {
            var lastRefreshUtc = _viewModel.LastUpdated == default ? (DateTime?)null : _viewModel.LastUpdated.ToUniversalTime();
            var timing = _logger.IsEnabled(LogLevel.Debug);
            var start = timing ? Stopwatch.GetTimestamp() : 0;
            var snapshot = _dashboardService.GetSnapshot(_viewModel.Providers.ToList(), lastRefreshUtc,
                _viewModel.NextRefreshUtc, _viewModel.EffectiveRefreshInterval, _viewModel.RefreshCadence);
            var snapshotEnd = timing ? Stopwatch.GetTimestamp() : 0;
            _page.SetHiddenGraphs(GraphVisibility.HiddenIds(_settings.Settings.Providers));
            _page.Update(snapshot);
            _redrawTracker.RedrawDone();
            if (timing) _logger.LogDebug("Dashboard redraw: GetSnapshot {SnapshotMs:F2} ms, Page.Update {UpdateMs:F2} ms",
                Stopwatch.GetElapsedTime(start, snapshotEnd).TotalMilliseconds,
                Stopwatch.GetElapsedTime(snapshotEnd).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard redraw failed");
            _redrawTracker.RedrawMissed();
        }
        finally { _redrawing = false; }
    }

    private void OnProvidersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Post(() => { _redrawTimer.Stop(); _redrawTimer.Start(); });

    /// <summary>New API credits (for example after the Admin key changed in Settings): show them now, not at the next clock tick.</summary>
    private void OnApiCreditsRefreshed(object? sender, EventArgs e) =>
        Post(() => { _redrawTimer.Stop(); _redrawTimer.Start(); });

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.IsLoading) or nameof(MainViewModel.LastUpdated)
            or nameof(MainViewModel.NextRefreshUtc) or nameof(MainViewModel.EffectiveRefreshInterval)
            or nameof(MainViewModel.RefreshCadence))) return;
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
        // Only a session holds the visibility, cost demand and clock: a closed (hidden) dashboard holds nothing
        if (visible && _visibility == null && _sessionActive)
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

    private void OnRedrawTimerTick(DispatcherQueueTimer sender, object args) => Redraw();

    private void OnClockTimerTick(DispatcherQueueTimer sender, object args) => RefreshClock();

    private void RefreshClock()
    {
        if (_closed || _redrawing || _hidden || _page == null || SessionStateNative.IsCurrentSessionLocked()) return;
        if (_redrawTracker.ClockTickNeedsFullRedraw())
        {
            Redraw();
            return;
        }
        _redrawing = true;
        try
        {
            if (_page.CurrentSnapshot is { } current &&
                DashboardClockUpdater.TryRefreshClock(current, DateTime.UtcNow, TimeZoneInfo.Local, out var updated))
            {
                _page.ApplyClockTick(updated);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard clock refresh failed");
            // The next tick takes a fresh snapshot instead of retrying the cheap path on a half-applied one
            _redrawTracker.RedrawMissed();
            return;
        }
        finally { _redrawing = false; }
        Redraw();
    }

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

    /// <summary>
    /// The user closed the dashboard (title bar, Alt+F4, taskbar): end the session and hide the window instead of
    /// destroying it (see the class remarks). A programmatic Close at exit does not raise this and tears down fully.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closed) return;
        args.Cancel = true;
        try { EndSession(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard session cleanup failed");
            App.LogHandled("Dashboard session cleanup", ex);
        }
        finally
        {
            _appWindow.Hide();
            // Not now: changing the presenter of a hidden window shows it again (an empty dashboard after closing
            // from full screen). The next open resets it.
            _resetPresenterOnShow = true;
        }
    }

    private void ResetPresenter()
    {
        _topmost = false;
        if (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            _appWindow.SetPresenter(AppWindowPresenterKind.Default);
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
    }

    private void OnClosed(object sender, WindowEventArgs args) => Teardown();

    private void Teardown()
    {
        if (_closed) return;
        _closed = true;
        _appWindow.Closing -= OnAppWindowClosing;
        Closed -= OnClosed;
        Activated -= OnActivated;
        try { EndSession(); }
        catch (Exception ex) { _logger.LogError(ex, "Dashboard session cleanup failed"); }
        _redrawTimer.Tick -= OnRedrawTimerTick;
        _clockTimer.Tick -= OnClockTimerTick;
        foreach (var accelerator in _accelerators)
        {
            accelerator.Invoked -= OnF11Invoked;
            accelerator.Invoked -= OnEscapeInvoked;
        }
        _accelerators.Clear();
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
