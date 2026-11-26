using AgentsWinBar.ViewModels;
using AgentsWinBar.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using WinRT.Interop;

namespace AgentsWinBar;

/// <summary>
/// Window for application settings.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private SettingsPage? _settingsPage;
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private AppWindow? _appWindow;

    public SettingsWindow()
    {
        InitializeComponent();

        // Set up extended title bar with Mica
        SetupTitleBar();
        SetupMicaBackdrop();

        // Set window size
        _appWindow = AppWindow;
        _appWindow.Resize(new Windows.Graphics.SizeInt32(520, 680));

        // Track window
        App.Windows.Add(this);
        Closed += OnWindowClosed;

        // Apply theme first, then set icon based on theme
        App.ApplyTheme();
        UpdateWindowIcon();

        // Get ViewModel from DI
        _viewModel = App.Services.GetRequiredService<SettingsViewModel>();
        // ViewModel events are handled through the Page, not directly

        // Create and configure the settings page
        _settingsPage = new SettingsPage { ViewModel = _viewModel };
        _settingsPage.SaveRequested += OnSaveRequested;
        _settingsPage.CancelRequested += OnCancelRequested;

        // Navigate to settings page
        ContentFrame.Content = _settingsPage;
    }

    /// <summary>Opens Settings (or brings the open window forward) scrolled to the Anthropic API credits card.</summary>
    public static void OpenAtApiCredits()
    {
        var window = App.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? new SettingsWindow();
        window._settingsPage?.BringApiCreditsIntoView();
        window.Activate();
    }

    private void SetupTitleBar()
    {
        // Extend content into title bar
        ExtendsContentIntoTitleBar = true;

        // Set the custom title bar element
        SetTitleBar(AppTitleBar);

        // Get AppWindow and customize title bar
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (_appWindow.TitleBar != null)
        {
            // Make title bar buttons transparent
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
    }

    private void SetupMicaBackdrop()
    {
        if (!MicaController.IsSupported()) return;

        _micaController = new MicaController
        {
            Kind = MicaKind.Base
        };

        // Configure backdrop with theme awareness
        _backdropConfiguration = new SystemBackdropConfiguration
        {
            IsInputActive = true
        };
        UpdateBackdropTheme();

        _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _micaController.AddSystemBackdropTarget(
            WinRT.CastExtensions.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>(this));

        // Listen for theme changes on the root element
        if (Content is FrameworkElement rootElement)
        {
            rootElement.ActualThemeChanged += OnActualThemeChanged;
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateBackdropTheme();
        UpdateWindowIcon();
    }

    private void UpdateBackdropTheme()
    {
        if (_backdropConfiguration == null) return;

        var theme = SystemBackdropTheme.Default;
        if (Content is FrameworkElement rootElement)
        {
            theme = rootElement.ActualTheme switch
            {
                ElementTheme.Light => SystemBackdropTheme.Light,
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default
            };
        }
        _backdropConfiguration.Theme = theme;
    }

    private void UpdateWindowIcon()
    {
        if (_appWindow == null) return;

        var isLight = IsAppLightTheme();
        var iconName = isLight ? "app-dark.ico" : "app.ico";
        _appWindow.SetIcon($"Assets/{iconName}");
    }

    private bool IsAppLightTheme()
    {
        // Check the actual theme of the window content (skip if Default)
        if (Content is FrameworkElement rootElement && rootElement.ActualTheme != ElementTheme.Default)
        {
            return rootElement.ActualTheme == ElementTheme.Light;
        }

        // Fall back to system setting
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 1;
        }
        catch
        {
            return false;
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnWindowClosed;
        if (_settingsPage != null)
        {
            _settingsPage.SaveRequested -= OnSaveRequested;
            _settingsPage.CancelRequested -= OnCancelRequested;
            _settingsPage.Cleanup();
            _settingsPage = null;
        }
        ContentFrame.Content = null;
        if (Content is FrameworkElement rootElement)
        {
            rootElement.ActualThemeChanged -= OnActualThemeChanged;
        }
        Content = null;
        App.Windows.Remove(this);
        _micaController?.RemoveAllSystemBackdropTargets();
        _micaController?.Dispose();
        _micaController = null;
        _backdropConfiguration = null;
    }

    private void OnSaveRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnCancelRequested(object? sender, EventArgs e)
    {
        Close();
    }
}
