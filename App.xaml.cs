using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.ViewModels;
using NativeTestOverrides = AgentsWinBar.Services.Native.NativeTestOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace AgentsWinBar;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;
#if DEBUG
    // A Debug instance running on the test hooks (AGENTSWINBAR_TEST_*) can run beside the installed app.
    private static string MutexName => NativeTestOverrides.Current is null
        ? "AgentsWinBar_SingleInstance" : "AgentsWinBar_SingleInstance_Test";
#else
    private const string MutexName = "AgentsWinBar_SingleInstance";
#endif

    private readonly CancellationTokenSource _shutdown = new();
    private MainWindow? _mainWindow;
    private Window? _setupWindow;

    /// <summary>
    /// Gets the service provider for dependency injection.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Gets all active windows.
    /// </summary>
    public static List<Window> Windows { get; } = [];

    /// <summary>
    /// Gets the current App instance.
    /// </summary>
    public static new App Current => (App)Application.Current;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        RegisterCrashLogging();
    }

    /// <summary>
    /// Writes unhandled exceptions to %LOCALAPPDATA%\AgentsWinBar\crash.log so crashes can be diagnosed.
    /// </summary>
    private void RegisterCrashLogging()
    {
        UnhandledException += (_, e) => LogCrash("Xaml.UnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash("UnobservedTaskException", e.Exception);
    }

    /// <summary>Records a handled-but-unexpected failure (e.g. a window that could not be opened) in the crash log.</summary>
    public static void LogHandled(string source, Exception ex) => LogCrash(source, ex);

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var folder = NativeTestOverrides.Current?.AppDataDirectory ?? AppPaths.DataDirectory;
            // Types and stack frames are enough to diagnose a crash; messages stay out (see DescribeException).
            var details = ex == null ? string.Empty : RollingLogFile.DescribeException(ex);
            RollingLogFile.Append(Path.Combine(folder, "crash.log"), $"{DateTimeOffset.UtcNow:O} {source}{details}");
        }
        catch
        {
            // Never throw from the crash logger
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Single instance check
            _mutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is already running
                ExitApplication();
                return;
            }

            // Before anything reads the data folder: bring over the data of the app's old name, once.
            await ImportLegacyDataAsync();

            // Load settings
            var settingsService = Services.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync();

            if (settingsService.Settings.DeveloperModeEnabled)
            {
                StartMainWindow();
                return;
            }

            // Load cache and persistent stores concurrently before setup validation.
            var cacheService = Services.GetRequiredService<ICacheService>();
            var historyPersistence = Services.GetRequiredService<UsageHistoryPersistence>();
            var costLedger = Services.GetRequiredService<ICostLedger>();
            var earlyResetStore = Services.GetRequiredService<IEarlyResetStore>();
            var setupMemoTask = Services.GetRequiredService<ISetupMemoService>().IsValidAsync();

            await Task.WhenAll(
                cacheService.LoadAsync(),
                historyPersistence.LoadAsync(),
                costLedger.LoadAsync(),
                earlyResetStore.LoadAsync(),
                setupMemoTask);

            // A recent successful setup avoids waking WSL solely for launch validation.
            if (await setupMemoTask)
            {
                StartMainWindow();
                return;
            }

            // Check setup status
            var setupChecker = Services.GetRequiredService<ISetupChecker>();
            var status = await setupChecker.CheckAsync();

            if (status.IsReady)
            {
                // Normal startup - show main window
                StartMainWindow();
            }
            else
            {
                // Setup required - show setup window
                ShowSetupWindow();
            }
        }
        catch (Exception ex)
        {
            LogCrash("Application launch failed", ex);
            Services.GetRequiredService<ILogger<App>>().LogError(ex, "Application launch failed");
            ReleaseMutex();
            ExitApplication();
        }
    }

    private void StartMainWindow()
    {
        var logger = Services.GetRequiredService<ILogger<App>>();
        try
        {
            _mainWindow = new MainWindow();
            Windows.Add(_mainWindow);
            _mainWindow.Closed += (s, e) =>
            {
                _shutdown.Cancel();
                Windows.Remove(_mainWindow);
                ReleaseMutex();
            };
            _mainWindow.Activate();

            // Apply theme
            ApplyTheme();

            // Start the main view model
            var mainViewModel = Services.GetRequiredService<MainViewModel>();

            // Global reset-animation gate: initial value, then every change (raised on the UI thread by the view model)
            Helpers.BarResetAnimation.PlaybackEnabled = mainViewModel.ShouldPlayResetAnimation;
            mainViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ShouldPlayResetAnimation))
                    Helpers.BarResetAnimation.PlaybackEnabled = mainViewModel.ShouldPlayResetAnimation;
            };
            mainViewModel.StartAsync().SafeFireAndForget(
                onError: ex => logger.LogError(ex, "MainViewModel.StartAsync failed"));

            // Tray icon is now handled in MainWindow.xaml
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "StartMainWindow failed");
            throw;
        }
    }

    private void ShowSetupWindow()
    {
        _setupWindow = new SetupWindow();
        Windows.Add(_setupWindow);
        _setupWindow.Closed += (s, e) =>
        {
            Windows.Remove(_setupWindow!);
            if (_mainWindow is null) _shutdown.Cancel();
        };
        _setupWindow.Activate();

        // Apply theme
        ApplyTheme();
    }

    /// <summary>
    /// Applies the saved theme to all windows.
    /// </summary>
    public static void ApplyTheme()
    {
        var settingsService = Services.GetRequiredService<ISettingsService>();
        var theme = settingsService.Settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        foreach (var window in Windows)
        {
            if (window.Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = theme;
            }
        }
    }

    /// <summary>
    /// Called when setup is complete to transition to main window.
    /// </summary>
    public void OnSetupComplete()
    {
        if (_setupWindow == null || _mainWindow != null) return;
        // Keep a live window while transitioning; closing the last window first can shut down WinUI.
        StartMainWindow();
        _setupWindow?.Close();
        _setupWindow = null;
    }

    /// <summary>
    /// Releases and disposes the single-instance mutex.
    /// </summary>
    private static void ReleaseMutex()
    {
        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Mutex was not owned by this thread (already released)
            }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    /// <summary>
    /// One-time import of the CodexBarWin data folder (see <see cref="LegacyDataImporter"/>). Skipped on the Debug
    /// test hooks, whose data folder is a fake one. Never throws, so it cannot block startup.
    /// </summary>
    private static async Task ImportLegacyDataAsync()
    {
        try
        {
            if (NativeTestOverrides.Current is not null) return;
            var importer = new LegacyDataImporter(Services.GetRequiredService<ILogger<LegacyDataImporter>>(),
                AppPaths.DataDirectory, () => LegacyDataImporter.DefaultSources(AppPaths.LocalAppData), LogHandled);
            await Task.Run(importer.Run);
        }
        catch (Exception ex)
        {
            LogHandled("ImportLegacyDataAsync", ex);
        }
    }

    internal void ExitApplication()
    {
        _shutdown.Cancel();
        Exit();
    }

    private IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
#if DEBUG
            builder.AddDebug();  // Output to VS Debug window
            builder.SetMinimumLevel(LogLevel.Debug);
#else
            builder.AddProvider(new FileLoggerProvider(AppPaths.DataDirectory));
            builder.SetMinimumLevel(LogLevel.Warning);
#endif
        });

        // Debug test hooks (AGENTSWINBAR_TEST_*): a fake profile, data folder, credential and provider server. Always
        // null in a Release build. A set but invalid variable throws here, so a Debug app never falls back silently.
        var testOverrides = NativeTestOverrides.Current;
        // A test run reads no admin key or spend-log setting from the environment; the spend log is the fake one.
        Func<string, EnvironmentVariableTarget, string?>? testEnvironment = testOverrides is null ? null
            : (name, _) => name == GeminiSpendService.SpendLogEnvironmentVariable && testOverrides.ProfileRoot is { } root
                ? Path.Combine(root, ".claude", "gemini-api", "spend.jsonl") : null;

        // Services
        services.AddSingleton<IWslService, WslService>();
        services.AddSingleton<ICacheService>(sp => new CacheService(sp.GetRequiredService<ILogger<CacheService>>(),
            sp.GetRequiredService<ISettingsService>(),
            testOverrides?.AppDataDirectory is { } cacheFolder ? Path.Combine(cacheFolder, "cache.json") : null));
        services.AddSingleton<ICostCacheStore, CostCacheStore>();
        services.AddSingleton<ICostLedger, CostLedger>();
        services.AddSingleton<IProviderIncidentFetcher, ProviderIncidentFetcher>();
        // Native usage readers (one pooled HttpClient). Registered with a factory so DI uses the production
        // constructor; CodexBarService routes providers whose usage source is "native" through it.
        services.AddSingleton<Services.Native.INativeUsageService>(sp =>
            new Services.Native.NativeUsageService(sp.GetRequiredService<ILoggerFactory>(),
                () => sp.GetRequiredService<ISettingsService>().Settings.RenewClaudeSignIn, testOverrides,
                () => sp.GetRequiredService<ISettingsService>().Settings.RenewGeminiSignIn, _shutdown.Token));
        // Native cost scanner: index and refreshed prices live beside settings.json. CodexBarService uses it while
        // the cost source is "native"; nothing is scanned or watched before the first cost request.
        services.AddSingleton<Services.Native.INativeCostService>(sp =>
            new Services.Native.NativeCostService(sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ILoggerFactory>(), testOverrides));
        services.AddSingleton<ICodexBarService, CodexBarService>();
        services.AddSingleton<ISettingsService>(sp =>
            new SettingsService(sp.GetRequiredService<ILogger<SettingsService>>(), testOverrides?.AppDataDirectory));
        services.AddSingleton<IApiBillingService>(sp => new ApiBillingService(sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ILogger<ApiBillingService>>(), environmentReader: testEnvironment));
        services.AddSingleton<IGeminiSpendService>(sp => new GeminiSpendService(sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ILogger<GeminiSpendService>>(), environmentReader: testEnvironment));
        services.AddSingleton<ISetupChecker, SetupChecker>();
        services.AddSingleton<ISetupMemoService, SetupMemoService>();
        services.AddSingleton<ISessionStateSource, SessionStateSource>();
        services.AddSingleton<IDisplayStateSource, DisplayStateSource>();
        services.AddSingleton<IBatterySaverStatusSource, WindowsBatterySaverStatusSource>();
        services.AddSingleton<IBatterySaverMonitor, BatterySaverMonitor>();
        services.AddSingleton<ISampleDataLoader, SampleDataLoader>();
        services.AddSingleton<UsageAlertEvaluator>();
        services.AddSingleton<UsageHistoryStore>();
        services.AddSingleton<UsageHistoryPersistence>();
        services.AddSingleton<IActivityLog, ActivityLog>();
        services.AddSingleton<IEarlyResetStore, EarlyResetStore>();
        services.AddSingleton<IDashboardService, DashboardService>();
        services.AddSingleton<IToastNotifier, ToastNotifier>();
        // "Sign in" buttons: open the provider's own sign-in, then watch (only while one is pending) for the new sign-in
        services.AddSingleton<ISignInSystem, WindowsSignInSystem>();
        services.AddSingleton<ISignInLauncher, SignInLauncher>();
        services.AddSingleton<ISignInSignals, WindowsSignInSignals>();
        services.AddSingleton<ISignInWatcher, SignInWatcher>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SetupViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }
}
