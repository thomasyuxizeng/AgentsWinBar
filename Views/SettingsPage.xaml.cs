using CodexBarWin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Views;

/// <summary>
/// Page for application settings.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private SettingsViewModel? _viewModel;

    public SettingsViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;

            if (_viewModel != null)
            {
                LoadSettings();
            }
        }
    }

    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void LoadSettings()
    {
        if (ViewModel == null)
        {
            return;
        }

        ViewModel.Load();

        ProvidersItemsControl.ItemsSource = ViewModel.Providers;

        // Set theme
        ThemeComboBox.SelectedIndex = ViewModel.SelectedThemeIndex;

        // Set refresh interval
        foreach (ComboBoxItem item in RefreshIntervalComboBox.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out var seconds) && seconds == ViewModel.RefreshInterval)
            {
                RefreshIntervalComboBox.SelectedItem = item;
                break;
            }
        }

        StartWithWindowsToggle.IsOn = ViewModel.StartWithWindows;
        StartMinimizedToggle.IsOn = ViewModel.StartMinimized;
        AlwaysOnOverlayToggle.IsOn = ViewModel.AlwaysOnOverlay;
        TaskbarDisplayToggle.IsOn = ViewModel.TaskbarDisplay;
        AlertsToggle.IsOn = ViewModel.AlertsEnabled;
        CostTotalsToggle.IsOn = ViewModel.ShowCostTotals;
        DeveloperModeToggle.IsOn = ViewModel.DeveloperModeEnabled;
    }

    private void OnTestAlertClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Services.GetRequiredService<Services.IToastNotifier>()
                .Show("CodexBarWin", "Notifications are working. You'll get one like this when usage needs attention.");
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<SettingsPage>>().LogWarning(ex, "Test notification failed");
        }
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        SaveErrorBar.IsOpen = false;
        try
        {
            if (ViewModel == null)
            {
                return;
            }

            // Update ViewModel from UI
            ViewModel.SelectedThemeIndex = ThemeComboBox.SelectedIndex;

            if (RefreshIntervalComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out var seconds))
            {
                ViewModel.RefreshInterval = seconds;
            }

            ViewModel.StartWithWindows = StartWithWindowsToggle.IsOn;
            ViewModel.StartMinimized = StartMinimizedToggle.IsOn;
            ViewModel.AlwaysOnOverlay = AlwaysOnOverlayToggle.IsOn;
            ViewModel.TaskbarDisplay = TaskbarDisplayToggle.IsOn;
            ViewModel.AlertsEnabled = AlertsToggle.IsOn;
            ViewModel.ShowCostTotals = CostTotalsToggle.IsOn;
            ViewModel.DeveloperModeEnabled = DeveloperModeToggle.IsOn;

            var wasDeveloperMode = App.Services.GetRequiredService<Services.ISettingsService>().Settings.DeveloperModeEnabled;
            await ViewModel.SaveCommand.ExecuteAsync(null);
            if (wasDeveloperMode && !ViewModel.DeveloperModeEnabled)
            {
                await App.Services.GetRequiredService<Services.ICostLedger>().LoadAsync();
            }

            // Apply theme change
            App.ApplyTheme();

            SaveRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<SettingsPage>>();
            logger.LogError(ex, "OnSaveClicked failed");
            SaveErrorBar.IsOpen = true;
            SettingsScrollViewer.UpdateLayout();
            SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        }
        finally { IsEnabled = true; }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        ViewModel?.CancelCommand.Execute(null);
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMoveUpClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is ProviderConfigViewModel provider)
        {
            ViewModel?.MoveUpCommand.Execute(provider);
        }
    }

    private void OnMoveDownClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is ProviderConfigViewModel provider)
        {
            ViewModel?.MoveDownCommand.Execute(provider);
        }
    }
}
