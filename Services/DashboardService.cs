using System.Reflection;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

public sealed class DashboardService : IDashboardService
{
    private readonly ISettingsService _settings;
    private readonly UsageHistoryStore _history;
    private readonly UsageHistoryPersistence _persistence;
    private readonly IActivityLog _activity;
    private readonly ICodexBarService _cli;
    private readonly ILogger<DashboardService> _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly string? _appVersion = typeof(DashboardService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DashboardService).Assembly.GetName().Version?.ToString();
    private Task? _inFlight;
    private DateTimeOffset? _capturedAt;
    private string? _cliVersion;
    private bool? _cliAvailable;

    public DashboardService(ISettingsService settings, UsageHistoryStore history, UsageHistoryPersistence persistence,
        IActivityLog activity, ICodexBarService cli, ILogger<DashboardService> logger, TimeProvider? timeProvider = null)
    {
        _settings = settings; _history = history; _persistence = persistence;
        _activity = activity; _cli = cli; _logger = logger; _clock = timeProvider ?? TimeProvider.System;
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
            RecentAlerts = _activity.RecentAlerts, CostCache = _cli.GetCostCacheInfo()
        });
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
