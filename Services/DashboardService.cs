using System.Reflection;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

public sealed class DashboardService : IDashboardService
{
    private readonly ISettingsService _settings;
    private readonly UsageHistoryStore _history;
    private readonly UsageHistoryPersistence _persistence;
    private readonly IActivityLog _activity;
    private readonly ICodexBarService _cli;
    private readonly ILogger<DashboardService> _logger;
    private readonly TimeProvider _clock;
    private readonly ICostLedger? _costLedger;
    private readonly object _gate = new();
    private readonly string? _appVersion = typeof(DashboardService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DashboardService).Assembly.GetName().Version?.ToString();
    private Task? _inFlight;
    private DateTimeOffset? _capturedAt;
    private string? _cliVersion;
    private bool? _cliAvailable;
    private readonly IEarlyResetStore? _earlyResetStore;
    private readonly IApiBillingService? _apiBilling;
    private readonly IGeminiSpendService? _geminiSpend;
    private ApiCreditsInfo? _apiCredits;
    private GeminiApiSpendInfo? _geminiApi;
    private int _billingGeneration;
    private BillingScope? _billingScope;
    private readonly record struct BillingScope(decimal Monthly, int Day, bool Developer);

    private BillingScope CurrentBillingScope() => new(
        ApiCreditsCalculator.NormalizeMonthlyUsd(_settings.Settings.ApiCreditsMonthlyUsd),
        _settings.Settings.ApiCreditsCycleStartDay is >= 1 and <= 28 ? _settings.Settings.ApiCreditsCycleStartDay : 1,
        _settings.Settings.DeveloperModeEnabled);

    public DashboardService(ISettingsService settings, UsageHistoryStore history, UsageHistoryPersistence persistence,
        IActivityLog activity, ICodexBarService cli, ILogger<DashboardService> logger, TimeProvider? timeProvider = null,
        ICostLedger? costLedger = null, IEarlyResetStore? earlyResetStore = null, IApiBillingService? apiBilling = null, IGeminiSpendService? geminiSpend = null)
    {
        _settings = settings; _history = history; _persistence = persistence;
        _activity = activity; _cli = cli; _logger = logger; _clock = timeProvider ?? TimeProvider.System;
        _costLedger = costLedger;
        _earlyResetStore = earlyResetStore;
        _apiBilling = apiBilling;
        _geminiSpend = geminiSpend;
    }

    public DashboardSnapshot GetSnapshot(IReadOnlyList<UsageData> providers, DateTime? lastRefreshUtc,
        DateTime? nextRefreshUtc = null, TimeSpan? effectiveRefreshInterval = null)
    {
        string? version;
        bool? available;
        lock (_gate) { version = _cliVersion; available = _cliAvailable; }
        long bytes = 0;
        try { var file = new FileInfo(_persistence.FilePath); if (file.Exists) bytes = file.Length; }
        catch (Exception ex) { _logger.LogDebug(ex, "History file size unavailable"); }
        return DashboardSnapshotBuilder.Build(new DashboardInputs
        {
            Providers = providers, Settings = _settings.Settings, HistorySamples = _history.Samples,
            HistoryFileBytes = bytes, NowUtc = _clock.GetUtcNow().UtcDateTime, LastRefreshUtc = lastRefreshUtc,
            NextRefreshUtc = nextRefreshUtc, EffectiveRefreshInterval = effectiveRefreshInterval,
            CliVersion = version, CliAvailable = available, AppVersion = _appVersion,
            SettingsPath = _settings.SettingsFilePath, RecentRefreshes = _activity.RecentRefreshes,
            RecentAlerts = _activity.RecentAlerts, CostCache = _cli.GetCostCacheInfo(),
            CostLedger = _costLedger,
            EarlyResetStore = _earlyResetStore,
            ApiCredits = CurrentApiCredits(),
            GeminiApi = CurrentGeminiSpend()
        });
    }

    private GeminiApiSpendInfo? CurrentGeminiSpend()
    {
        lock (_gate)
        {
            var spend = _geminiApi;
            if (spend == null) return null;
            var zone = _geminiSpend?.Zone ?? TimeZoneInfo.Local;
            var nowUtc = _clock.GetUtcNow().UtcDateTime;
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
            var currentMonthStart = new DateOnly(localNow.Year, localNow.Month, 1);
            if (spend.MonthStart != currentMonthStart) return null;
            var monthEnd = currentMonthStart.AddMonths(1);
            var localDate = DateOnly.FromDateTime(localNow);
            var daysLeft = Math.Max(1, monthEnd.DayNumber - localDate.DayNumber);
            return spend with { DaysLeft = daysLeft };
        }
    }

    private ApiCreditsInfo? CurrentApiCredits()
    {
        lock (_gate)
        {
            var credits = _apiCredits;
            var scope = CurrentBillingScope();
            var now = _clock.GetUtcNow().UtcDateTime;
            var window = ApiCreditsCalculator.GetCycleWindow(scope.Day, now);
            if (credits == null || _billingScope != scope || credits.CycleStart != DateOnly.FromDateTime(window.StartUtc) ||
                credits.CycleEnd != DateOnly.FromDateTime(window.EndUtc)) return null;
            if (credits.State == ApiCreditsState.Ok && credits.FetchedAtUtc is { } fetched)
            {
                var current = ApiCreditsCalculator.BuildOk(scope.Monthly, credits.SpentUsd, scope.Day, now, fetched);
                return credits with { DaysLeft = current.DaysLeft, PaceUsdPerDay = current.PaceUsdPerDay,
                    IsStale = credits.IsStale || current.IsStale };
            }
            return credits;
        }
    }

    /// <summary>Independent of usage/CLI cost scans; all billing work runs away from the UI context.</summary>
    public async Task RefreshApiCreditsAsync(bool force, CancellationToken ct = default)
    {
        if (_apiBilling == null && _geminiSpend == null) return;
        int generation;
        BillingScope scope;
        lock (_gate) { generation = ++_billingGeneration; scope = CurrentBillingScope(); }
        if (_apiBilling != null)
        {
            try
            {
                var result = await Task.Run(() => _apiBilling.GetAsync(force, ct), ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!ct.IsCancellationRequested && generation == _billingGeneration && scope == CurrentBillingScope())
                    { _apiCredits = result; _billingScope = scope; }
                }
            }
            catch (Exception)
            {
                // A replacement billing implementation must not expose transport exception details either.
                _logger.LogDebug("Background API billing refresh did not complete");
            }
        }

        if (_geminiSpend != null)
        {
            try
            {
                var geminiResult = await Task.Run(() => _geminiSpend.GetAsync(ct), ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!ct.IsCancellationRequested && generation == _billingGeneration)
                    { _geminiApi = geminiResult; }
                }
            }
            catch (Exception)
            {
                _logger.LogDebug("Background Gemini spend refresh did not complete");
            }
        }
    }

    public Task RefreshDiagnosticsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            if (_capturedAt.HasValue && _clock.GetUtcNow() - _capturedAt.Value < TimeSpan.FromMinutes(10))
                return Task.CompletedTask;
            return _inFlight = RefreshCoreAsync(ct);
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        string? version = null;
        bool? available = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            version = await _cli.GetVersionAsync(ct).ConfigureAwait(false);
            available = await _cli.IsAvailableAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // Cancellation must not overwrite facts or start the cache backoff.
        }
        catch (Exception ex)
        {
            version = null; available = null;
            _logger.LogDebug(ex, "CLI diagnostics unavailable");
        }
        lock (_gate)
        {
            _cliVersion = version; _cliAvailable = available; _capturedAt = _clock.GetUtcNow();
        }
    }
}
