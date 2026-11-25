using CodexBarWin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Views;

/// <summary>One provider in the Settings "Sign-ins" section.</summary>
public sealed class SignInRow
{
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    private Microsoft.UI.Xaml.Media.Geometry? _icon;
    /// <summary>Built when the row is shown, not for every state comparison.</summary>
    public Microsoft.UI.Xaml.Media.Geometry Icon => _icon ??= Helpers.ProviderIcons.GetGeometry(ProviderId);
    public required string StateText { get; init; }
    public bool CanSignIn { get; init; }
    public Visibility SignInVisibility => CanSignIn ? Visibility.Visible : Visibility.Collapsed;
    public string SignInName => $"Sign in to {DisplayName}";
}

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

    private MainViewModel? _mainViewModel;
    private bool _disposed;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Detaches singleton ViewModel subscriptions and clears item sources when the Settings window closes.</summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        if (_mainViewModel != null)
        {
            _mainViewModel.Providers.CollectionChanged -= OnProvidersChanged;
            _mainViewModel = null;
        }
        ProvidersItemsControl.ItemsSource = null;
        SignInsItemsControl.ItemsSource = null;
        SaveErrorBar.IsOpen = false;
        _viewModel = null;
        DataContext = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            _mainViewModel ??= App.Services.GetRequiredService<MainViewModel>();
            _mainViewModel.Providers.CollectionChanged -= OnProvidersChanged;
            _mainViewModel.Providers.CollectionChanged += OnProvidersChanged;
            UpdateSignIns();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<SettingsPage>>().LogWarning(ex, "Sign-ins could not be shown");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel != null) _mainViewModel.Providers.CollectionChanged -= OnProvidersChanged;
    }

    private void OnProvidersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_disposed) UpdateSignIns();
    }

    /// <summary>Each enabled provider (saved settings, in display order) with its sign-in state from the last refresh.</summary>
    private void UpdateSignIns()
    {
        if (_mainViewModel == null) return;
        var settings = App.Services.GetRequiredService<Services.ISettingsService>().Settings;
        var rows = settings.Providers
            .Where(p => p.IsEnabled && Models.ProviderConstants.IsValidProvider(p.Id))
            .OrderBy(p => p.Order)
            .Select(p => Models.ProviderConstants.ValidateAndNormalize(p.Id))
            .Distinct()
            .Select(id =>
            {
                var state = Helpers.SignInStatus.Of(_mainViewModel.Providers.FirstOrDefault(
                    d => d.Provider.Equals(id, StringComparison.OrdinalIgnoreCase)));
                return new SignInRow
                {
                    ProviderId = id,
                    DisplayName = Helpers.ProviderIcons.GetDisplayName(id),
                    StateText = Helpers.SignInStatus.Text(id, state),
                    CanSignIn = Helpers.SignInStatus.CanSignIn(state)
                };
            })
            .ToList();
        if (SignInsItemsControl.ItemsSource is List<SignInRow> current && current.Count == rows.Count &&
            current.Zip(rows).All(pair => pair.First.ProviderId == pair.Second.ProviderId &&
                pair.First.StateText == pair.Second.StateText && pair.First.CanSignIn == pair.Second.CanSignIn)) return;
        SignInsItemsControl.ItemsSource = rows;
    }

    private void OnSignInClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string provider } && Models.ProviderConstants.IsValidProvider(provider))
            SignInAction.Start(provider);
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

        // Set reset animation. Plain ComboBoxItems (as for the theme and the refresh interval) rather than
        // ItemsSource + DisplayMemberPath + SelectedValue: with those the closed box stayed blank until the user
        // picked a value, although the dropdown already marked the saved one.
        ResetAnimationComboBox.Items.Clear();
        foreach (var option in ViewModel.ResetAnimationOptions)
        {
            var item = new ComboBoxItem { Content = option.DisplayName, Tag = option.Mode };
            ResetAnimationComboBox.Items.Add(item);
            if (option.Mode == ViewModel.SelectedResetAnimationMode)
            {
                ResetAnimationComboBox.SelectedItem = item;
            }
        }
        ResetAnimationDescriptionText.Text = ViewModel.ResetAnimationDescription;

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
        ApiCreditsMonthlyBox.Value = (double)ViewModel.ApiCreditsMonthlyUsd;
        ApiCreditsCycleDayBox.Value = ViewModel.ApiCreditsCycleStartDay;
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
            if (ResetAnimationComboBox.SelectedItem is ComboBoxItem { Tag: Models.ResetAnimationMode resetMode })
            {
                ViewModel.SelectedResetAnimationMode = resetMode;
            }

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
            // A cleared NumberBox reports NaN: keep the previous value
            if (double.IsFinite(ApiCreditsMonthlyBox.Value))
            {
                ViewModel.ApiCreditsMonthlyUsd = (decimal)ApiCreditsMonthlyBox.Value;
            }
            if (double.IsFinite(ApiCreditsCycleDayBox.Value))
            {
                ViewModel.ApiCreditsCycleStartDay = (int)Math.Round(ApiCreditsCycleDayBox.Value);
            }
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
