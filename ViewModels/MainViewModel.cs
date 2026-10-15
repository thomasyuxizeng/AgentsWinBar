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
    private readonly IEarlyResetStore? _earlyResetStore;
    private readonly IBatterySaverMonitor? _batterySaverMonitor;
    private readonly IDashboardService? _dashboardService;

    public bool ShouldPlayResetAnimation => ResetAnimationGate.ShouldAnimate(
        _settingsService.Settings.ResetAnimationMode, _batterySaverMonitor?.BatterySaverActive ?? false);
    private bool _sessionLocked;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _timerCts;
    private CancellationTokenSource? _costRefreshCts;
    private CancellationTokenSource? _backgroundCostRefreshCts;
    private bool _backgroundCostRefreshForced;
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
    private int _stopVersion;
    private TaskCompletionSource? _refreshCompletion;
    private TaskCompletionSource? _costRefreshCompletion;
    private TaskCompletionSource? _backgroundCostRefreshCompletion;
    private Task _retiredCostRefreshes = Task.CompletedTask;
    private bool _forceCostRefreshPending;
    private readonly UsageResetTracker _resetTracker = new();
    private readonly ResetAnimationVisibility _resetVisibility;
    private readonly HashSet<DateTime> _knownResetStamps = [];
    private readonly Dictionary<DateTime, ResetRefreshAttempt> _resetRefreshAttempts = [];
    private bool? _resetTrackerDeveloperMode;

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
        ISetupMemoService setupMemo,
        IEarlyResetStore? earlyResetStore = null,
        IBatterySaverMonitor? batterySaverMonitor = null,
        IDashboardService? dashboardService = null,
        ResetAnimationVisibility? resetVisibility = null)
    {
        _resetVisibility = resetVisibility ?? ResetAnimationVisibility.Shared;
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
        _earlyResetStore = earlyResetStore;
        _batterySaverMonitor = batterySaverMonitor;
        _dashboardService = dashboardService;
        if (_batterySaverMonitor != null) _batterySaverMonitor.BatterySaverChanged += OnBatterySaverChanged;

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
        var stopVersion = _stopVersion;
        if (activeRefresh != null) await activeRefresh;
        if (_disposed || _exiting || stopVersion != _stopVersion) return;
        _stopped = false;
        ResetAdaptiveRefresh();
        if (TryRestoreFreshCache())
        {
            RefreshApiCredits(false);
            return;
        }
        RestoreLastKnownUsage();
        await RefreshCoreAsync();
    }

    internal void RestoreLastKnownUsage()
    {
        if (_settingsService.Settings.DeveloperModeEnabled) return;
        var enabled = _settingsService.Settings.Providers.Where(p => p.IsEnabled && ProviderConstants.IsValidProvider(p.Id))
            .OrderBy(p => p.Order).Select(p => ProviderConstants.ValidateAndNormalize(p.Id)).ToList();
        var restoredByProvider = enabled.ToDictionary(provider => provider, provider =>
        {
            var cached = _cacheService.GetLastKnown(provider);
            if (cached != null) cached = CacheRetention.PruneExpired(cached, DateTime.UtcNow);
            return cached;
        });
        if (Providers.Count == 0 && restoredByProvider.Values.All(data => data == null)) return;
        ProviderListSynchronizer.Sync(Providers, enabled);
        for (var i = 0; i < Providers.Count; i++)
        {
            if (Providers[i].HasUsage) continue;
            var restored = restoredByProvider[Providers[i].Provider];
            if (restored == null) continue;
            Providers[i] = restored with { IsLoading = false, UsageIsStale = restored.HasUsage,
                StatusIsStale = restored.ServiceStatus != null,
                LastSuccessfulFetch = restored.HasUsage ? restored.LastSuccessfulFetch ?? restored.FetchedAt : null,
                Cost = _settingsService.Settings.ShowCostTotals ? restored.Cost : null };
        }
    }

    private bool TryRestoreFreshCache()
    {
        if (_settingsService.Settings.DeveloperModeEnabled) return false;
        var enabled = _settingsService.Settings.Providers.Where(p => p.IsEnabled).OrderBy(p => p.Order).ToList();
        if (enabled.Count == 0) return false;
        var interval = TimeSpan.FromSeconds(_settingsService.Settings.RefreshIntervalSeconds);
        var now = DateTime.UtcNow;
        var cached = enabled.Select(p => _cacheService.Get(p.Id) is { } data
            ? CacheRetention.PruneExpired(data, now) : null).ToList();
        if (cached.Any(data => data == null || data.HasError || CacheTimestampUtc(data.FetchedAt) > now ||
            now - CacheTimestampUtc(data.FetchedAt) >= interval))
            return false;
        var oldest = cached.Min(data => CacheTimestampUtc(data!.FetchedAt));
        Providers = new(cached.Select(data => data! with
        {
            IsLoading = false,
            Cost = _settingsService.Settings.ShowCostTotals ? data!.Cost : null
        }));
        ErrorMessage = null;
        _usageFingerprint = UsageFingerprint.Create(Providers);
        LastUpdated = oldest.ToLocalTime();
        UpdateLastUpdatedText();
        RecreateTimer(interval - (now - oldest));
        RefreshDemandedCostsAsync().SafeFireAndForget(
            onError: ex => _logger.LogError(ex, "Cached usage cost refresh failed"));
        return true;
    }

    private static DateTime CacheTimestampUtc(DateTime timestamp) => Helpers.DashboardValues.Utc(timestamp);

    /// <summary>
    /// Stops the periodic refresh.
    /// </summary>
    public void Stop()
    {
        _stopVersion++;
        _stopped = true;
        _refreshPending = false;
        _refreshCts?.Cancel();
        _timerCts?.Cancel();
        _timerCts?.Dispose();
        _timerCts = null;
        _costRefreshCts?.Cancel();
        _backgroundCostRefreshCts?.Cancel();
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
        var age = DateTime.Now - LastUpdated;
        if (_disposed || _stopped || _exiting ||
            (LastUpdated != default && age >= TimeSpan.Zero && age <= maxAge)) return Task.CompletedTask;
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
                    if (Volatile.Read(ref _costDemand) == 0)
                    {
                        _costRefreshCts?.Cancel();
                        if (!_backgroundCostRefreshForced) _backgroundCostRefreshCts?.Cancel();
                    }
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
        if (Interlocked.Increment(ref _dashboardVisibility) == 1) OnUiThread(() => RefreshApiCredits(false));
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
            _settingsService.Settings.AdaptiveRefreshEnabled, _batterySaverMonitor?.BatterySaverActive ?? false);
        var lockedInterval = TimeSpan.FromMinutes(_settingsService.Settings.LockedRefreshIntervalMinutes);
        return (_sessionLocked || QuerySessionLocked()) && lockedInterval > delay ? lockedInterval : delay;
    }

    private bool IsSessionLocked => _sessionLocked || QuerySessionLocked();

    private bool QuerySessionLocked()
    {
        try { return _sessionState.IsLocked; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query session lock state");
            return false;
        }
    }

    // A reset detected on a locked screen is remembered, so its bar animates once when the session is back (not unseen)
    private void TrackResetStamps(UsageData data)
    {
        var locked = QuerySessionLocked();
        var now = DateTime.UtcNow;
        foreach (var window in new[] { data.Session, data.Weekly, data.Tertiary }.Concat(data.ExtraWindows.Select(e => e.Window)))
        {
            if (window?.ResetDetectedAtUtc is not { } stamp) continue;
            // A stamp is the detection time, so only a fresh one can have been detected on this locked screen
            if (_knownResetStamps.Add(stamp) && locked && now - stamp.ToUniversalTime() <= TimeSpan.FromMinutes(5))
                _resetVisibility.NoteUnseen(stamp);
        }
        if (_knownResetStamps.Count > 64) _knownResetStamps.Clear();
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
        // Resuming from sleep can land on the lock screen: the bars can be seen only once the session is unlocked
        if (!QuerySessionLocked()) _resetVisibility.NoteVisible(DateTime.UtcNow);
        ResetAdaptiveRefresh();
        if (_stopped || _exiting) return;
        // Cancel even a fresh-data timer: its monotonic delay may have paused throughout sleep.
        if (!IsLoading) RecreateTimer();
        await RefreshIfStaleAsync(TimeSpan.FromSeconds(_settingsService.Settings.RefreshIntervalSeconds));
        if (!_disposed && !_stopped && !_exiting && !IsLoading) RecreateTimer();
    }

    private async Task RefreshDemandedCostsAsync()
    {
        if (_disposed || _stopped || _exiting || !_settingsService.Settings.ShowCostTotals ||
            _settingsService.Settings.DeveloperModeEnabled || IsSessionLocked ||
            Volatile.Read(ref _costDemand) == 0 || _costRefreshCts != null ||
            _backgroundCostRefreshCts != null || _forceCostRefreshPending) return;
        var enabled = _settingsService.Settings.Providers.Where(p => p.IsEnabled)
            .Select(p => p.Id).Where(provider => Providers.FirstOrDefault(p =>
                p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))?.Cost is not { } cost ||
                CacheRetention.RetainedCost(cost, DateTime.UtcNow) == null).ToList();
        if (enabled.Count == 0) return;
        using var scanCts = new CancellationTokenSource();
        _costRefreshCts = scanCts;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _costRefreshCompletion = completion;
        try
        {
            await RefreshCostsAsync(enabled, false, scanCts);
        }
        catch (OperationCanceledException) when (scanCts.IsCancellationRequested)
        {
            _logger.LogDebug("Obsolete demanded cost refresh canceled");
        }
        finally
        {
            if (ReferenceEquals(_costRefreshCts, scanCts)) _costRefreshCts = null;
            if (ReferenceEquals(_costRefreshCompletion, completion)) _costRefreshCompletion = null;
            completion.TrySetResult();
            // Reopening, restarting or changing settings can restore demand before cancellation settles.
            // Retry only canceled passes; a completed empty scan remains subject to the service cache.
            if (scanCts.IsCancellationRequested)
                RefreshDemandedCostsAsync().SafeFireAndForget(
                    onError: ex => _logger.LogError(ex, "Pending demanded cost refresh failed"));
        }
    }

    private async Task RefreshBackgroundCostsAsync(IReadOnlyList<string> providers, bool forceRefresh)
    {
        if (_disposed || _stopped || _exiting || !_settingsService.Settings.ShowCostTotals ||
            _settingsService.Settings.DeveloperModeEnabled) return;
        // A window left open behind the lock screen must not boot the WSL VM for a cost scan nobody can see
        if (!forceRefresh && (IsSessionLocked || Volatile.Read(ref _costDemand) == 0 || _costRefreshCts != null ||
            _backgroundCostRefreshCts != null || _forceCostRefreshPending)) return;

        var activeDemand = forceRefresh ? _costRefreshCompletion?.Task : null;
        var previousBackground = _backgroundCostRefreshCompletion?.Task;
        if (forceRefresh)
        {
            _costRefreshCts?.Cancel();
            _backgroundCostRefreshCts?.Cancel();
        }
        // This scan owns its lifetime independently of the completed usage refresh. Replace the owner before
        // awaiting cleanup so superseded scans cannot publish or clear the newer scan's state.
        using var scanCts = new CancellationTokenSource();
        _backgroundCostRefreshCts = scanCts;
        _backgroundCostRefreshForced = forceRefresh;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _backgroundCostRefreshCompletion = completion;
        if (previousBackground != null)
            _retiredCostRefreshes = _retiredCostRefreshes.IsCompleted
                ? previousBackground : Task.WhenAll(_retiredCostRefreshes, previousBackground);
        if (forceRefresh) _forceCostRefreshPending = false;
        try
        {
            if (activeDemand != null)
            {
                try { await activeDemand.WaitAsync(TimeSpan.FromSeconds(2), scanCts.Token); }
                catch (TimeoutException) { _logger.LogDebug("Demand cost scan did not stop in time; continuing with the forced scan"); }
            }
            scanCts.Token.ThrowIfCancellationRequested();
            await RefreshCostsAsync(providers, forceRefresh, scanCts, backgroundScan: true);
        }
        catch (OperationCanceledException) when (scanCts.IsCancellationRequested)
        {
            _logger.LogDebug("Obsolete background cost refresh canceled");
        }
        finally
        {
            if (ReferenceEquals(_backgroundCostRefreshCts, scanCts))
            {
                _backgroundCostRefreshCts = null;
                _backgroundCostRefreshForced = false;
            }
            if (ReferenceEquals(_backgroundCostRefreshCompletion, completion)) _backgroundCostRefreshCompletion = null;
            completion.TrySetResult();
            if (scanCts.IsCancellationRequested)
                RefreshDemandedCostsAsync().SafeFireAndForget(
                    onError: ex => _logger.LogError(ex, "Pending demanded cost refresh failed"));
        }
    }

    private async Task RefreshCostsAsync(IReadOnlyList<string> providers, bool forceRefresh,
        CancellationTokenSource scanCts, bool backgroundScan = false)
    {
        if (!_settingsService.Settings.ShowCostTotals || _settingsService.Settings.DeveloperModeEnabled) return;
        var ct = scanCts.Token;
        await foreach (var (provider, cost) in _codexBarService.GetCostsAsync(providers, forceRefresh, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!forceRefresh && Volatile.Read(ref _costDemand) == 0) return;
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            OnUiThread(() =>
            {
                try
                {
                    // Recheck on the dispatcher: cancellation, replacement or disposal may happen while queued.
                    if (_disposed || _stopped || _exiting || ct.IsCancellationRequested ||
                        !ReferenceEquals(backgroundScan ? _backgroundCostRefreshCts : _costRefreshCts, scanCts) ||
                        !_settingsService.Settings.ShowCostTotals || _settingsService.Settings.DeveloperModeEnabled ||
                        (!forceRefresh && Volatile.Read(ref _costDemand) == 0)) return;
                    var index = Providers.ToList().FindIndex(p => p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) Providers[index] = Providers[index] with { Cost = cost };
                }
                catch (Exception ex) { published.TrySetException(ex); }
                finally { published.TrySetResult(); }
            });
            // Keep the scan owner alive until queued UI results have been applied or rejected.
            await published.Task.WaitAsync(ct);
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
                _flyoutVisible, Volatile.Read(ref _dashboardVisibility) > 0,
                IsLoading || _costRefreshCts != null || _backgroundCostRefreshCts != null))
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

    private async Task RefreshCoreAsync(bool forceCostRefresh = false, IReadOnlyList<EarlyResetWindowReading>? resetTargets = null)
    {
        if (_stopped || _disposed || _exiting)
        {
            return;
        }

        RefreshApiCredits(forceCostRefresh);

        if (forceCostRefresh)
        {
            _forceCostRefreshPending = true;
            // Supersede cost scans even when the manual request joins an existing usage refresh.
            _costRefreshCts?.Cancel();
            _backgroundCostRefreshCts?.Cancel();
        }
        if (IsLoading) return;
        forceCostRefresh |= _forceCostRefreshPending;

        // Check if this is the first load (no existing data)
        bool isFirstLoad = Providers.Count == 0;

        _timerCts?.Cancel();
        NextRefreshUtc = null;
        var resetVersion = _adaptiveResetVersion;
        var resetAdaptiveStreak = _adaptiveResetPending;
        _adaptiveResetPending = false;
        _forceCostRefreshPending = forceCostRefresh;
        IsLoading = true;
        ErrorMessage = null;
        using var refreshCts = new CancellationTokenSource();
        _refreshCts = refreshCts;
        var ct = refreshCts.Token;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshCompletion = completion;
        var developerMode = _settingsService.Settings.DeveloperModeEnabled;
        if (_resetTrackerDeveloperMode != developerMode) _resetTracker.Clear();
        _resetTrackerDeveloperMode = developerMode;
        if (!developerMode && !_historyPersistence.IsLoaded)
        {
            await _historyPersistence.LoadAsync(ct);
        }
        List<string>? costProviders = null;
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
                _resetTracker.RetainProviders([]);
                ResetAdaptiveRefresh();
                Providers.Clear();
                ErrorMessage = "No providers enabled. Go to Settings to enable providers.";
                return;
            }

            // Follow Settings on every refresh: add newly enabled providers, drop disabled ones, keep the order
            _resetTracker.RetainProviders(enabledProviders.Select(p => p.Id));
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
                ct.ThrowIfCancellationRequested();
                var resultAtUtc = DateTime.UtcNow;
                var resetOutcome = resetTargets == null ? null : ResetRefreshOutcome.Describe(resetTargets, data, resultAtUtc);
                if (resetOutcome != null) _logger.LogInformation("Reset refresh {Provider}: {Outcome}", data.Provider, resetOutcome);
                try
                {
                    _activityLog.RecordRefresh(new DashboardRefreshEntry
                    {
                        TimestampUtc = resultAtUtc, ProviderId = data.Provider, Success = !data.HasError,
                        DurationMs = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds),
                        Message = resetOutcome == null ? data.Error : $"Reset refresh: {resetOutcome}"
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
                    Providers[index] = _resetTracker.Annotate(
                        UsageChangeTracker.Annotate(data, previous, DateTime.UtcNow), DateTime.UtcNow) with
                    {
                        Cost = _settingsService.Settings.ShowCostTotals
                            ? CacheRetention.RetainedCost(previous.Cost, DateTime.UtcNow) : null
                    };
                    TrackResetStamps(Providers[index]);
                }
            }

            // Usage appears as soon as it arrives. The heavier log scans run afterwards, at a slow cadence.
            ct.ThrowIfCancellationRequested();
            if (!developerMode) await _historyPersistence.SaveAsync(ct: ct);
            ct.ThrowIfCancellationRequested();
            var fingerprint = UsageFingerprint.Create(Providers);
            _unchangedRefreshes = resetAdaptiveStreak || resetVersion != _adaptiveResetVersion || forceCostRefresh ||
                fingerprint.HasChangedFrom(_usageFingerprint) ? 0 : _unchangedRefreshes + 1;
            _usageFingerprint = fingerprint;

            LastUpdated = DateTime.Now;
            UpdateLastUpdatedText();
            NotifyAlerts();

            _logger.LogInformation("Usage data refreshed, {Count} providers", Providers.Count);
            costProviders = Providers.Select(p => p.Provider).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Obsolete usage refresh canceled");
        }
        catch (Exception ex)
        {
            if (_disposed || _stopped || _exiting || ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Obsolete usage refresh failed during cancellation");
                return;
            }
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
            if (!_disposed)
            {
                for (var i = 0; i < Providers.Count; i++)
                    if (Providers[i].IsLoading) Providers[i] = Providers[i] with { IsLoading = false };
                IsLoading = false;
            }
            completion.TrySetResult();
            if (!_disposed && !_stopped && !_exiting) RecreateTimer();
            // Finish usage and restore its timer before starting any potentially slow cost work.
            // Keep the forced-pending guard until the background scan registers its owner synchronously.
            forceCostRefresh |= _forceCostRefreshPending;
            if (costProviders != null && !ct.IsCancellationRequested && !_refreshPending)
                RefreshBackgroundCostsAsync(costProviders, forceCostRefresh).SafeFireAndForget(
                    onError: ex => _logger.LogError(ex, "Background cost refresh failed"));
            _forceCostRefreshPending = false;
            if (_refreshPending && !_disposed && !_stopped && !_exiting)
            {
                _refreshPending = false;
                RefreshCoreAsync(forceCostRefresh).SafeFireAndForget(onError: ex => _logger.LogError(ex, "Pending settings refresh failed"));
            }
        }
    }

    private void RefreshApiCredits(bool force)
    {
        // Only the dashboard shows the API credits: without a visible one a background poll is wasted network wake-ups.
        // The dashboard asks again when it becomes visible; a manual refresh (force) always goes through.
        if (_dashboardService == null || (!force && Volatile.Read(ref _dashboardVisibility) == 0)) return;
        _dashboardService.RefreshApiCreditsAsync(force).SafeFireAndForget(
            onError: _ => _logger.LogDebug("Background API billing refresh did not complete"));
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
        var activeDemand = _costRefreshCompletion?.Task;
        var activeBackground = Task.WhenAll(_retiredCostRefreshes,
            _backgroundCostRefreshCompletion?.Task ?? Task.CompletedTask);
        Stop();
        if (activeRefresh != null) await activeRefresh;
        // Iterator disposal waits for the service producer to persist completed costs even on cancellation.
        if (activeDemand != null) await activeDemand;
        await activeBackground;
        if (!_settingsService.Settings.DeveloperModeEnabled) await _historyPersistence.SaveAsync(force: true);
        if (_earlyResetStore != null) await _earlyResetStore.FlushAsync();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunPeriodicRefreshAsync(ResetRefreshSchedule schedule, CancellationToken ct)
    {
        try
        {
            await Task.Delay(schedule.Delay, ct);
            ct.ThrowIfCancellationRequested();
            // Recover from a missed unlock event before scheduling the next refresh.
            if (_sessionLocked && !QuerySessionLocked()) _sessionLocked = false;
            if (_disposed || _stopped || _exiting || IsLoading) return;
            IReadOnlyList<EarlyResetWindowReading>? targets = null;
            if (schedule.TargetResetAtUtc is { } reset)
            {
                if (_sessionLocked || QuerySessionLocked())
                {
                    RecreateTimer();
                    return;
                }
                // An obsolete/duplicate callback must not consume another attempt for this instant.
                var attempted = _resetRefreshAttempts.TryGetValue(reset, out var attempt);
                if (schedule.IsRetry ? !attempted || attempt!.RetryTriggered : attempted) return;
                var enabled = _settingsService.Settings.Providers.Where(p => p.IsEnabled)
                    .Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                targets = Providers.Where(p => enabled.Contains(p.Provider)).SelectMany(EarlyResetDetector.Readings)
                    .Where(r => r.ResetAt.HasValue && DashboardValues.Utc(r.ResetAt.Value) == reset).ToArray();
                _resetRefreshAttempts[reset] = schedule.IsRetry
                    ? _resetRefreshAttempts[reset] with { RetryTriggered = true }
                    : new ResetRefreshAttempt(DateTime.UtcNow);
                _logger.LogInformation("Refreshing after usage reset {ResetAtUtc}; retry: {IsRetry}", reset, schedule.IsRetry);
            }
            await RefreshCoreAsync(resetTargets: targets);
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

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            OnUiThread(() =>
            {
                if (_disposed) return;
                OnPropertyChanged(nameof(ShouldPlayResetAnimation));
                HandleSettingsChangeAsync().SafeFireAndForget(
                    onError: ex => _logger.LogError(ex, "Failed to handle settings change"));
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to dispatch settings change"); }
    }

    private void OnBatterySaverChanged(object? sender, EventArgs e)
    {
        try
        {
            OnUiThread(() =>
            {
                if (_disposed) return;
                OnPropertyChanged(nameof(ShouldPlayResetAnimation));
                // Battery saver stretches the refresh interval: apply it (or lift it) to the pending refresh too.
                EffectiveRefreshInterval = GetScheduledDelay();
                if (!_stopped && !_exiting && !IsLoading) RecreateTimer();
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to dispatch battery saver change"); }
    }

    private async Task HandleSettingsChangeAsync()
    {
        try
        {
            if (_disposed || _stopped || _exiting) return;
            _logger.LogDebug("Settings changed, recreating timer and refreshing data");
            _resetTracker.RetainProviders(_settingsService.Settings.Providers.Where(p => p.IsEnabled).Select(p => p.Id));
            ResetAdaptiveRefresh();
            _costRefreshCts?.Cancel();
            _backgroundCostRefreshCts?.Cancel();
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
        if (_disposed || _stopped || _exiting || IsLoading) return;
        var now = DateTime.UtcNow;
        var normalDelay = firstDelay ?? GetScheduledDelay();
        if (normalDelay < ResetRefreshScheduler.MinimumDelay) normalDelay = ResetRefreshScheduler.MinimumDelay;
        var locked = _sessionLocked || QuerySessionLocked();
        if (locked)
        {
            var lockedInterval = TimeSpan.FromMinutes(_settingsService.Settings.LockedRefreshIntervalMinutes);
            if (lockedInterval > normalDelay) normalDelay = lockedInterval;
        }
        var enabled = _settingsService.Settings.Providers.Where(p => p.IsEnabled).Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resetTimes = Providers.Where(p => enabled.Contains(p.Provider))
            .SelectMany(p => new[] { p.Session, p.Weekly, p.Tertiary }
                .Concat(p.ExtraWindows.Select(e => e.Window))).Select(w => w?.ResetAt);
        var schedule = locked ? new ResetRefreshSchedule(normalDelay)
            : ResetRefreshScheduler.GetSchedule(resetTimes, now, normalDelay, _resetRefreshAttempts);
        EffectiveRefreshInterval = schedule.Delay;
        _timerCts?.Cancel();
        _timerCts?.Dispose();
        _timerCts = new CancellationTokenSource();
        NextRefreshUtc = now + EffectiveRefreshInterval;
        RunPeriodicRefreshAsync(schedule, _timerCts.Token).SafeFireAndForget(
            onError: ex => _logger.LogError(ex, "Periodic refresh task failed"));
        // Unlock events are not reliable (they never reached the app in testing), and a locked session sleeps for up
        // to LockedRefreshIntervalMinutes: while locked, poll cheaply so an unlock is noticed within seconds.
        if (_sessionLocked || QuerySessionLocked())
            WatchForUnlockAsync(_timerCts.Token).SafeFireAndForget(
                onError: ex => _logger.LogError(ex, "Unlock watcher failed"));
    }

    /// <summary>How often a locked session is checked for having been unlocked (the lock-state query is a cheap call).</summary>
    public TimeSpan UnlockPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    private async Task WatchForUnlockAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(UnlockPollInterval, ct);
                if (_disposed || _stopped || _exiting) return;
                if (QuerySessionLocked()) continue;
                OnUiThread(() => HandleSessionActivationAsync().SafeFireAndForget(
                    onError: ex => _logger.LogError(ex, "Failed to handle detected unlock")));
                return;
            }
        }
        catch (OperationCanceledException) { }
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
            if (_batterySaverMonitor != null) _batterySaverMonitor.BatterySaverChanged -= OnBatterySaverChanged;
            _sessionState.Locked -= OnSessionLocked;
            _sessionState.Unlocked -= OnSessionActivated;
            _sessionState.Resumed -= OnSessionActivated;
        }

        _disposed = true;
    }
}
