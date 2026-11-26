using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Services.Native;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.ViewModels;

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
    private CancellationTokenSource? _apiCreditsCts;
    private Task _apiCreditsTask = Task.CompletedTask;
    private bool _apiCreditsPending;
    private bool _apiCreditsForcePending;

    public bool ShouldPlayResetAnimation => ResetAnimationGate.ShouldAnimate(
        _settingsService.Settings.ResetAnimationMode, _batterySaverMonitor?.BatterySaverActive ?? false);
    private bool _sessionLocked;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _timerCts;
    private DateTime _timerStartedUtc;
    // Set just before a RecreateTimer call that continues the wait of the pending refresh instead of restarting it
    private DateTime? _carryOverSinceUtc;
    private CancellationTokenSource? _costRefreshCts;
    private CancellationTokenSource? _backgroundCostRefreshCts;
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
    private DateTime? _lastCompact;
    private bool _hasCompactedAfterStartup;
    private bool _dashboardTrimPending;
    private long _baselineHeapBytes;
    private bool _disposed;
    private bool _refreshPending;
    private readonly ISignInWatcher? _signInWatcher;
    private readonly IAdminKeyService? _adminKeys;
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
    // Per-provider schedule ("Refresh faster while a tool is in use"). All touched on the UI thread only.
    private readonly IProviderActivitySource? _toolActivity;
    private readonly ILogger _scheduleLog;
    private readonly Dictionary<string, DateTime> _lastFetchUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastResultUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _providerStreaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageFingerprint> _providerFingerprints = new(StringComparer.OrdinalIgnoreCase);
    // The providers the running refresh reads; null while it reads every enabled provider (or none runs)
    private HashSet<string>? _inFlightProviders;
    // Asked for during a partial refresh: read right after it (a full follow-up, _refreshPending, covers them)
    private readonly HashSet<string> _pendingProviders = new(StringComparer.OrdinalIgnoreCase);
    private RefreshStartLog? _pendingLog;
    private RefreshStartLog? _pendingPartialLog;
    /// <summary>A refresh that fires up to this much before a provider is due (timer and wall clock disagree) still takes it.</summary>
    private static readonly TimeSpan DueTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Which providers refresh faster right now (shown in the dashboard header).</summary>
    [ObservableProperty]
    private RefreshCadence _refreshCadence = RefreshCadence.None;

    [ObservableProperty]
    private DateTime? _nextRefreshUtc;

    [ObservableProperty]
    private TimeSpan _effectiveRefreshInterval;

    public event EventHandler? ExitRequested;

    /// <summary>Raised on the UI thread when an API credits read finished, so a visible dashboard can show it at once.</summary>
    public event EventHandler? ApiCreditsRefreshed;

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
        ResetAnimationVisibility? resetVisibility = null,
        ISignInWatcher? signInWatcher = null,
        IAdminKeyService? adminKeys = null,
        IProviderActivitySource? toolActivity = null,
        ILogger<RefreshScheduleLog>? scheduleLog = null)
    {
        _toolActivity = toolActivity;
        _scheduleLog = scheduleLog ?? (ILogger)logger;
        if (_toolActivity != null) _toolActivity.ActivityStarted += OnToolActivityStarted;
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
        _signInWatcher = signInWatcher;
        if (_signInWatcher != null) _signInWatcher.RefreshRequested += OnSignInRefreshRequested;
        _adminKeys = adminKeys;
        if (_adminKeys != null) _adminKeys.KeyChanged += OnAdminKeyChanged;

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
        await RefreshCoreAsync(log: new("Startup", RefreshReason.First));
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
            Providers[i] = restored with { IsLoading = false, AuthRequired = false, AuthMessage = null, UsageIsStale = restored.HasUsage,
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
        var restored = cached.Select(data => data! with
        {
            IsLoading = false,
            AuthRequired = false, AuthMessage = null,
            Cost = _settingsService.Settings.ShowCostTotals ? data!.Cost : null
        }).ToList();
        // Replace the items, never the collection: the flyout page, strip, overlay, dashboard and Settings subscribe to
        // this collection's CollectionChanged when they are created. A new collection left them listening to the old
        // one, so after a restart within one refresh interval the first flyout showed no cards until it was rebuilt.
        for (var i = 0; i < restored.Count; i++)
        {
            if (i < Providers.Count) Providers[i] = restored[i];
            else Providers.Add(restored[i]);
        }
        while (Providers.Count > restored.Count) Providers.RemoveAt(Providers.Count - 1);
        ErrorMessage = null;
        _usageFingerprint = UsageFingerprint.Create(Providers);
        foreach (var data in restored)
        {
            if (!ProviderConstants.IsValidProvider(data.Provider)) continue;
            var provider = ProviderConstants.ValidateAndNormalize(data.Provider);
            _lastFetchUtc[provider] = _lastResultUtc[provider] = CacheTimestampUtc(data.FetchedAt);
            _providerFingerprints[provider] = UsageFingerprint.Create([data]);
        }
        // Per provider the header shows the newest reading, as after any refresh (each provider's own stamp decides when
        // it is read again); today's single schedule keeps the oldest, which decides its next refresh.
        LastUpdated = (ActivityRefreshOn ? cached.Max(data => CacheTimestampUtc(data!.FetchedAt)) : oldest).ToLocalTime();
        UpdateLastUpdatedText();
        // Per provider, each cached reading's own age decides when it is due; otherwise the oldest one does.
        if (ActivityRefreshOn) RecreateTimer();
        else RecreateTimer(interval - (now - oldest));
        RefreshDemandedCostsAsync().SafeFireAndForget(
            onError: ex => _logger.LogError(ex, "Cached usage cost refresh failed"));
        ScheduleWorkingSetTrim();
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
        CancelApiCredits();
        _refreshPending = false;
        _pendingProviders.Clear();
        _pendingLog = _pendingPartialLog = null;
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
        return RefreshCoreAsync(forceCostRefresh: true, log: new("Manual", RefreshReason.Manual));
    }

    /// <summary>
    /// Refreshes visible data that is older than <paramref name="maxAge"/> (never less than the base refresh interval,
    /// so opening a window at normal cadence costs nothing extra) without forcing a new cost scan.
    /// </summary>
    public Task RefreshIfStaleAsync(TimeSpan maxAge)
    {
        var baseInterval = TimeSpan.FromSeconds(_settingsService.Settings.RefreshIntervalSeconds);
        if (maxAge < baseInterval) maxAge = baseInterval;
        // Per provider, its own stamps decide even before any refresh has finished (a first refresh that failed)
        if (ActivityRefreshOn) return RefreshStaleProvidersAsync(maxAge);
        var age = DateTime.Now - LastUpdated;
        if (_disposed || _stopped || _exiting ||
            (LastUpdated != default && age >= TimeSpan.Zero && age <= maxAge)) return Task.CompletedTask;
        ResetAdaptiveRefresh();
        if (IsLoading) return _refreshCompletion?.Task ?? Task.CompletedTask;
        return RefreshCoreAsync(log: new("OnOpen", RefreshReason.OnOpen));
    }

    /// <summary>
    /// Refresh-on-open under the per-provider schedule: providers refresh at different times, so each one's own last
    /// good answer decides, and only the providers older than <paramref name="maxAge"/> are read. Ages are UTC stamps, so
    /// a DST or time zone change makes nothing look stale. A provider is never read before its floor since its last
    /// attempt (Claude's 2 minutes) or during its 429 cooldown, however old its reading.
    /// </summary>
    private Task RefreshStaleProvidersAsync(TimeSpan maxAge)
    {
        if (_disposed || _stopped || _exiting) return Task.CompletedTask;
        var now = DateTime.UtcNow;
        // A provider's last answer, not its last attempt: a refresh that failed is tried again when a window opens
        var stale = BuildPlan(now).Where(due => ProviderRefreshPlanner.MayReadNow(due, now) &&
                (!_lastResultUtc.TryGetValue(due.Provider, out var last) || now < last || now - last > maxAge))
            .Select(due => due.Provider).ToList();
        if (stale.Count == 0) return Task.CompletedTask;
        ResetAdaptiveRefresh();
        var log = new RefreshStartLog("OnOpen", RefreshReason.OnOpen);
        if (IsLoading)
        {
            // The stale providers the running refresh does not read are read right after it
            QueueBehindInFlight(stale, log);
            return _refreshCompletion?.Task ?? Task.CompletedTask;
        }
        return RefreshCoreAsync(onlyProviders: stale, log: log);
    }

    /// <summary>
    /// A sign-in button was used and the provider's tool wrote a new sign-in (or, for Gemini, it is time to re-check):
    /// refresh now, or right after the refresh that is running. A re-check is skipped once the provider is signed in.
    /// </summary>
    private void OnSignInRefreshRequested(object? sender, SignInRefreshRequest request) => OnUiThread(() =>
    {
        try
        {
            if (_disposed || _stopped || _exiting) return;
            if (!request.CredentialChanged && !IsSignedOut(request.Provider))
            {
                _signInWatcher?.Cancel(request.Provider);
                return;
            }
            ResetAdaptiveRefresh();
            var log = new RefreshStartLog("SignIn", RefreshReason.Manual);
            if (IsLoading) { QueueFullRefresh(log); return; }
            RefreshCoreAsync(log: log).SafeFireAndForget(onError: ex => _logger.LogError(ex, "Sign-in refresh failed"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-in refresh request failed");
        }
    });

    /// <summary>The provider is shown as signed out (missing or rejected sign-in). Call on the UI thread.</summary>
    public bool IsSignedOut(string provider) =>
        Providers.Any(p => p.AuthRequired && p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));

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
            if (Interlocked.Decrement(ref _dashboardVisibility) == 0)
            {
                OnUiThread(() =>
                {
                    _dashboardTrimPending = true;
                    ScheduleWorkingSetTrim();
                });
            }
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
        _providerStreaks.Clear();
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
        NoteWindowResetStamp(data.Session, locked, now);
        NoteWindowResetStamp(data.Weekly, locked, now);
        NoteWindowResetStamp(data.Tertiary, locked, now);
        foreach (var extra in data.ExtraWindows)
            NoteWindowResetStamp(extra.Window, locked, now);
        if (_knownResetStamps.Count > 64) _knownResetStamps.Clear();
    }

    private void NoteWindowResetStamp(UsageWindow? window, bool locked, DateTime nowUtc)
    {
        if (window?.ResetDetectedAtUtc is not { } stamp) return;
        // A stamp is the detection time, so only a fresh one can have been detected on this locked screen
        if (_knownResetStamps.Add(stamp) && locked && nowUtc - stamp.ToUniversalTime() <= TimeSpan.FromMinutes(5))
            _resetVisibility.NoteUnseen(stamp);
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
            else
                ScheduleWorkingSetTrim();
        }
    }

    private async Task RefreshBackgroundCostsAsync(IReadOnlyList<string> providers, bool forceRefresh)
    {
        // Every early return still schedules the idle trim: the usage refresh hands its trim to this method, and
        // the common case (no window open, so no cost demand) returns here. Without it the app never trimmed after
        // a refresh and idled at about 75 MB private working set instead of under 20.
        if (_disposed || _stopped || _exiting || !_settingsService.Settings.ShowCostTotals ||
            _settingsService.Settings.DeveloperModeEnabled)
        {
            ScheduleWorkingSetTrim();
            return;
        }
        // A window left open behind the lock screen must not boot the WSL VM for a cost scan nobody can see
        if (!forceRefresh && (IsSessionLocked || Volatile.Read(ref _costDemand) == 0 || _costRefreshCts != null ||
            _backgroundCostRefreshCts != null || _forceCostRefreshPending))
        {
            ScheduleWorkingSetTrim();
            return;
        }

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
            }
            if (ReferenceEquals(_backgroundCostRefreshCompletion, completion)) _backgroundCostRefreshCompletion = null;
            completion.TrySetResult();
            if (scanCts.IsCancellationRequested)
                RefreshDemandedCostsAsync().SafeFireAndForget(
                    onError: ex => _logger.LogError(ex, "Pending demanded cost refresh failed"));
            else
                ScheduleWorkingSetTrim();
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
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            OnUiThread(() =>
            {
                try
                {
                    // Recheck on the dispatcher: cancellation, replacement or disposal may happen while queued.
                    if (_disposed || _stopped || _exiting || ct.IsCancellationRequested ||
                        !ReferenceEquals(backgroundScan ? _backgroundCostRefreshCts : _costRefreshCts, scanCts) ||
                        !_settingsService.Settings.ShowCostTotals || _settingsService.Settings.DeveloperModeEnabled) return;
                    var index = -1;
                    for (var i = 0; i < Providers.Count; i++)
                    {
                        if (Providers[i].Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                        {
                            index = i;
                            break;
                        }
                    }
                    if (index >= 0 && !DashboardContent.CostDataEquals(Providers[index].Cost, cost))
                        Providers[index] = Providers[index] with { Cost = cost };
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
                var currentHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
                var compactLoh = WorkingSetTrimmer.ShouldCompactLoh(
                    now,
                    _lastCompact,
                    _hasCompactedAfterStartup,
                    _dashboardTrimPending,
                    currentHeapBytes,
                    _baselineHeapBytes);

                await Task.Run(() => WorkingSetTrimmer.Trim(compactLoh), trimCts.Token);

                _lastTrim = now;
                if (compactLoh)
                {
                    _hasCompactedAfterStartup = true;
                    _dashboardTrimPending = false;
                    _lastCompact = now;
                    _baselineHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
                }
                else if (_baselineHeapBytes == 0)
                {
                    _baselineHeapBytes = currentHeapBytes;
                }
            }
        }
        catch (OperationCanceledException) when (trimCts.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_trimCts, trimCts)) _trimCts = null;
            trimCts.Dispose();
        }
    }

    /// <param name="onlyProviders">
    /// Read only these providers (the per-provider schedule); the others keep their readings and fetch times. Null
    /// (and a list naming every enabled provider) reads them all.
    /// </param>
    /// <param name="log">The schedule log lines, written only when the refresh actually starts.</param>
    private async Task RefreshCoreAsync(bool forceCostRefresh = false, IReadOnlyList<EarlyResetWindowReading>? resetTargets = null,
        IReadOnlyCollection<string>? onlyProviders = null, RefreshStartLog? log = null)
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
        if (IsLoading)
        {
            QueueBehindInFlight(onlyProviders, log);
            return;
        }
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
        _inFlightProviders = PartialSet(onlyProviders);
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
        List<string>? attempted = null;
        // Providers in a 429 cooldown when the read began, with its end: their client answers from its hold
        Dictionary<string, DateTime>? held = null;
        var stamped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            var enabledIds = enabledProviders.Where(p => ProviderConstants.IsValidProvider(p.Id))
                .Select(p => ProviderConstants.ValidateAndNormalize(p.Id)).ToList();
            List<string>? partial = onlyProviders == null ? null
                : enabledIds.Where(id => onlyProviders.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            if (partial != null && partial.Count == enabledIds.Count) partial = null;
            attempted = partial ?? enabledIds;
            // Every provider that was due has been disabled meanwhile
            if (attempted.Count == 0) return;
            LogStart(log, attempted);
            if (ActivityRefreshOn) held = CooldownsAt(attempted, DateTime.UtcNow);

            // The providers being read are loading again (existing data stays visible while it refreshes)
            for (int i = 0; i < Providers.Count; i++)
            {
                if (!Providers[i].IsLoading && (partial == null || partial.Contains(Providers[i].Provider, StringComparer.OrdinalIgnoreCase)))
                    Providers[i] = Providers[i] with { IsLoading = true };
            }

            // Stream results and update as they complete
            var stream = partial == null ? _codexBarService.GetAllUsageStreamAsync(ct) : _codexBarService.GetUsageStreamAsync(partial, ct);
            await foreach (var data in stream)
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
                        TimestampUtc = resultAtUtc, ProviderId = data.Provider, Success = !data.AuthRequired && !data.HasError,
                        DurationMs = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds),
                        Message = resetOutcome == null ? data.AuthMessage ?? data.Error : $"Reset refresh: {resetOutcome}"
                    });
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Refresh activity could not be recorded"); }
                if (!developerMode) _history.Record(data, DateTime.UtcNow, prune: false);
                // A reading without a sign-in problem ends any wait for a new sign-in
                if (!data.AuthRequired && !data.HasError && ProviderConstants.IsValidProvider(data.Provider))
                    _signInWatcher?.Cancel(data.Provider);
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
                if (ProviderConstants.IsValidProvider(data.Provider) &&
                    AnsweredFromHold(held, ProviderConstants.ValidateAndNormalize(data.Provider), resultAtUtc) is { } heldKey)
                {
                    // No call was made: not a fetch, so the real attempt still comes when the cooldown ends
                    stamped.Add(heldKey);
                    _logger.LogDebug("Usage of {Provider} was answered from its rate limit hold", heldKey);
                }
                else if (ProviderConstants.IsValidProvider(data.Provider))
                {
                    // Each provider's own fetch time and unchanged streak drive the per-provider schedule
                    var key = ProviderConstants.ValidateAndNormalize(data.Provider);
                    stamped.Add(key);
                    _lastFetchUtc[key] = resultAtUtc;
                    // Only a good answer: a failed read (or a missing sign-in) is tried again when a window opens
                    if (!data.HasError && !data.AuthRequired && data.UsageFetchOutcome != FetchOutcome.Failed)
                        _lastResultUtc[key] = resultAtUtc;
                    var providerFingerprint = UsageFingerprint.Create(index >= 0 ? [Providers[index]] : [data]);
                    _providerStreaks[key] = resetAdaptiveStreak || resetVersion != _adaptiveResetVersion || forceCostRefresh ||
                        providerFingerprint.HasChangedFrom(_providerFingerprints.GetValueOrDefault(key))
                            ? 0 : _providerStreaks.GetValueOrDefault(key) + 1;
                    _providerFingerprints[key] = providerFingerprint;
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
                        Error = ProviderConstants.IsValidProvider(config.Id) && _settingsService.Settings.UsesNativeUsage(config.Id)
                            ? "Connection error. Check your network connection."
                            : "Connection error. Check WSL status.",
                        FetchedAt = DateTime.UtcNow
                    });
                }
            }
            ErrorMessage = "Failed to refresh data. Check connection.";
        }
        finally
        {
            _refreshCts = null;
            // A provider that gave no answer (the refresh failed) still counts as tried, so a failing provider waits its
            // interval instead of being retried at once. A canceled refresh is followed by the one that canceled it.
            if (attempted != null && !ct.IsCancellationRequested)
            {
                var triedAt = DateTime.UtcNow;
                foreach (var provider in attempted)
                {
                    // A provider whose cooldown lasted throughout was never called
                    if (stamped.Contains(provider) || held?.TryGetValue(provider, out var until) == true && until > triedAt) continue;
                    _lastFetchUtc[provider] = triedAt;
                }
            }
            _inFlightProviders = null;
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
            var followUp = _refreshPending || _pendingProviders.Count > 0;
            if (costProviders != null && !ct.IsCancellationRequested && !followUp)
                RefreshBackgroundCostsAsync(costProviders, forceCostRefresh).SafeFireAndForget(
                    onError: ex => _logger.LogError(ex, "Background cost refresh failed"));
            else if (!followUp)
                ScheduleWorkingSetTrim();
            _forceCostRefreshPending = false;
            if (followUp && !_disposed && !_stopped && !_exiting)
            {
                // A full follow-up reads every provider; otherwise only those asked for during this partial refresh
                IReadOnlyCollection<string>? only = _refreshPending ? null : _pendingProviders.ToArray();
                var pendingLog = _refreshPending ? _pendingLog : _pendingPartialLog;
                _refreshPending = false;
                _pendingProviders.Clear();
                _pendingLog = _pendingPartialLog = null;
                RefreshCoreAsync(forceCostRefresh, onlyProviders: only, log: pendingLog)
                    .SafeFireAndForget(onError: ex => _logger.LogError(ex, "Pending refresh failed"));
            }
        }
    }

    private void RefreshApiCredits(bool force)
    {
        // Only the dashboard shows the API credits: without a visible one (or on a locked screen) a background poll is
        // wasted network wake-ups. The dashboard asks again when it becomes visible; a manual refresh (force) always goes through.
        if (_dashboardService == null || _disposed || _stopped || _exiting ||
            (!force && (Volatile.Read(ref _dashboardVisibility) == 0 || IsSessionLocked))) return;
        if (_apiCreditsCts != null)
        {
            _apiCreditsPending = true;
            _apiCreditsForcePending |= force;
            return;
        }
        var cts = new CancellationTokenSource();
        _apiCreditsCts = cts;
        _apiCreditsTask = RefreshApiCreditsCoreAsync(force, cts);
        _apiCreditsTask.SafeFireAndForget(
            onError: _ => _logger.LogDebug("Background API billing refresh did not complete"));
    }

    private async Task RefreshApiCreditsCoreAsync(bool force, CancellationTokenSource cts)
    {
        try
        {
            await _dashboardService!.RefreshApiCreditsAsync(force, cts.Token);
            if (!cts.IsCancellationRequested && !_disposed) ApiCreditsRefreshed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _apiCreditsCts = null;
            cts.Dispose();
            var pending = _apiCreditsPending;
            var forcePending = _apiCreditsForcePending;
            _apiCreditsPending = false;
            _apiCreditsForcePending = false;
            if (pending) RefreshApiCredits(forcePending);
        }
    }

    /// <summary>
    /// The Admin key was saved or removed in Settings: drop any read still using the old key and read again now, even
    /// without a visible dashboard, so the next dashboard never shows the old key's result.
    /// </summary>
    private void OnAdminKeyChanged(object? sender, EventArgs e)
    {
        try
        {
            OnUiThread(() =>
            {
                if (_disposed) return;
                CancelApiCredits();
                RefreshApiCredits(true);
            });
        }
        catch (Exception ex) { _logger.LogError("Failed to dispatch the Admin key change ({Type})", ex.GetType().Name); }
    }

    private void CancelApiCredits()
    {
        _apiCreditsPending = false;
        _apiCreditsForcePending = false;
        _apiCreditsCts?.Cancel();
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
        var activeBilling = _apiCreditsTask;
        var activeBackground = Task.WhenAll(_retiredCostRefreshes,
            _backgroundCostRefreshCompletion?.Task ?? Task.CompletedTask);
        Stop();
        if (activeRefresh != null) await activeRefresh;
        // Iterator disposal waits for the service producer to persist completed costs even on cancellation.
        if (activeDemand != null) await activeDemand;
        await activeBackground;
        try { await activeBilling; }
        catch (Exception) { _logger.LogDebug("Background API billing refresh did not complete on exit"); }
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
            if (!ActivityRefreshOn)
            {
                var lines = _scheduleLog.IsEnabled(LogLevel.Information)
                    ? BuildPlan(DateTime.UtcNow, legacy: true)
                        .Select(due => targets == null ? due : due with { Reason = RefreshReason.Reset }).ToList()
                    : null;
                await RefreshCoreAsync(resetTargets: targets,
                    log: lines == null ? null : new("Scheduled", RefreshReason.Idle, lines));
                return;
            }
            // Re-plan at the time of the wake-up: activity, settings or a lock may have changed while waiting.
            var now = DateTime.UtcNow;
            var plan = BuildPlan(now);
            var dueNow = ProviderRefreshPlanner.SelectDue(plan, now, IsSessionLocked, DueTolerance).ToList();
            if (targets != null)
            {
                foreach (var provider in targets.Select(t => t.Provider).Where(ProviderConstants.IsValidProvider)
                    .Select(ProviderConstants.ValidateAndNormalize).Distinct(StringComparer.Ordinal))
                {
                    var index = dueNow.FindIndex(d => d.Provider == provider);
                    if (index >= 0) dueNow[index] = dueNow[index] with { Reason = RefreshReason.Reset };
                    // A reset never ends a 429 cooldown early: the provider is read when its cooldown ends
                    else if (plan.Any(p => p.Provider == provider && ProviderRefreshPlanner.InCooldown(p, now))) continue;
                    else dueNow.Add(new ProviderRefreshDue(provider, now, TimeSpan.Zero, RefreshReason.Reset));
                }
            }
            if (dueNow.Count == 0)
            {
                RecreateTimer();
                return;
            }
            await RefreshCoreAsync(resetTargets: targets, onlyProviders: dueNow.Select(d => d.Provider).ToArray(),
                log: new("Scheduled", RefreshReason.Idle, dueNow));
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
                // Plugging in or out must not push the pending refresh back by the time it has already waited
                if (_stopped || _exiting || IsLoading) return;
                _carryOverSinceUtc = _timerCts != null ? _timerStartedUtc : null;
                RecreateTimer();
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to dispatch battery saver change"); }
    }

    private async Task HandleSettingsChangeAsync()
    {
        try
        {
            if (_disposed || _stopped || _exiting) return;
            CancelApiCredits();
            RefreshApiCredits(false);
            _logger.LogDebug("Settings changed, recreating timer and refreshing data");
            _resetTracker.RetainProviders(_settingsService.Settings.Providers.Where(p => p.IsEnabled).Select(p => p.Id));
            ResetAdaptiveRefresh();
            _costRefreshCts?.Cancel();
            _backgroundCostRefreshCts?.Cancel();
            if (!_settingsService.Settings.ShowCostTotals)
                for (var i = 0; i < Providers.Count; i++) Providers[i] = Providers[i] with { Cost = null };
            var log = new RefreshStartLog("Settings", RefreshReason.Manual);
            if (IsLoading) { QueueFullRefresh(log); _refreshCts?.Cancel(); return; }
            await RefreshCoreAsync(log: log);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle settings change");
        }
    }

    private bool IsProviderEnabled(string providerId)
    {
        foreach (var provider in _settingsService.Settings.Providers)
        {
            if (provider.IsEnabled && provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Recreates the periodic timer with the current refresh interval from settings.
    /// </summary>
    private void RecreateTimer(TimeSpan? firstDelay = null)
    {
        if (_disposed || _stopped || _exiting || IsLoading) return;
        var now = DateTime.UtcNow;
        ResetRefreshScheduler.PruneExpiredAttempts(_resetRefreshAttempts, now);
        var pendingSinceUtc = _carryOverSinceUtc;
        _carryOverSinceUtc = null;
        IReadOnlyList<ProviderRefreshDue>? plan = null;
        if (ActivityRefreshOn)
        {
            // Each provider is due from its own last fetch, so a changed interval never loses the time already waited.
            pendingSinceUtc = null;
            // A folder that cannot be watched only costs the speed-up, never the timer
            try { _toolActivity?.EnsureWatching(); }
            catch (Exception ex) { _logger.LogDebug("Tool log folders could not be watched ({Type})", ex.GetType().Name); }
            plan = BuildPlan(now);
            if (ProviderRefreshPlanner.NextDue(plan) is { } next) firstDelay = next - now;
            RefreshCadence = ProviderRefreshPlanner.Cadence(plan);
        }
        else RefreshCadence = RefreshCadence.None;
        var normalDelay = firstDelay ?? GetScheduledDelay();
        if (normalDelay < ResetRefreshScheduler.MinimumDelay) normalDelay = ResetRefreshScheduler.MinimumDelay;
        var locked = _sessionLocked || QuerySessionLocked();
        if (locked)
        {
            var lockedInterval = TimeSpan.FromMinutes(_settingsService.Settings.LockedRefreshIntervalMinutes);
            if (lockedInterval > normalDelay) normalDelay = lockedInterval;
        }
        var resetTimes = new List<DateTime?>(Providers.Count * 4);
        foreach (var provider in Providers)
        {
            if (!IsProviderEnabled(provider.Provider)) continue;
            if (provider.Session?.ResetAt is { } sessionReset) resetTimes.Add(sessionReset);
            if (provider.Weekly?.ResetAt is { } weeklyReset) resetTimes.Add(weeklyReset);
            if (provider.Tertiary?.ResetAt is { } tertiaryReset) resetTimes.Add(tertiaryReset);
            foreach (var extra in provider.ExtraWindows)
            {
                if (extra.Window?.ResetAt is { } extraReset) resetTimes.Add(extraReset);
            }
        }
        var schedule = locked ? new ResetRefreshSchedule(normalDelay)
            : ResetRefreshScheduler.GetSchedule(resetTimes, now, normalDelay, _resetRefreshAttempts);
        // The per-provider schedule shows its shortest whole interval (a reset refresh shows its own wait, as before)
        EffectiveRefreshInterval = plan is { Count: > 0 } && schedule.TargetResetAtUtc == null
            ? plan.Min(due => due.Interval) : schedule.Delay;
        // A new interval for the refresh already pending (battery saver) keeps the time already waited; the interval shown stays whole
        if (pendingSinceUtc is { } since && schedule.TargetResetAtUtc == null)
            schedule = schedule with { Delay = TimeSpan.FromTicks(Math.Max(ResetRefreshScheduler.MinimumDelay.Ticks, (schedule.Delay - (now - since)).Ticks)) };
        _timerCts?.Cancel();
        _timerCts?.Dispose();
        _timerCts = new CancellationTokenSource();
        _timerStartedUtc = now;
        NextRefreshUtc = now + schedule.Delay;
        RunPeriodicRefreshAsync(schedule, _timerCts.Token).SafeFireAndForget(
            onError: ex => _logger.LogError(ex, "Periodic refresh task failed"));
        // Unlock events are not reliable (they never reached the app in testing), and a locked session sleeps for up
        // to LockedRefreshIntervalMinutes: while locked, poll cheaply so an unlock is noticed within seconds.
        if (_sessionLocked || QuerySessionLocked())
            WatchForUnlockAsync(_timerCts.Token).SafeFireAndForget(
                onError: ex => _logger.LogError(ex, "Unlock watcher failed"));
    }

    /// <summary>The "Refresh faster while a tool is in use" setting: one schedule per provider instead of one for all.</summary>
    private bool ActivityRefreshOn => _settingsService.Settings.ActivityRefreshEnabled;

    private List<string> EnabledProviderIds() => _settingsService.Settings.Providers
        .Where(p => p.IsEnabled && ProviderConstants.IsValidProvider(p.Id)).OrderBy(p => p.Order)
        .Select(p => ProviderConstants.ValidateAndNormalize(p.Id)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// When each enabled provider is due and why. <paramref name="legacy"/> plans with the setting off (one shared
    /// streak), only for the log line of a refresh under today's schedule.
    /// </summary>
    private IReadOnlyList<ProviderRefreshDue> BuildPlan(DateTime nowUtc, bool legacy = false)
    {
        var settings = _settingsService.Settings;
        var inputs = new List<ProviderRefreshInput>(3);
        foreach (var provider in EnabledProviderIds())
        {
            DateTime? lastFetch = null;
            if (_lastFetchUtc.TryGetValue(provider, out var stamp))
            {
                // The clock was set back: a fetch "in the future" counts as just now (once), so the wait stays one interval.
                if (stamp > nowUtc) _lastFetchUtc[provider] = stamp = nowUtc;
                lastFetch = stamp;
            }
            UsageData? data = null;
            foreach (var shown in Providers)
            {
                if (shown.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)) { data = shown; break; }
            }
            var (percent, pace) = ProviderRefreshPlanner.Pressure(data, nowUtc);
            inputs.Add(new(provider, _toolActivity?.LastActivityUtc(provider), lastFetch, percent, pace,
                legacy ? _unchangedRefreshes : _providerStreaks.GetValueOrDefault(provider), RateLimitedUntil(provider)));
        }
        return ProviderRefreshPlanner.Plan(inputs, new RefreshPlanOptions
        {
            BaseInterval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds),
            AdaptiveEnabled = settings.AdaptiveRefreshEnabled,
            ActivityRefreshEnabled = !legacy && settings.ActivityRefreshEnabled,
            BatterySaver = _batterySaverMonitor?.BatterySaverActive ?? false,
            Locked = IsSessionLocked,
            LockedInterval = TimeSpan.FromMinutes(settings.LockedRefreshIntervalMinutes)
        }, nowUtc);
    }

    /// <summary>When the provider's 429 cooldown ends (UTC), or null when none lasts or it cannot be told.</summary>
    private DateTime? RateLimitedUntil(string provider)
    {
        try { return _codexBarService.GetRateLimitedUntil(provider); }
        catch (Exception ex)
        {
            _logger.LogDebug("Rate limit state unavailable ({Type})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The providers among <paramref name="providers"/> whose 429 cooldown lasts past <paramref name="nowUtc"/>, with its end.</summary>
    private Dictionary<string, DateTime>? CooldownsAt(IEnumerable<string> providers, DateTime nowUtc)
    {
        Dictionary<string, DateTime>? held = null;
        foreach (var provider in providers)
        {
            if (RateLimitedUntil(provider) is { } until && until > nowUtc)
                (held ??= new(StringComparer.OrdinalIgnoreCase))[provider] = until;
        }
        return held;
    }

    /// <summary>
    /// <paramref name="provider"/> when its answer at <paramref name="resultAtUtc"/> came from its client's 429 hold
    /// without a call: the cooldown that lasted when the read began still lasts, unchanged (a new 429 or a new sign-in
    /// would have changed it). Null otherwise.
    /// </summary>
    private string? AnsweredFromHold(Dictionary<string, DateTime>? held, string provider, DateTime resultAtUtc) =>
        held != null && held.TryGetValue(provider, out var until) && until > resultAtUtc && RateLimitedUntil(provider) == until
            ? provider : null;

    /// <summary>The schedule log lines a refresh writes when it actually starts (one per provider it reads).</summary>
    /// <param name="Lines">Planned lines; null writes one line with <paramref name="Reason"/> for each provider read.</param>
    private sealed record RefreshStartLog(string Trigger, RefreshReason Reason, IReadOnlyList<ProviderRefreshDue>? Lines = null);

    /// <summary>The providers a refresh of <paramref name="onlyProviders"/> reads, or null when it reads every enabled one.</summary>
    private HashSet<string>? PartialSet(IReadOnlyCollection<string>? onlyProviders)
    {
        if (onlyProviders == null) return null;
        var enabled = EnabledProviderIds();
        var read = enabled.Where(provider => onlyProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return read.Count < enabled.Count ? read : null;
    }

    /// <summary>A refresh of every provider asked for while one runs: it follows the running one.</summary>
    private void QueueFullRefresh(RefreshStartLog log)
    {
        _refreshPending = true;
        _pendingLog = log;
    }

    /// <summary>
    /// A refresh asked for while another runs. One that reads every provider covers it (as before the per-provider
    /// schedule). Behind a partial one, a refresh of every provider, or the asked-for providers it does not read, follow it.
    /// </summary>
    private void QueueBehindInFlight(IReadOnlyCollection<string>? providers, RefreshStartLog? log)
    {
        if (_inFlightProviders is not { } inFlight) return;
        if (providers == null)
        {
            _refreshPending = true;
            _pendingLog = log ?? _pendingLog;
            return;
        }
        var queued = false;
        foreach (var provider in providers)
            queued |= !inFlight.Contains(provider) && _pendingProviders.Add(provider);
        if (queued && log != null) _pendingPartialLog = log;
    }

    /// <summary>Writes the schedule lines of a refresh that starts now, for the providers it reads.</summary>
    private void LogStart(RefreshStartLog? log, IReadOnlyList<string> attempted)
    {
        if (log == null || !_scheduleLog.IsEnabled(LogLevel.Information)) return;
        var now = DateTime.UtcNow;
        LogRefresh(log.Trigger, log.Lines?.Where(due => attempted.Contains(due.Provider, StringComparer.OrdinalIgnoreCase)) ??
            attempted.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(provider => new ProviderRefreshDue(provider, now, TimeSpan.Zero, log.Reason)));
    }

    /// <summary>One line per provider read: the trigger, the provider and why (no paths, no usage values).</summary>
    private void LogRefresh(string trigger, IEnumerable<ProviderRefreshDue> providers)
    {
        if (!_scheduleLog.IsEnabled(LogLevel.Information)) return;
        foreach (var due in providers)
        {
            _scheduleLog.LogInformation(
                "{Trigger} refresh of {Provider}: {Reason}, back-off step {Step}, interval {IntervalSeconds} s, rate limited {RateLimited}",
                trigger, due.Provider, due.Reason, due.BackoffStep, (int)due.Interval.TotalSeconds, due.RateLimited);
        }
    }

    /// <summary>A provider's tool started writing logs after a quiet period: re-plan so it is read soon, not at its idle time.</summary>
    private void OnToolActivityStarted(object? sender, string provider)
    {
        try
        {
            OnUiThread(() =>
            {
                try
                {
                    // A locked session waits its locked interval whatever the tools do (re-planning would restart that wait)
                    if (_disposed || _stopped || _exiting || IsLoading || !ActivityRefreshOn || IsSessionLocked) return;
                    RecreateTimer();
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to re-plan after tool activity"); }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to dispatch tool activity"); }
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
            if (_signInWatcher != null) _signInWatcher.RefreshRequested -= OnSignInRefreshRequested;
            if (_adminKeys != null) _adminKeys.KeyChanged -= OnAdminKeyChanged;
            if (_toolActivity != null) _toolActivity.ActivityStarted -= OnToolActivityStarted;
            _sessionState.Locked -= OnSessionLocked;
            _sessionState.Unlocked -= OnSessionActivated;
            _sessionState.Resumed -= OnSessionActivated;
        }

        _disposed = true;
    }
}
