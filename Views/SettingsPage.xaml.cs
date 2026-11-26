using AgentsWinBar.Helpers;
using AgentsWinBar.Services;
using AgentsWinBar.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentsWinBar.Views;

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

    // Anthropic Admin key ("Anthropic API credits" card). Key actions apply at once and never wait for Save.
    private IAdminKeyService? _adminKeys;
    private CancellationTokenSource? _keyCheckCts;
    private int _keyOperation;
    private bool _keyBusy;
    private bool _replacingKey;
    private bool _scrollToApiCredits;
    /// <summary>The last status line that is not a progress line; a key action that changes nothing goes back to it.</summary>
    private string _settledKeyStatus = "Status: Not set up";
    /// <summary>A pasted key, held only between a check that could not reach Anthropic and "Save anyway"; null otherwise.</summary>
    private string? _unverifiedKey;

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
        // An in-flight save or removal still completes (the variable must not be left half written); its result is not shown.
        _keyCheckCts?.Cancel();
        _keyCheckCts = null;
        _unverifiedKey = null;
        ApiKeyPasswordBox.Password = string.Empty;
        ApiKeyRemoveFlyout.Hide();
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
        try
        {
            if (_adminKeys == null)
            {
                _adminKeys = App.Services.GetRequiredService<IAdminKeyService>();
                ShowSavedKey();
            }
            ScrollToApiCreditsIfRequested();
        }
        catch (Exception ex) { LogKeyFailure("ShowStatus", ex); }
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

        ActivityRefreshToggle.IsOn = ViewModel.ActivityRefresh;
        StartWithWindowsToggle.IsOn = ViewModel.StartWithWindows;
        StartMinimizedToggle.IsOn = ViewModel.StartMinimized;
        AlwaysOnOverlayToggle.IsOn = ViewModel.AlwaysOnOverlay;
        TaskbarDisplayToggle.IsOn = ViewModel.TaskbarDisplay;
        // Plain ComboBoxItems tagged with the stored value (see the reset animation note above)
        TaskbarPositionComboBox.SelectedItem = TaskbarPositionComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag && tag == ViewModel.TaskbarDisplayPosition)
            ?? TaskbarPositionComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        AlertsToggle.IsOn = ViewModel.AlertsEnabled;
        CostTotalsToggle.IsOn = ViewModel.ShowCostTotals;
        ApiCreditsMonthlyBox.Value = (double)ViewModel.ApiCreditsMonthlyUsd;
        ApiCreditsCycleDayBox.Value = ViewModel.ApiCreditsCycleStartDay;
        DeveloperModeToggle.IsOn = ViewModel.DeveloperModeEnabled;
    }

    /// <summary>Scrolls to the Anthropic API credits card: now when the page is loaded, otherwise once it loads.</summary>
    public void BringApiCreditsIntoView()
    {
        if (_disposed) return;
        _scrollToApiCredits = true;
        if (IsLoaded) ScrollToApiCreditsIfRequested();
    }

    private void ScrollToApiCreditsIfRequested()
    {
        if (!_scrollToApiCredits) return;
        _scrollToApiCredits = false;
        // Queued behind layout, so the card is measured and in its final place
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_disposed) return;
            try { ApiCreditsCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false }); }
            catch (Exception ex) { LogKeyFailure("ScrollToCard", ex); }
        });
    }

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>" (key ending in abcd)", or nothing when the service withheld the last 4 characters.</summary>
    private static string KeyEnding(string? lastFour) => lastFour == null ? string.Empty : $" (key ending in {lastFour})";

    private static string KeyStatusLine(AdminKeyCheck result, string? lastFour) => result switch
    {
        AdminKeyCheck.Valid => $"Status: Connected{KeyEnding(lastFour)}",
        AdminKeyCheck.Rejected => $"Status: Rejected by Anthropic{KeyEnding(lastFour)}",
        AdminKeyCheck.BadFormat => $"Status: Not an Anthropic key{KeyEnding(lastFour)}",
        _ => $"Status: Could not be checked{KeyEnding(lastFour)}"
    };

    /// <summary>Shows the key that is set now; a set key is checked with Anthropic in the background.</summary>
    private void ShowSavedKey()
    {
        var status = _adminKeys!.GetStatus();
        ApplyKeyLayout(status);
        if (!status.IsSet)
        {
            ApiKeyStatusText.Text = _settledKeyStatus = "Status: Not set up";
            return;
        }
        // What a failed key action falls back to if it interrupts this check
        _settledKeyStatus = $"Status: Set up, not checked yet{KeyEnding(status.LastFour)}";
        // Developer mode shows sample API credits and contacts no one, so the saved key is not checked either
        if (App.Services.GetRequiredService<ISettingsService>().Settings.DeveloperModeEnabled)
        {
            ApiKeyStatusText.Text = _settledKeyStatus;
            return;
        }
        ApiKeyStatusText.Text = $"Status: Checking{KeyEnding(status.LastFour)}…";
        CheckSavedKeyAsync(status.LastFour).SafeFireAndForget(
            onError: ex => LogKeyFailure("CheckSavedKey", ex), continueOnCapturedContext: true);
    }

    private async Task CheckSavedKeyAsync(string? lastFour)
    {
        var operation = ++_keyOperation;
        _keyCheckCts?.Cancel();
        var cts = new CancellationTokenSource();
        _keyCheckCts = cts;
        try
        {
            var result = await _adminKeys!.TestSavedAsync(cts.Token);
            if (_disposed || operation != _keyOperation) return;
            ApiKeyStatusText.Text = _settledKeyStatus = KeyStatusLine(result, lastFour);
        }
        finally
        {
            if (ReferenceEquals(_keyCheckCts, cts)) _keyCheckCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Setup steps without a key (or while replacing one); "Replace key" and "Remove key" with one.</summary>
    private void ApplyKeyLayout(AdminKeyStatus status)
    {
        if (!status.IsSet) _replacingKey = false;
        ApiKeySetupPanel.Visibility = Vis(!status.IsSet || _replacingKey);
        ApiKeyCancelReplaceButton.Visibility = Vis(status.IsSet && _replacingKey);
        ApiKeyManagePanel.Visibility = Vis(status.IsSet && !_replacingKey);
    }

    private void ShowKeyMessage(string? message, bool offerSaveAnyway = false)
    {
        ApiKeyMessageText.Text = message ?? string.Empty;
        ApiKeyMessageText.Visibility = Vis(!string.IsNullOrEmpty(message));
        ApiKeySaveAnywayButton.Visibility = Vis(offerSaveAnyway);
    }

    /// <summary>Starts a key action: stops the background check, shows progress and locks the key controls.</summary>
    private void BeginKeyAction(string progressStatus)
    {
        _keyCheckCts?.Cancel();
        _keyCheckCts = null;
        _keyOperation++;
        _keyBusy = true;
        ShowKeyMessage(null);
        ApiKeyStatusText.Text = progressStatus;
        ApiKeyProgress.IsActive = true;
        ApiKeyProgress.Visibility = Visibility.Visible;
        SetKeyControlsEnabled(false);
    }

    private void EndKeyAction(string status, string message, bool offerSaveAnyway = false)
    {
        _keyBusy = false;
        ApiKeyProgress.IsActive = false;
        ApiKeyProgress.Visibility = Visibility.Collapsed;
        SetKeyControlsEnabled(true);
        ApplyKeyLayout(_adminKeys!.GetStatus());
        ApiKeyStatusText.Text = _settledKeyStatus = status;
        ShowKeyMessage(message, offerSaveAnyway);
    }

    private void SetKeyControlsEnabled(bool enabled)
    {
        ApiKeyPasswordBox.IsEnabled = enabled;
        ApiKeySaveButton.IsEnabled = enabled;
        ApiKeyCancelReplaceButton.IsEnabled = enabled;
        ApiKeyReplaceButton.IsEnabled = enabled;
        ApiKeyRemoveButton.IsEnabled = enabled;
        ApiKeySaveAnywayButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Logs only a short action name and the exception type: a message could carry request or key details, and app.log
    /// keeps only identifier-like arguments.
    /// </summary>
    private static void LogKeyFailure(string action, Exception ex)
    {
        try { App.Services.GetRequiredService<ILogger<SettingsPage>>().LogWarning("Admin key card: {Action} failed ({Type})", action, ex.GetType().Name); }
        catch { /* never throw from logging */ }
    }

    private async void OnCreateAdminKeyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(Models.ApiCreditsInfo.AdminKeysUrl)) && !_disposed)
                ShowKeyMessage("The browser did not open. Go to platform.claude.com/settings/admin-keys to create the key.");
        }
        catch (Exception ex)
        {
            LogKeyFailure("OpenAdminKeysPage", ex);
            if (!_disposed) ShowKeyMessage("The browser did not open. Go to platform.claude.com/settings/admin-keys to create the key.");
        }
    }

    /// <summary>"Save key": check with Anthropic, then save on Valid; refuse a malformed or rejected key; offer "Save anyway" when unreachable.</summary>
    private async void OnSaveKeyClicked(object sender, RoutedEventArgs e)
    {
        if (_keyBusy || _disposed || _adminKeys == null) return;
        // The box is cleared on every attempt; only "Save anyway" keeps the key (in _unverifiedKey) any longer.
        var candidate = ApiKeyPasswordBox.Password;
        ApiKeyPasswordBox.Password = string.Empty;
        _unverifiedKey = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            ShowKeyMessage("Paste your Admin key into the box first.");
            return;
        }
        var previousStatus = _settledKeyStatus;
        BeginKeyAction("Status: Checking the key with Anthropic…");
        try
        {
            var result = await _adminKeys.TestAsync(candidate);
            if (_disposed) return;
            switch (result)
            {
                case AdminKeyCheck.Valid:
                    await SaveKeyAsync(candidate, AdminKeyCheck.Valid, previousStatus);
                    break;
                case AdminKeyCheck.BadFormat:
                    EndKeyAction(previousStatus, "This is not an Anthropic key, so it was not saved. Anthropic keys start with sk-ant-. Copy the whole key and paste it again.");
                    break;
                case AdminKeyCheck.Rejected:
                    EndKeyAction(previousStatus, "Anthropic rejected this key, so it was not saved. Check that it is an active Admin key, then paste it again.");
                    break;
                default:
                    _unverifiedKey = candidate;
                    EndKeyAction(previousStatus, "Anthropic could not be reached to check this key. Try again later, or save it without the check.",
                        offerSaveAnyway: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogKeyFailure("SaveKey", ex);
            if (!_disposed) EndKeyAction(previousStatus, "Something went wrong, so the key was not saved. Try again.");
        }
    }

    private async void OnSaveKeyAnywayClicked(object sender, RoutedEventArgs e)
    {
        if (_keyBusy || _disposed || _adminKeys == null) return;
        var candidate = _unverifiedKey;
        _unverifiedKey = null;
        if (candidate == null)
        {
            ShowKeyMessage(null);
            return;
        }
        var previousStatus = _settledKeyStatus;
        BeginKeyAction("Status: Saving the key…");
        try { await SaveKeyAsync(candidate, AdminKeyCheck.Unreachable, previousStatus); }
        catch (Exception ex)
        {
            LogKeyFailure("SaveKey", ex);
            if (!_disposed) EndKeyAction(previousStatus, "Something went wrong, so the key was not saved. Try again.");
        }
    }

    /// <summary>Writes the key (off the UI thread, in the service); the dashboard re-reads API credits as soon as it is saved.</summary>
    private async Task SaveKeyAsync(string candidate, AdminKeyCheck check, string previousStatus)
    {
        ApiKeyStatusText.Text = "Status: Saving the key…";
        var saved = await _adminKeys!.SaveAsync(candidate);
        if (_disposed) return;
        if (!saved)
        {
            EndKeyAction(previousStatus, "The key could not be saved to your Windows user environment, so nothing changed. Try again.");
            return;
        }
        _replacingKey = false;
        EndKeyAction(KeyStatusLine(check, _adminKeys.GetStatus().LastFour), check == AdminKeyCheck.Valid
            ? "Saved and in use now. The dashboard shows your API spend. You don't need to click Save."
            : "Saved without the check and in use now. The dashboard shows your API spend once Anthropic answers. You don't need to click Save.");
    }

    private void OnReplaceKeyClicked(object sender, RoutedEventArgs e)
    {
        if (_keyBusy || _disposed || _adminKeys == null) return;
        _replacingKey = true;
        ShowKeyMessage(null);
        ApplyKeyLayout(_adminKeys.GetStatus());
        ApiKeyPasswordBox.Focus(FocusState.Programmatic);
    }

    private void OnCancelReplaceKeyClicked(object sender, RoutedEventArgs e)
    {
        if (_keyBusy || _disposed || _adminKeys == null) return;
        _replacingKey = false;
        _unverifiedKey = null;
        ApiKeyPasswordBox.Password = string.Empty;
        ShowKeyMessage(null);
        ApplyKeyLayout(_adminKeys.GetStatus());
    }

    /// <summary>"Remove key" in the confirmation flyout: deletes the user and process variables at once.</summary>
    private async void OnRemoveKeyConfirmed(object sender, RoutedEventArgs e)
    {
        ApiKeyRemoveFlyout.Hide();
        if (_keyBusy || _disposed || _adminKeys == null) return;
        _unverifiedKey = null;
        var previousStatus = _settledKeyStatus;
        BeginKeyAction("Status: Removing the key…");
        try
        {
            var removed = await _adminKeys.RemoveAsync();
            if (_disposed) return;
            if (removed)
            {
                _replacingKey = false;
                EndKeyAction("Status: Not set up",
                    "Removed now. The dashboard no longer shows API spend. The key stays active at Anthropic until you delete it on the Claude Platform.");
            }
            else EndKeyAction(previousStatus, "The key could not be removed from your Windows user environment. Try again.");
        }
        catch (Exception ex)
        {
            LogKeyFailure("RemoveKey", ex);
            if (!_disposed) EndKeyAction(previousStatus, "Something went wrong, so the key was not removed. Try again.");
        }
    }

    private void OnTestAlertClicked(object sender, RoutedEventArgs e)
    {
        var result = Services.ToastResult.Unavailable;
        try
        {
            result = App.Services.GetRequiredService<Services.IToastNotifier>()
                .Show("AgentsWinBar", "Notifications are working. You'll get one like this when usage needs attention.");
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<SettingsPage>>().LogWarning(ex, "Test notification failed");
        }

        TestAlertStatus.Text = result switch
        {
            Services.ToastResult.Shown => "Sent. If nothing appeared, check Do not disturb in Windows.",
            Services.ToastResult.TurnedOff => "Notifications are off for AgentsWinBar. Turn them on in Windows Settings > System > Notifications.",
            _ => "Windows didn't accept the notification. app.log in the app's data folder has the details."
        };
        TestAlertStatus.Visibility = Visibility.Visible;
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

            ViewModel.ActivityRefresh = ActivityRefreshToggle.IsOn;
            ViewModel.StartWithWindows = StartWithWindowsToggle.IsOn;
            ViewModel.StartMinimized = StartMinimizedToggle.IsOn;
            ViewModel.AlwaysOnOverlay = AlwaysOnOverlayToggle.IsOn;
            ViewModel.TaskbarDisplay = TaskbarDisplayToggle.IsOn;
            if (TaskbarPositionComboBox.SelectedItem is ComboBoxItem { Tag: string position })
            {
                ViewModel.TaskbarDisplayPosition = Models.TaskbarDisplayPositions.Normalize(position);
            }
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
