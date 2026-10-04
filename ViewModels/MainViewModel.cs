using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.ViewModels;

/// <summary>
/// ViewModel for the main window.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ICodexBarService _codexBarService;
    private readonly ISettingsService _settingsService;
    private readonly UsageAlertEvaluator _alertEvaluator;
    private readonly IToastNotifier _toastNotifier;
    private readonly ILogger<MainViewModel> _logger;
    private readonly UsageHistoryStore _history;
    private readonly UsageHistoryPersistence _historyPersistence;
    private PeriodicTimer? _refreshTimer;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _timerCts;
    private bool _disposed;
    private bool _refreshPending;

    [ObservableProperty]
    private ObservableCollection<UsageData> _providers = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private DateTime _lastUpdated;

    [ObservableProperty]
    private string _lastUpdatedText = "Never updated";

    public MainViewModel(
        ICodexBarService codexBarService,
        ISettingsService settingsService,
        UsageAlertEvaluator alertEvaluator,
        IToastNotifier toastNotifier,
        ILogger<MainViewModel> logger,
        UsageHistoryStore history,
        UsageHistoryPersistence historyPersistence)
    {
        _codexBarService = codexBarService;
        _settingsService = settingsService;
        _alertEvaluator = alertEvaluator;
        _toastNotifier = toastNotifier;
        _logger = logger;
        _history = history;
        _historyPersistence = historyPersistence;

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>
    /// Starts the periodic refresh.
    /// </summary>
    public async Task StartAsync()
    {
        // Dispose existing CTSs if any (prevents leaks on restart)
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _timerCts?.Cancel();
        _timerCts?.Dispose();
        _refreshTimer?.Dispose();

        _refreshCts = new CancellationTokenSource();
        _timerCts = new CancellationTokenSource();

        // Create timer with current interval from settings
        var interval = TimeSpan.FromSeconds(_settingsService.Settings.RefreshIntervalSeconds);
        _refreshTimer = new PeriodicTimer(interval);

        // Initial refresh
        await RefreshCoreAsync();

        // Start periodic refresh
        RunPeriodicRefreshAsync(_timerCts.Token).SafeFireAndForget(
            onError: ex => _logger.LogError(ex, "Periodic refresh task failed"));
    }

    /// <summary>
    /// Stops the periodic refresh.
    /// </summary>
    public void Stop()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(forceCostRefresh: true);

    private async Task RefreshCoreAsync(bool forceCostRefresh = false)
    {
        if (IsLoading)
        {
            return;
        }

        // Check if this is the first load (no existing data)
        bool isFirstLoad = Providers.Count == 0;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            _logger.LogInformation("Refreshing usage data...");

            // Get enabled providers from settings (sorted by Order)
            var enabledProviders = _settingsService.Settings.Providers
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.Order)
                .ToList();

            if (enabledProviders.Count == 0)
            {
                Providers.Clear();
                ErrorMessage = "No providers enabled. Go to Settings to enable providers.";
                return;
            }

            // Follow Settings on every refresh: add newly enabled providers, drop disabled ones, keep the order
            ProviderListSynchronizer.Sync(Providers, enabledProviders.Select(p => p.Id).ToList());

            // Everything is loading again (existing data stays visible while it refreshes)
            for (int i = 0; i < Providers.Count; i++)
            {
                Providers[i] = Providers[i] with { IsLoading = true };
            }

            // Stream results and update as they complete
            await foreach (var data in _codexBarService.GetAllUsageStreamAsync())
            {
                if (!_settingsService.Settings.DeveloperModeEnabled) _history.Record(data, DateTime.UtcNow);
                // Find the index of this provider
                var index = -1;
                for (int i = 0; i < Providers.Count; i++)
                {
                    if (Providers[i].Provider.Equals(data.Provider, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                if (index >= 0)
                {
                    // Remember which bars moved, so the compact widgets can show the ones that matter
                    var previous = Providers[index];
                    Providers[index] = UsageChangeTracker.Annotate(data, previous, DateTime.UtcNow) with
                    {
                        Cost = _settingsService.Settings.ShowCostTotals ? previous.Cost : null
                    };
                }
                else if (isFirstLoad)
                {
                    // Provider not in list yet (shouldn't happen normally)
                    Providers.Add(data);
                }
            }

            // Usage appears as soon as it arrives. The heavier log scans run afterwards, at a slow cadence.
            await _historyPersistence.SaveAsync();
            foreach (var provider in Providers.Select(p => p.Provider).ToList())
            {
                var cost = await _codexBarService.GetCostAsync(provider, forceCostRefresh, _refreshCts?.Token ?? default);
                var index = Providers.ToList().FindIndex(p => p.Provider == provider);
                if (index >= 0) Providers[index] = Providers[index] with
                {
                    Cost = _settingsService.Settings.ShowCostTotals ? cost : null
                };
            }

            LastUpdated = DateTime.Now;
            UpdateLastUpdatedText();
            NotifyAlerts();

            _logger.LogInformation("Usage data refreshed, {Count} providers", Providers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh usage data");

            // Only show error state on first load, otherwise keep existing data
            if (isFirstLoad)
            {
                var enabledProviders = _settingsService.Settings.Providers.Where(p => p.IsEnabled);
                Providers.Clear();
                foreach (var config in enabledProviders)
                {
                    Providers.Add(new UsageData
                    {
                        Provider = config.Id,
                        Error = "Connection error. Check WSL status.",
                        FetchedAt = DateTime.UtcNow
                    });
                }
            }
            ErrorMessage = "Failed to refresh data. Check connection.";
        }
        finally
        {
            IsLoading = false;
            if (_refreshPending && !_disposed)
            {
                _refreshPending = false;
                RefreshCoreAsync().SafeFireAndForget(onError: ex => _logger.LogError(ex, "Pending settings refresh failed"));
            }
        }
    }

    /// <summary>
    /// Shows a Windows notification for anything that newly needs attention (once per usage window).
    /// </summary>
    private void NotifyAlerts()
    {
        if (!_settingsService.Settings.AlertsEnabled)
        {
            return;
        }

        try
        {
            foreach (var alert in _alertEvaluator.Evaluate(Providers, DateTime.UtcNow))
            {
                _logger.LogInformation("Usage alert: {Title}", alert.Title);
                _toastNotifier.Show(alert.Title, alert.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evaluate usage alerts");
        }
    }

    [RelayCommand]
    private async Task ExitAsync()
    {
        _logger.LogInformation("Exit requested");
        await _historyPersistence.SaveAsync(force: true);
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private async Task RunPeriodicRefreshAsync(CancellationToken ct)
    {
        if (_refreshTimer == null)
        {
            return;
        }

        try
        {
            while (await _refreshTimer.WaitForNextTickAsync(ct))
            {
                await RefreshCoreAsync();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Periodic refresh stopped");
        }
    }

    private void UpdateLastUpdatedText()
    {
        var elapsed = DateTime.Now - LastUpdated;

        LastUpdatedText = elapsed.TotalSeconds switch
        {
            < 5 => "Updated just now",
            < 60 => $"Updated {elapsed.Seconds} sec ago",
            < 3600 => $"Updated {elapsed.Minutes} min ago",
            _ => $"Updated {elapsed.Hours} hours ago"
        };
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            _logger.LogDebug("Settings changed, recreating timer and refreshing data");
            RecreateTimer();
            if (!_settingsService.Settings.ShowCostTotals)
                for (var i = 0; i < Providers.Count; i++) Providers[i] = Providers[i] with { Cost = null };
            if (IsLoading) { _refreshPending = true; return; }
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle settings change");
        }
    }

    /// <summary>
    /// Recreates the periodic timer with the current refresh interval from settings.
    /// </summary>
    private void RecreateTimer()
    {
        var interval = TimeSpan.FromSeconds(_settingsService.Settings.RefreshIntervalSeconds);

        // Stop and dispose old timer and CTS
        _timerCts?.Cancel();
        _timerCts?.Dispose();
        _refreshTimer?.Dispose();

        // Create new timer
        _refreshTimer = new PeriodicTimer(interval);
        _timerCts = new CancellationTokenSource();

        // Restart periodic refresh
        RunPeriodicRefreshAsync(_timerCts.Token).SafeFireAndForget(
            onError: ex => _logger.LogError(ex, "Periodic refresh task failed"));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            Stop();
            _timerCts?.Cancel();
            _timerCts?.Dispose();
            _refreshTimer?.Dispose();
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }

        _disposed = true;
    }
}
