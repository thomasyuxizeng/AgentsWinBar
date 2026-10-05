using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly IActivityLog _activityLog;
    private readonly ISessionStateSource _sessionState;
    private readonly ICacheService _cacheService;
    private readonly ISetupMemoService _setupMemo;
    private bool _sessionLocked;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _timerCts;
    private CancellationTokenSource? _costRefreshCts;
    private CancellationTokenSource? _trimCts;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private UsageFingerprint? _usageFingerprint;
    private int _unchangedRefreshes;
    private int _adaptiveResetVersion;
    private bool _adaptiveResetPending;
    private int _costDemand;
    private int _dashboardVisibility;
    private bool _flyoutVisible;
    private DateTime? _lastTrim;
    private bool _disposed;
    private bool _refreshPending;
    private bool _stopped;
    private bool _exiting;
    private TaskCompletionSource? _refreshCompletion;

    [ObservableProperty]
    private DateTime? _nextRefreshUtc;

    [ObservableProperty]
    private TimeSpan _effectiveRefreshInterval;

    public event EventHandler? ExitRequested;

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
        UsageHistoryPersistence historyPersistence,
        IActivityLog activityLog,
        ISessionStateSource sessionState,
        ICacheService cacheService,
        ISetupMemoService setupMemo)
    {
        _codexBarService = codexBarService;
        _settingsService = settingsService;
        _alertEvaluator = alertEvaluator;
        _toastNotifier = toastNotifier;
        _logger = logger;
        _history = history;
        _historyPersistence = historyPersistence;
        _activityLog = activityLog;
        _sessionState = sessionState;
        _cacheService = cacheService;
        _setupMemo = setupMemo;

        _settingsService.SettingsChanged += OnSettingsChanged;
        _sessionState.Locked += OnSessionLocked;
        _sessionState.Unlocked += OnSessionActivated;
        _sessionState.Resumed += OnSessionActivated;
    }

    /// <summary>
    /// Starts the periodic refresh.
    /// </summary>
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_exiting) return;
        var activeRefresh = _refreshCompletion?.Task;
        Stop();
        if (activeRefresh != null) await activeRefresh;
        if (_disposed || _exiting) return;
        _stopped = false;
        ResetAdaptiveRefresh();
        if (TryRestoreFreshCache()) return;
        await RefreshCoreAsync();
    }

    private bool TryRestoreFreshCache()
    {
        if (_settingsService.Settings.DeveloperModeEnabled) return false;
        var enabled = _settingsService.Settings.Providers.Where(p => p.IsEnabled).OrderBy(p => p.Order).ToList();
        if (enabled.Count == 0) return false;
        var interval = TimeSpan.FromSeconds(_settingsService.Settings.RefreshIntervalSeconds);
        var now = DateTime.UtcNow;
        var cached = enabled.Select(p => _cacheService.Get(p.Id)).ToList();
        if (cached.Any(data => data == null || data.HasError || data.FetchedAt.ToUniversalTime() > now ||
            now - data.FetchedAt.ToUniversalTime() >= interval))
            return false;
        var oldest = cached.Min(data => data!.FetchedAt.ToUniversalTime());
        Providers = new(cached.Select(data => data! with
        {
            IsLoading = false,
            Cost = _settingsService.Settings.ShowCostTotals ? data!.Cost : null
        }));
        _usageFingerprint = UsageFingerprint.Create(Providers);
        LastUpdated = oldest.ToLocalTime();
        UpdateLastUpdatedText();
        RecreateTimer(interval - (now - oldest));
        return true;
    }

    /// <summary>
    /// Stops the periodic refresh.
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        _refreshPending = false;
        _refreshCts?.Cancel();
        _timerCts?.Cancel();
        _timerCts?.Dispose();
        _timerCts = null;
        _costRefreshCts?.Cancel();
        _trimCts?.Cancel();
        NextRefreshUtc = null;
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        ResetAdaptiveRefresh();
        return RefreshCoreAsync(forceCostRefresh: true);
    }

    /// <summary>
    /// Refreshes visible data that is older than <paramref name="maxAge"/> (never less than the base refresh interval,
    /// so opening a window at normal cadence costs nothing extra) without forcing a new cost scan.
    /// </summary>
    public Task RefreshIfStaleAsync(TimeSpan maxAge)
    {
        var baseInterval = TimeSpan.FromSeconds(_settingsService.Settings.RefreshIntervalSeconds);
        if (maxAge < baseInterval) maxAge = baseInterval;
        if (_disposed || _stopped || _exiting ||
            (LastUpdated != default && DateTime.Now - LastUpdated <= maxAge)) return Task.CompletedTask;
        ResetAdaptiveRefresh();
        if (IsLoading) return _refreshCompletion?.Task ?? Task.CompletedTask;
        return RefreshCoreAsync();
    }

    /// <summary>Requests cost totals while a consumer is open.</summary>
    public IDisposable AcquireCostDemand()
    {
        if (Interlocked.Increment(ref _costDemand) == 1)
            OnUiThread(() => RefreshDemandedCostsAsync().SafeFireAndForget(
                onError: ex => _logger.LogError(ex, "Demanded cost refresh failed")));
        return new ActionLease(() =>
        {
            if (Interlocked.Decrement(ref _costDemand) == 0)
                OnUiThread(() =>
                {
                    if (Volatile.Read(ref _costDemand) == 0) _costRefreshCts?.Cancel();
                    ScheduleWorkingSetTrim();
                });
        });
    }

    public void SetFlyoutVisible(bool visible)
    {
        _flyoutVisible = visible;
        if (!visible) ScheduleWorkingSetTrim();
    }

    /// <summary>Tracks visible dashboards independently of their cost-demand lifetime.</summary>
    public IDisposable AcquireDashboardVisibility()
    {
        Interlocked.Increment(ref _dashboardVisibility);
        return new ActionLease(() =>
        {
            Interlocked.Decrement(ref _dashboardVisibility);
            OnUiThread(ScheduleWorkingSetTrim);
        });
    }

    private void OnUiThread(Action action)
    {
        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
            _uiContext.Post(_ => action(), null);
        else action();
    }

    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private void ResetAdaptiveRefresh()
    {
        _unchangedRefreshes = 0;
        _adaptiveResetVersion++;
        _adaptiveResetPending = true;
        EffectiveRefreshInterval = GetScheduledDelay();
    }

    private TimeSpan GetScheduledDelay()
    {
        var delay = AdaptiveRefreshPolicy.GetDelay(
            TimeSpan.FromSeconds(_settingsService.Settings.RefreshIntervalSeconds), _unchangedRefreshes,
            _settingsService.Settings.AdaptiveRefreshEnabled);
        var lockedInterval = TimeSpan.FromMinutes(_settingsService.Settings.LockedRefreshIntervalMinutes);
        return (_sessionLocked || QuerySessionLocked()) && lockedInterval > delay ? lockedInterval : delay;
    }

    private bool QuerySessionLocked()
    {
        try { return _sessionState.IsLocked; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query session lock state");
            return false;
        }
    }

    private void OnSessionLocked(object? sender, EventArgs e)
    {
        try
        {
            OnUiThread(() =>
            {
                try
                {
                    if (_disposed) return;
                    _sessionLocked = true;
                    EffectiveRefreshInterval = GetScheduledDelay();
                    if (!_stopped && !_exiting && !IsLoading) RecreateTimer();
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to apply session lock"); }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to handle session lock"); }
    }

    private void OnSessionActivated(object? sender, EventArgs e)
    {
        try
        {
            OnUiThread(() => HandleSessionActivationAsync().SafeFireAndForget(
                onError: ex => _logger.LogError(ex, "Failed to handle session activation")));
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to dispatch session activation"); }
    }

    private async Task HandleSessionActivationAsync()
    {
        if (_disposed) return;
        _sessionLocked = false;
        ResetAdaptiveRefresh();
        if (_stopped || _exiting) return;
        // Cancel even a fresh-data timer: its monotonic delay may have paused throughout sleep.
        RecreateTimer();
        await RefreshIfStaleAsync(TimeSpan.FromSeconds(_settingsService.Settings.RefreshIntervalSeconds));
        if (!_disposed && !_stopped && !_exiting) RecreateTimer();
    }

    private async Task RefreshDemandedCostsAsync()
    {
        if (_disposed || _stopped || _exiting || !_settingsService.Settings.ShowCostTotals ||
            _settingsService.Settings.DeveloperModeEnabled ||
            Volatile.Read(ref _costDemand) == 0 || _costRefreshCts != null) return;
        var enabled = _settingsService.Settings.Providers.Where(p => p.IsEnabled)
            .Select(p => p.Id).Where(provider => Providers.FirstOrDefault(p =>
                p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))?.Cost == null).ToList();
        if (enabled.Count == 0) return;
        using var scanCts = new CancellationTokenSource();
        _costRefreshCts = scanCts;
        try
        {
            await RefreshCostsAsync(enabled, false, scanCts.Token);
        }
        catch (OperationCanceledException) when (scanCts.IsCancellationRequested)
        {
            _logger.LogDebug("Obsolete demanded cost refresh canceled");
        }
        finally
        {
            if (ReferenceEquals(_costRefreshCts, scanCts)) _costRefreshCts = null;
            ScheduleWorkingSetTrim();
            // Reopening, restarting or changing settings can restore demand before cancellation settles.
            // Retry only canceled passes; a completed empty scan remains subject to the service cache.
            if (scanCts.IsCancellationRequested)
                RefreshDemandedCostsAsync().SafeFireAndForget(
                    onError: ex => _logger.LogError(ex, "Pending demanded cost refresh failed"));
        }
    }

    private async Task RefreshCostsAsync(IReadOnlyList<string> providers, bool forceRefresh, CancellationToken ct)
    {
        if (!_settingsService.Settings.ShowCostTotals || _settingsService.Settings.DeveloperModeEnabled) return;
        await foreach (var (provider, cost) in _codexBarService.GetCostsAsync(providers, forceRefresh, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!forceRefresh && Volatile.Read(ref _costDemand) == 0) return;
            var index = Providers.ToList().FindIndex(p => p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) Providers[index] = Providers[index] with
            {
                Cost = _settingsService.Settings.ShowCostTotals ? cost : null
            };
        }
    }

    private void ScheduleWorkingSetTrim()
    {
        if (_disposed || _stopped || _exiting) return;
        _trimCts?.Cancel();
        var trimCts = new CancellationTokenSource();
        _trimCts = trimCts;
        TrimWhenIdleAsync(trimCts).SafeFireAndForget(
            onError: ex => _logger.LogDebug(ex, "Idle working set trim failed"));
    }

    private async Task TrimWhenIdleAsync(CancellationTokenSource trimCts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), trimCts.Token);
            var now = DateTime.UtcNow;
            if (!_disposed && !_stopped && !_exiting && WorkingSetTrimmer.ShouldTrim(now, _lastTrim,
                _flyoutVisible, Volatile.Read(ref _dashboardVisibility) > 0, IsLoading || _costRefreshCts != null))
            {
                WorkingSetTrimmer.Trim();
                _lastTrim = now;
            }
        }
        catch (OperationCanceledException) when (trimCts.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_trimCts, trimCts)) _trimCts = null;
            trimCts.Dispose();
        }
    }

    private async Task RefreshCoreAsync(bool forceCostRefresh = false)
    {
        if (IsLoading || _stopped || _disposed || _exiting)
        {
            return;
        }

        // Check if this is the first load (no existing data)
        bool isFirstLoad = Providers.Count == 0;

        _timerCts?.Cancel();
        NextRefreshUtc = null;
        var resetVersion = _adaptiveResetVersion;
        var resetAdaptiveStreak = _adaptiveResetPending;
        _adaptiveResetPending = false;
        IsLoading = true;
        ErrorMessage = null;
        using var refreshCts = new CancellationTokenSource();
        _refreshCts = refreshCts;
        var ct = refreshCts.Token;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshCompletion = completion;
        var developerMode = _settingsService.Settings.DeveloperModeEnabled;
        var refreshStarted = Stopwatch.GetTimestamp();

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
                ResetAdaptiveRefresh();
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
            await foreach (var data in _codexBarService.GetAllUsageStreamAsync(ct))
            {
                // Services may finish despite cancellation; obsolete results must never reach UI or history.
                ct.ThrowIfCancellationRequested();
                if (data.CommandNotFound) await _setupMemo.InvalidateAsync(ct);
                try
                {
                    _activityLog.RecordRefresh(new DashboardRefreshEntry
                    {
                        TimestampUtc = DateTime.UtcNow, ProviderId = data.Provider, Success = !data.HasError,
                        DurationMs = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds),
                        Message = data.Error
                    });
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Refresh activity could not be recorded"); }
                if (!developerMode) _history.Record(data, DateTime.UtcNow);
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
            ct.ThrowIfCancellationRequested();
            await _historyPersistence.SaveAsync(ct: ct);
            if (!developerMode && Providers.All(p => !p.HasError))
            {
                // Serialize and persist away from the UI context; cache failures cannot fail a refresh.
                try { await Task.Run(() => _cacheService.SaveAsync(), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogWarning(ex, "Usage cache could not be saved"); }
            }
            // Demand-start scans run separately so usage never waits behind a slow scan.
            if (forceCostRefresh)
                await RefreshCostsAsync(Providers.Select(p => p.Provider).ToList(), true, ct);
            else if (Volatile.Read(ref _costDemand) > 0 && _costRefreshCts == null)
            {
                using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _costRefreshCts = scanCts;
                try
                {
                    await RefreshCostsAsync(Providers.Select(p => p.Provider).ToList(), false, scanCts.Token);
                }
                catch (OperationCanceledException) when (scanCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    _logger.LogDebug("Obsolete periodic cost refresh canceled");
                }
                finally
                {
                    if (ReferenceEquals(_costRefreshCts, scanCts)) _costRefreshCts = null;
                    if (scanCts.IsCancellationRequested && !ct.IsCancellationRequested)
                        RefreshDemandedCostsAsync().SafeFireAndForget(
                            onError: ex => _logger.LogError(ex, "Pending demanded cost refresh failed"));
                }
            }

            ct.ThrowIfCancellationRequested();
            var fingerprint = UsageFingerprint.Create(Providers);
            _unchangedRefreshes = resetAdaptiveStreak || resetVersion != _adaptiveResetVersion || forceCostRefresh ||
                fingerprint.HasChangedFrom(_usageFingerprint) ? 0 : _unchangedRefreshes + 1;
            _usageFingerprint = fingerprint;

            LastUpdated = DateTime.Now;
            UpdateLastUpdatedText();
            NotifyAlerts();

            _logger.LogInformation("Usage data refreshed, {Count} providers", Providers.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Obsolete usage refresh canceled");
        }
        catch (Exception ex)
        {
            ResetAdaptiveRefresh();
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
            _refreshCts = null;
            for (var i = 0; i < Providers.Count; i++)
                if (Providers[i].IsLoading) Providers[i] = Providers[i] with { IsLoading = false };
            IsLoading = false;
            completion.TrySetResult();
            if (!_disposed && !_stopped && !_exiting) RecreateTimer();
            ScheduleWorkingSetTrim();
            if (_refreshPending && !_disposed && !_stopped && !_exiting)
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
                try
                {
                    _activityLog.RecordAlert(new DashboardAlertEntry
                    {
                        TimestampUtc = DateTime.UtcNow, ProviderId = alert.Key.Split('|', 2)[0],
                        Title = alert.Title, Message = alert.Message
                    });
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Alert activity could not be recorded"); }
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
        if (_exiting) return;
        _exiting = true;
        _logger.LogInformation("Exit requested");
        var activeRefresh = _refreshCompletion?.Task;
        Stop();
        if (activeRefresh != null) await activeRefresh;
        await _historyPersistence.SaveAsync(force: true);
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunPeriodicRefreshAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            ct.ThrowIfCancellationRequested();
            // Recover from a missed unlock event before scheduling the next refresh.
            if (_sessionLocked && !QuerySessionLocked()) _sessionLocked = false;
            await RefreshCoreAsync();
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
            if (_disposed || _stopped || _exiting) return;
            _logger.LogDebug("Settings changed, recreating timer and refreshing data");
            ResetAdaptiveRefresh();
            _costRefreshCts?.Cancel();
            RecreateTimer();
            if (!_settingsService.Settings.ShowCostTotals)
                for (var i = 0; i < Providers.Count; i++) Providers[i] = Providers[i] with { Cost = null };
            if (IsLoading) { _refreshPending = true; _refreshCts?.Cancel(); return; }
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
    private void RecreateTimer(TimeSpan? firstDelay = null)
    {
        EffectiveRefreshInterval = firstDelay ?? GetScheduledDelay();
        if (_sessionLocked || QuerySessionLocked())
        {
            var lockedInterval = TimeSpan.FromMinutes(_settingsService.Settings.LockedRefreshIntervalMinutes);
            if (lockedInterval > EffectiveRefreshInterval) EffectiveRefreshInterval = lockedInterval;
        }
        _timerCts?.Cancel();
        _timerCts?.Dispose();
        _timerCts = new CancellationTokenSource();
        NextRefreshUtc = DateTime.UtcNow + EffectiveRefreshInterval;
        RunPeriodicRefreshAsync(EffectiveRefreshInterval, _timerCts.Token).SafeFireAndForget(
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
            _settingsService.SettingsChanged -= OnSettingsChanged;
            _sessionState.Locked -= OnSessionLocked;
            _sessionState.Unlocked -= OnSessionActivated;
            _sessionState.Resumed -= OnSessionActivated;
        }

        _disposed = true;
    }
}
