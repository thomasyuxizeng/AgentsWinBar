using System.Text.Json;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services.Native;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Usage readings per provider: read natively (<see cref="INativeUsageService"/>, the default) or through the
/// codexbar CLI in WSL. Both routes produce the CLI's JSON and share one interpretation path. Cost scans follow the
/// cost source setting the same way: the native log scanner (<see cref="INativeCostService"/>, the default) or the
/// CLI's cost scan in WSL.
/// </summary>
public class CodexBarService : ICodexBarService
{
    private readonly IWslService _wslService;
    private readonly ICacheService _cacheService;
    private readonly ISettingsService _settingsService;
    private readonly ISampleDataLoader _sampleDataLoader;
    private readonly ILogger<CodexBarService> _logger;
    private readonly IActivityLog? _activityLog;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _userProfile;
    private readonly Func<string, bool> _directoryExists;
    private readonly SemaphoreSlim _costGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset AttemptedAt, CostData? Data)> _costCache = [];
    private static readonly TimeSpan CostCacheTtl = TimeSpan.FromMinutes(30);
    // Cost scans run in the background and can read hundreds of MB of logs over /mnt/c (Codex took ~90 s on
    // a real machine), so they get their own deadline instead of the interactive usage timeout.
    internal static readonly TimeSpan CostScanTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan CostScanTimeoutCap = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CostTimeoutBackoff = TimeSpan.FromMinutes(5);
    // Guarded by _costCache, like the last-good readings.
    private readonly Dictionary<string, int> _costTimeouts = [];
    private readonly Dictionary<string, string> _costFailures = [];
    private readonly HashSet<string> _costTimedOut = [];
    private DateOnly? _costStoreDay;
    private readonly ICostCacheStore? _costStore;
    private readonly ICostLedger? _costLedger;
    private readonly IEarlyResetStore? _earlyResetStore;
    private readonly IToastNotifier? _toastNotifier;
    private readonly object _earlyResetTaskGate = new();
    private Task _earlyResetNotifications = Task.CompletedTask;
    private readonly UsageHistoryStore? _history;
    private readonly IGeminiStatusFallback? _geminiStatusFallback;
    private readonly IProviderIncidentFetcher? _incidentFetcher;
    private readonly INativeUsageService? _nativeUsage;
    private readonly INativeCostService? _nativeCost;
    /// <summary>
    /// A prefetched provider status may take this long, counted from the moment its prefetch started. A usage read
    /// that took longer adds no extra wait.
    /// </summary>
    public static readonly TimeSpan StatusDeadline = TimeSpan.FromSeconds(4);
    private sealed record StatusPrefetch(long StartedAt, Task<ProviderStatus?> Task);
    /// <summary>
    /// TTL of the WSL codexbar shim cache (600 seconds / 10 minutes), shared with tools/wsl/codexbar-shim.sh.
    /// Replays younger than this TTL represent normal operation and are not considered stale.
    /// </summary>
    public static readonly TimeSpan ShimTtl = TimeSpan.FromSeconds(600);
    // The last complete result per provider (guarded by _costCache). A failed, empty or timed-out scan serves it
    // instead of blanking the totals; only a newer good result replaces it.
    private readonly Dictionary<string, CostData> _lastGoodCost = [];
    // Results restored from disk that this session hasn't rescanned yet; published once, before the first scan.
    private readonly HashSet<string> _restoredCost = [];
    private bool _costStoreLoaded;
    private bool _costStoreDirty;
    private sealed class ProviderRefreshState
    {
        public long Issued;
        public long PublishedUsage;
        public long PublishedStatus;
        public long PublishedAuth;
        public bool AuthRequired;
        public string? AuthMessage;
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderRefreshState> _providerRefreshes = new(StringComparer.Ordinal);

    private long BeginProviderRefresh(string provider)
    {
        var state = _providerRefreshes.GetOrAdd(provider, _ => new());
        lock (state) return ++state.Issued;
    }


    public CodexBarService(
        IWslService wslService,
        ICacheService cacheService,
        ISettingsService settingsService,
        ISampleDataLoader sampleDataLoader,
        ILogger<CodexBarService> logger,
        TimeProvider? timeProvider = null,
        Func<string>? userProfile = null,
        Func<string, bool>? directoryExists = null,
        IActivityLog? activityLog = null,
        ICostCacheStore? costStore = null,
        UsageHistoryStore? history = null,
        IGeminiStatusFallback? geminiStatusFallback = null,
        IProviderIncidentFetcher? incidentFetcher = null,
        ICostLedger? costLedger = null,
        IEarlyResetStore? earlyResetStore = null,
        IToastNotifier? toastNotifier = null,
        INativeUsageService? nativeUsage = null,
        INativeCostService? nativeCost = null)
    {
        _nativeUsage = nativeUsage;
        _nativeCost = nativeCost;
        _history = history;
        _costStore = costStore;
        _costLedger = costLedger;
        _earlyResetStore = earlyResetStore;
        _toastNotifier = toastNotifier;
        _geminiStatusFallback = geminiStatusFallback;
        _incidentFetcher = incidentFetcher;
        _wslService = wslService;
        _cacheService = cacheService;
        _settingsService = settingsService;
        _sampleDataLoader = sampleDataLoader;
        _logger = logger;
        _activityLog = activityLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public async Task<CostData?> GetCostAsync(string provider, bool forceRefresh = false, CancellationToken ct = default)
    {
        CostData? data = null;
        try
        {
            await FetchCostsAsync([provider], forceRefresh, useBatch: false, (_, cost) => data = cost, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cost scan unavailable for {Provider}", provider);
        }
        return WithLedgerCosts(data, _timeProvider.GetUtcNow());
    }

    public async IAsyncEnumerable<(string Provider, CostData? Cost)> GetCostsAsync(IReadOnlyList<string> providers,
        bool forceRefresh = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<(string Provider, CostData? Cost)>(new()
        {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false
        });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = ProduceAsync();
        try
        {
            await foreach (var result in channel.Reader.ReadAllAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return (result.Provider, WithLedgerCosts(result.Cost, _timeProvider.GetUtcNow()));
            }
            await producer;
        }
        finally
        {
            lifetime.Cancel();
            await producer;
        }

        async Task ProduceAsync()
        {
            try
            {
                await FetchCostsAsync(providers, forceRefresh, useBatch: true,
                    (provider, cost) => channel.Writer.TryWrite((provider, cost)), lifetime.Token);
                channel.Writer.TryComplete();
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); }
        }
    }

    private bool CostsEnabled => _settingsService.Settings.ShowCostTotals && !_settingsService.Settings.DeveloperModeEnabled;

    /// <summary>True when cost scans run in-process; they then never reach <see cref="IWslService"/>.</summary>
    private bool UsesNativeCost => _nativeCost != null && _settingsService.Settings.UsesNativeCost();

    private async Task FetchCostsAsync(IReadOnlyList<string> providers, bool forceRefresh, bool useBatch,
        Action<string, CostData?> onCompleted, CancellationToken ct)
    {
        var normalized = providers.Select(ProviderConstants.ValidateAndNormalize).Distinct(StringComparer.Ordinal).ToArray();
        ct.ThrowIfCancellationRequested();
        if (normalized.Length == 0 || !CostsEnabled) return;
        await _costGate.WaitAsync(ct);
        var previousAttempts = new Dictionary<string, (DateTimeOffset AttemptedAt, CostData? Data)?>();
        var completedProviders = new HashSet<string>(StringComparer.Ordinal);
        var ledgerScans = new List<CostData>();
        try
        {
            if (!CostsEnabled) return;
            if (_costLedger != null) await _costLedger.LoadAsync(ct);
            if (_costStore != null && !_costStoreLoaded)
            {
                var restored = await _costStore.LoadAsync(ct);
                lock (_costCache)
                    foreach (var (provider, data) in restored)
                        if (_lastGoodCost.TryAdd(provider, data)) _restoredCost.Add(provider);
                _costStoreLoaded = true;
            }
            var now = _timeProvider.GetUtcNow();
            var due = new List<string>();
            foreach (var provider in normalized)
            {
                ct.ThrowIfCancellationRequested();
                CostData? published;
                lock (_costCache)
                {
                    // A clock that stepped back past the last attempt ends the cache instead of stretching it.
                    if (!forceRefresh && _costCache.TryGetValue(provider, out var cached) && now >= cached.AttemptedAt &&
                        now - cached.AttemptedAt < (_costTimedOut.Contains(provider) ? CostTimeoutBackoff : CostCacheTtl))
                        published = cached.Data != null ? CachedCost(cached.Data, now) : LastGoodCost(provider, now);
                    else
                    {
                        previousAttempts[provider] = _costCache.TryGetValue(provider, out var prior) ? prior : null;
                        _costCache[provider] = (now, null); // Back off on errors too.
                        due.Add(provider);
                        // Show a result restored from disk straight away; the scan below replaces it.
                        published = _restoredCost.Remove(provider) ? LastGoodCost(provider, now) : null;
                        if (published == null) continue;
                    }
                }
                onCompleted(provider, published);
            }
            if (due.Count == 0) return;
            var nativeCost = UsesNativeCost;
            var timeouts = due.Select(provider => CostTimeout(provider, now)).ToArray();
            // The native scanner reads the Windows log folders itself; only the WSL route builds shell commands.
            var commands = nativeCost ? Array.Empty<WslBatchCommand>() : due.Select((provider, index) => new WslBatchCommand(
                Helpers.CostCommandBuilder.Build(provider, _userProfile(), _directoryExists), timeouts[index])).ToArray();
            var completed = new bool[due.Count];
            var retry = new bool[due.Count];
            var batchFailed = false;
            void Complete(int index, WslResult result)
            {
                ct.ThrowIfCancellationRequested();
                if (index < 0 || index >= due.Count) throw new InvalidDataException("WSL batch returned an invalid command index");
                if (completed[index]) return;
                completed[index] = true;
                var costsEnabled = CostsEnabled;
                var data = costsEnabled ? InterpretCostResult(due[index], result, now, ledgerScans) : null;
                if (costsEnabled)
                {
                    lock (_costCache)
                    {
                        var provider = due[index];
                        if (data == null)
                        {
                            var timedOut = result.ExitCode == 124 ||
                                result.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase);
                            _costTimeouts[provider] = timedOut ? Math.Min(_costTimeouts.GetValueOrDefault(provider) + 1, 3) : 0;
                            if (timedOut) _costTimedOut.Add(provider);
                            else _costTimedOut.Remove(provider);
                            _costFailures[provider] = timedOut
                                ? FormattableString.Invariant($"Scan timed out after {timeouts[index].TotalMinutes:0.##} min")
                                : "Scan failed";
                            _costStoreDirty = true;
                            data = LastGoodCost(provider, now);
                        }
                        else
                        {
                            _costTimeouts.Remove(provider);
                            _costTimedOut.Remove(provider);
                            _costFailures.Remove(provider);
                        }
                    }
                }
                if (costsEnabled)
                    lock (_costCache) completedProviders.Add(due[index]);
                onCompleted(due[index], data);
            }
            async Task ScanIndividuallyAsync(int index)
            {
                if (retry[index] && !batchFailed) RecordBatchFallback(due[index], "Command failed (exit code 127)");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(commands[index].Timeout);
                try
                {
                    var result = await _wslService.ExecuteAsync(commands[index].Command, commands[index].Timeout, deadline.Token);
                    ct.ThrowIfCancellationRequested();
                    Complete(index, result);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    Complete(index, new WslResult { ExitCode = -1, Error = "Command timed out" });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Cost scan unavailable for {Provider}", due[index]);
                    Complete(index, new WslResult());
                }
            }
            if (nativeCost)
            {
                // One index update serves every due provider. Each report then takes the CLI's path through
                // InterpretCostResult, so the ledger, the cache and last-good handling behave as with WSL.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(timeouts.Max());
                IReadOnlyDictionary<string, string?>? reports = null;
                var failure = new WslResult();
                try
                {
                    reports = await _nativeCost!.FetchCostJsonAsync(due, now, deadline.Token);
                    ct.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    // The index keeps what was read so far; the next scan continues from there.
                    failure = new WslResult { ExitCode = -1, Error = "Command timed out" };
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Native cost scan unavailable");
                }
                for (var i = 0; i < due.Count; i++)
                    Complete(i, reports != null && reports.TryGetValue(due[i], out var json) && !string.IsNullOrWhiteSpace(json)
                        ? new WslResult { Success = true, Output = json }
                        : failure);
                return;
            }
            if (!useBatch)
            {
                await ScanIndividuallyAsync(0);
                return;
            }
            try
            {
                var batch = await _wslService.ExecuteBatchAsync(commands, (index, result) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (index < 0 || index >= due.Count) throw new InvalidDataException("WSL batch returned an invalid command index");
                    if (result.ExitCode == 127) retry[index] = true;
                    else Complete(index, result);
                }, ct);
                ct.ThrowIfCancellationRequested();
                if (batch == null || batch.Count != due.Count) throw new InvalidDataException("WSL batch returned an incomplete result list");
                for (var i = 0; i < batch.Count; i++)
                {
                    if (batch[i].ExitCode == 127) retry[i] = true;
                    else Complete(i, batch[i]);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogDebug(ex, "WSL cost batch unavailable; retrying unfinished providers individually");
                batchFailed = true;
                RecordBatchFallback("wsl", ex.Message);
                for (var i = 0; i < due.Count; i++) retry[i] = !completed[i];
            }
            // Retain the gate and the original attempts so fallbacks bypass backoff without deadlocking.
            await Task.WhenAll(Enumerable.Range(0, due.Count).Where(i => retry[i] && !completed[i]).Select(ScanIndividuallyAsync));
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            // Obsolete scans are not failures: retain the last good cache or allow an immediate retry.
            if (ct.IsCancellationRequested || !CostsEnabled)
            {
                lock (_costCache)
                {
                    foreach (var (provider, previous) in previousAttempts)
                    {
                        if (completedProviders.Contains(provider)) continue;
                        if (previous is { } prior) _costCache[provider] = prior;
                        else _costCache.Remove(provider);
                    }
                }
            }
            // Results that completed before a cancellation (e.g. the dashboard closed mid-scan) are complete and
            // worth keeping; the snapshot only ever holds complete results.
            try
            {
                await SaveCostStoreAsync();
                if (_costLedger != null)
                {
                    List<CostData> scansToRecord;
                    lock (_costCache) scansToRecord = [.. ledgerScans];
                    if (scansToRecord.Count > 0)
                    {
                        try { await _costLedger.RecordScansAsync(scansToRecord, _timeProvider.GetUtcNow().UtcDateTime); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Cost ledger update failed"); }
                    }
                }
            }
            finally { _costGate.Release(); }
        }
    }

    private TimeSpan CostTimeout(string provider, DateTimeOffset now)
    {
        lock (_costCache)
        {
            var minutes = Math.Max(CostScanTimeout.TotalMinutes,
                _settingsService.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds / 60.0) *
                Math.Pow(2, _costTimeouts.GetValueOrDefault(provider));
            if (_lastGoodCost.TryGetValue(provider, out var data) &&
                now.UtcDateTime - DashboardValues.Utc(data.LastSuccessfulFetch ?? data.FetchedAt) > TimeSpan.FromHours(24))
                minutes = Math.Max(minutes, 20);
            return TimeSpan.FromMinutes(Math.Min(minutes, CostScanTimeoutCap.TotalMinutes));
        }
    }

    /// <summary>The last complete result, with its original totals and timestamp preserved.</summary>
    private CostData? LastGoodCost(string provider, DateTimeOffset now)
    {
        if (!_lastGoodCost.TryGetValue(provider, out var data)) return null;
        var staleCost = data with
        {
            IsStale = true, StaleReason = _costFailures.GetValueOrDefault(provider) ?? data.StaleReason,
            LastSuccessfulFetch = data.LastSuccessfulFetch ?? data.FetchedAt
        };
        if (_costLedger != null)
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, _timeProvider.LocalTimeZone));
            var ledger30 = _costLedger.GetLast30Days(provider, today);
            if (ledger30 != null) staleCost = staleCost.WithLedgerLast30Days(ledger30, today);
        }
        return staleCost;
    }

    private CostData? WithLedgerCosts(CostData? cost, DateTimeOffset now)
    {
        if (cost == null || _costLedger == null) return cost;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, _timeProvider.LocalTimeZone));
        var ledger30 = _costLedger.GetLast30Days(cost.Provider, today);
        var readingDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DashboardValues.Utc(cost.LastSuccessfulFetch ?? cost.FetchedAt), _timeProvider.LocalTimeZone));
        return ledger30 == null ? cost : cost.WithLedgerLast30Days(ledger30, today, readingDay);
    }

    private async Task SaveCostStoreAsync()
    {
        if (_costStore == null || !_costStoreLoaded) return;
        Dictionary<string, CostData>? snapshot = null;
        lock (_costCache)
        {
            var now = _timeProvider.GetUtcNow();
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _timeProvider.LocalTimeZone).DateTime);
            if (!_costStoreDirty && (_lastGoodCost.Count == 0 || _costStoreDay == day)) return;
            snapshot = _lastGoodCost.ToDictionary(pair => pair.Key,
                pair => _costFailures.TryGetValue(pair.Key, out var reason)
                    ? pair.Value with { IsStale = true, StaleReason = reason }
                    : CachedCost(pair.Value, now), StringComparer.Ordinal);
            _costStoreDirty = false;
            _costStoreDay = day;
        }
        try
        {
            if (!await _costStore.SaveAsync(snapshot))
                lock (_costCache) _costStoreDirty = true;
        }
        catch (Exception ex)
        {
            lock (_costCache) _costStoreDirty = true;
            _logger.LogDebug(ex, "Cost cache could not be saved");
        }
    }

    private CostData? InterpretCostResult(string provider, WslResult result, DateTimeOffset attemptedAt, List<CostData>? ledgerScans = null)
    {
        try
        {
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                _logger.LogDebug("Cost scan returned no data for {Provider} (exit {ExitCode})", provider, result.ExitCode);
                return null;
            }
            var json = result.Output.Trim();
            var dto = json.StartsWith('[')
                ? JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListCostDataDto)?.FirstOrDefault()
                : JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CostDataDto);
            if (dto == null || !dto.HasReading || !string.Equals(dto.Provider, provider == "gemini" ? "antigravity" : provider,
                StringComparison.OrdinalIgnoreCase)) return null;
            var data = dto.ToCostData(provider, attemptedAt.UtcDateTime);
            lock (_costCache)
            {
                _costCache[provider] = (attemptedAt, data.HasData ? data : null);
                if (data.HasData)
                {
                    _lastGoodCost[provider] = data;
                    _restoredCost.Remove(provider);
                    _costStoreDirty = true;
                    ledgerScans?.Add(data);
                }
            }
            return data.HasData ? data : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cost scan unavailable for {Provider}", provider);
            return null;
        }
    }

    public IReadOnlyList<DashboardCostCacheEntry> GetCostCacheInfo()
    {
        lock (_costCache)
            return _costCache.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new DashboardCostCacheEntry
            {
                ProviderId = pair.Key, LastAttemptUtc = pair.Value.AttemptedAt.UtcDateTime,
                HasData = pair.Value.Data?.HasData == true
            }).ToArray();
    }

    public async Task<UsageData?> GetUsageAsync(string provider, CancellationToken ct = default)
    {
        if (!ProviderConstants.IsValidProvider(provider)) return null;
        var normalizedProvider = ProviderConstants.ValidateAndNormalize(provider);
        var generation = BeginProviderRefresh(normalizedProvider);
        ProviderStatus? prefetchedStatus = null;
        Dictionary<string, StatusPrefetch> prefetchTasks = [];
        try
        {
            prefetchTasks = StartProviderStatusPrefetches([normalizedProvider], ct);
            ct.ThrowIfCancellationRequested();
            WslResult result;
            if (UsesNativeUsage(normalizedProvider))
            {
                result = await FetchNativeResultAsync(normalizedProvider, ct);
            }
            else
            {
                var source = ProviderConstants.GetSource(normalizedProvider);
                result = await _wslService.ExecuteAsync($"codexbar --provider {normalizedProvider} --format json --source {source} --status", ct);
            }
            prefetchedStatus = await AwaitPrefetchedStatusAsync(normalizedProvider, prefetchTasks, ct);
            ct.ThrowIfCancellationRequested();

            return InterpretProviderResult(normalizedProvider, result, prefetchedStatus, generation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get usage for {Provider}", provider);
            prefetchedStatus ??= await AwaitPrefetchedStatusAsync(normalizedProvider, prefetchTasks, ct);
            return ProviderError(normalizedProvider, "Usage fetch unavailable", prefetchedStatus, generation);
        }
        finally
        {
            await DrainEarlyResetNotificationsAsync();
            await SaveUsageCacheAsync();
        }
    }

    public async Task<IReadOnlyList<UsageData>> GetAllUsageAsync(CancellationToken ct = default)
    {
        var providers = GetEnabledProviders();
        if (providers.Count == 0) { ct.ThrowIfCancellationRequested(); return []; }
        return await FetchAllProvidersAsync(providers, null, ct);
    }

    public async IAsyncEnumerable<UsageData> GetAllUsageStreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var providers = GetEnabledProviders();
        if (providers.Count == 0) { ct.ThrowIfCancellationRequested(); yield break; }
        var channel = System.Threading.Channels.Channel.CreateUnbounded<UsageData>(new()
        {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false
        });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = ProduceAsync();
        try
        {
            await foreach (var result in channel.Reader.ReadAllAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                yield return result;
            }
            await producer;
        }
        finally
        {
            // Disposing an enumeration early must also stop its WSL work.
            if (!producer.IsCompleted)
            {
                lifetime.Cancel();
                try { await producer; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }

        async Task ProduceAsync()
        {
            try
            {
                await FetchAllProvidersAsync(providers, data => channel.Writer.TryWrite(data), lifetime.Token);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }
    }

    private List<string> GetEnabledProviders() => _settingsService.Settings.Providers
        .Where(p => p.IsEnabled && ProviderConstants.IsValidProvider(p.Id))
        .Select(p => ProviderConstants.ValidateAndNormalize(p.Id)).ToList();

    // 1 = first fetch, 0 = subsequent fetches. Only a completed refresh advances it.
    private int _isFirstFetch = 1;

    private WslBatchCommand BuildProviderCommand(string provider)
    {
        var normalized = ProviderConstants.ValidateAndNormalize(provider);
        var source = ProviderConstants.GetSource(normalized);
        var verbose = normalized == "gemini" ? " --verbose" : "";
        return new($"codexbar usage --provider {normalized} --format json --source {source} --status{verbose}",
            UsageTimeout(normalized));
    }

    /// <summary>Usage deadline per provider, shared by both routes.</summary>
    private TimeSpan UsageTimeout(string provider)
    {
        var source = ProviderConstants.GetSource(provider);
        var settings = _settingsService.Settings.Timeouts;
        var first = Volatile.Read(ref _isFirstFetch) == 1;
        var seconds = source == "cli"
            ? (first ? settings.CliProviderFirstFetchTimeoutSeconds : settings.CliProviderTimeoutSeconds)
            : (first ? settings.StandardProviderFirstFetchTimeoutSeconds : settings.StandardProviderTimeoutSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// True when the provider's usage source is "native" and a native reader is available. Without a native reader
    /// (a service constructed without one, as in older tests) every provider uses the WSL route.
    /// </summary>
    private bool UsesNativeUsage(string provider) =>
        _nativeUsage != null && _settingsService.Settings.UsesNativeUsage(provider);

    /// <summary>True when no enabled provider reads usage through WSL.</summary>
    private bool AllUsageNative() =>
        _nativeUsage != null && _settingsService.Settings.AllEnabledUsageNative();

    /// <summary>
    /// Reads a provider natively and wraps its CLI-shaped JSON the way a successful WSL command arrives, so it goes
    /// through <see cref="InterpretProviderResult"/> exactly like CLI output.
    /// </summary>
    private async Task<WslResult> FetchNativeResultAsync(string provider, CancellationToken ct)
    {
        var json = await _nativeUsage!.FetchUsageJsonAsync(ProviderConstants.ValidateAndNormalize(provider), ct);
        return new WslResult { Success = true, Output = json, ExitCode = 0 };
    }

    /// <summary>One native read within the provider's usage deadline; a timeout gives the same error as a CLI timeout.</summary>
    private async Task<UsageData> FetchNativeProviderAsync(string provider, Dictionary<string, StatusPrefetch> prefetchTasks,
        long generation, CancellationToken ct)
    {
        WslResult? result = null;
        string? error = null;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(UsageTimeout(provider));
            try
            {
                result = await FetchNativeResultAsync(provider, deadline.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { error = "Request timed out"; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Native usage read failed for {Provider}", provider);
                error = "Unexpected error occurred";
            }
        }
        var status = await AwaitPrefetchedStatusAsync(provider, prefetchTasks, ct);
        ct.ThrowIfCancellationRequested();
        return result != null
            ? InterpretProviderResult(provider, result, status, generation)
            : ProviderError(provider, error!, status, generation);
    }

    private async Task<IReadOnlyList<UsageData>> FetchAllProvidersAsync(IReadOnlyList<string> providers,
        Action<UsageData>? onCompleted, CancellationToken ct)
    {
        try { return await FetchAllProvidersCoreAsync(providers, onCompleted, ct); }
        finally
        {
            await DrainEarlyResetNotificationsAsync();
            if (!_settingsService.Settings.DeveloperModeEnabled) await SaveUsageCacheAsync();
        }
    }

    private CostData CachedCost(CostData data, DateTimeOffset now)
    {
        var fetchedAt = data.FetchedAt.Kind == DateTimeKind.Local ? data.FetchedAt.ToUniversalTime() : DateTime.SpecifyKind(data.FetchedAt, DateTimeKind.Utc);
        // Crossing midnight cannot establish real zero totals. Preserve the reading and label its old calendar day.
        return TimeZoneInfo.ConvertTimeFromUtc(fetchedAt, _timeProvider.LocalTimeZone).Date == TimeZoneInfo.ConvertTime(now, _timeProvider.LocalTimeZone).Date
            ? data : data with { IsStale = true, LastSuccessfulFetch = data.LastSuccessfulFetch ?? data.FetchedAt };
    }

    private async Task SaveUsageCacheAsync()
    {
        try { await _cacheService.SaveAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Usage cache could not be saved"); }
    }

    private async Task<IReadOnlyList<UsageData>> FetchAllProvidersCoreAsync(IReadOnlyList<string> providers,
        Action<UsageData>? onCompleted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = new UsageData?[providers.Count];
        void Complete(int index, UsageData data)
        {
            ct.ThrowIfCancellationRequested();
            lock (results)
            {
                if (results[index] != null) return;
                results[index] = data;
                onCompleted?.Invoke(data);
            }
        }

        if (_settingsService.Settings.DeveloperModeEnabled)
        {
            for (var i = 0; i < providers.Count; i++) Complete(i, LoadSample(providers[i]));
        }
        else
        {
            var generations = providers.Select(BeginProviderRefresh).ToArray();
            var prefetchTasks = StartProviderStatusPrefetches(providers, ct);
            ct.ThrowIfCancellationRequested();
            // Providers set to "native" are read in parallel over HTTPS; only providers set to "wsl" go to the WSL
            // batch. With no "wsl" provider the refresh never touches IWslService.
            var wsl = Enumerable.Range(0, providers.Count).Where(i => !UsesNativeUsage(providers[i])).ToArray();
            var nativeReads = Enumerable.Range(0, providers.Count).Where(i => !wsl.Contains(i))
                .Select(async i => Complete(i, await FetchNativeProviderAsync(providers[i], prefetchTasks, generations[i], ct)))
                .ToArray();
            var commandFor = new WslBatchCommand?[providers.Count];
            foreach (var i in wsl) commandFor[i] = BuildProviderCommand(providers[i]);
            var commands = wsl.Select(i => commandFor[i]!).ToArray();
            var retryInIndividualShell = new bool[providers.Count];
            var publications = new Task?[providers.Count];
            async Task PublishAsync(int index, WslResult result)
            {
                var status = await AwaitPrefetchedStatusAsync(providers[index], prefetchTasks, ct);
                ct.ThrowIfCancellationRequested();
                Complete(index, InterpretProviderResult(providers[index], result, status, generations[index]));
            }
            void SchedulePublication(int index, WslResult result)
            {
                if (publications[index] == null) publications[index] = PublishAsync(index, result);
            }
            async Task RetryProviderAsync(int index)
            {
                if (retryInIndividualShell[index])
                {
                    _logger.LogInformation("WSL batch command not found for {Provider}; retrying individually", providers[index]);
                    RecordBatchFallback(providers[index], "Command failed (exit code 127)");
                }
                var pStatus = await AwaitPrefetchedStatusAsync(providers[index], prefetchTasks, ct);
                Complete(index, await FetchProviderAsync(providers[index], commandFor[index]!, pStatus, generations[index], ct));
            }
            async Task RunWslBatchAsync()
            {
                try
                {
                    var batch = await _wslService.ExecuteBatchAsync(commands, (position, result) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (position < 0 || position >= commands.Length)
                            throw new InvalidDataException("WSL batch returned an invalid command index");
                        var index = wsl[position];
                        // The nested batch shell cannot see aliases or unexported functions from .bashrc.
                        if (result.ExitCode == 127)
                        {
                            retryInIndividualShell[index] = true;
                            return;
                        }
                        SchedulePublication(index, result);
                    }, ct);
                    ct.ThrowIfCancellationRequested();
                    if (batch == null || batch.Count != commands.Length)
                        throw new InvalidDataException("WSL batch returned an incomplete result list");
                    for (var position = 0; position < batch.Count; position++)
                    {
                        var index = wsl[position];
                        if (results[index] != null) continue;
                        if (batch[position].ExitCode == 127)
                        {
                            retryInIndividualShell[index] = true;
                        }
                        else
                        {
                            SchedulePublication(index, batch[position]);
                        }
                    }
                    await Task.WhenAll(publications.OfType<Task>());
                    await Task.WhenAll(wsl.Where(i => results[i] == null && retryInIndividualShell[i]).Select(RetryProviderAsync));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    ct.ThrowIfCancellationRequested();
                    _logger.LogWarning(ex, "WSL batch unavailable; retrying unfinished providers individually");
                    RecordBatchFallback("wsl", ex.Message);
                    await Task.WhenAll(publications.OfType<Task>());
                    await Task.WhenAll(wsl.Where(i => results[i] == null).Select(RetryProviderAsync));
                }
                finally { await Task.WhenAll(publications.OfType<Task>()); }
            }
            var wslRun = wsl.Length > 0 ? RunWslBatchAsync() : Task.CompletedTask;
            await Task.WhenAll(nativeReads.Append(wslRun));
        }
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _isFirstFetch, 0);
        return results.Select(r => r!).ToArray();
    }

    private void RecordBatchFallback(string provider, string message) => _activityLog?.RecordRefresh(new DashboardRefreshEntry
    {
        TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime, ProviderId = provider, Success = false,
        Message = "WSL batch fallback: " + message
    });

    private UsageData LoadSample(string provider)
    {
        var normalized = ProviderConstants.ValidateAndNormalize(provider);
        try
        {
            var json = _sampleDataLoader.LoadSampleJson(normalized);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var data = ParseUsageJsonArray(json).FirstOrDefault();
                if (data != null) return data;
            }
            return ProviderError(provider, "Sample data not available (Developer mode)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load sample for {Provider}", provider);
            return ProviderError(provider, "Unexpected error occurred");
        }
    }

    private async Task<UsageData> FetchProviderAsync(string provider, WslBatchCommand command, ProviderStatus? prefetchedStatus, long generation, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(command.Timeout);
        try
        {
            var result = await _wslService.ExecuteAsync(command.Command, deadline.Token);
            ct.ThrowIfCancellationRequested();
            return deadline.IsCancellationRequested
                ? ProviderError(provider, "Request timed out", prefetchedStatus, generation)
                : InterpretProviderResult(provider, result, prefetchedStatus, generation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ProviderError(provider, "Request timed out", prefetchedStatus, generation); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Provider}", provider);
            return ProviderError(provider, "Unexpected error occurred", prefetchedStatus, generation);
        }
    }

    private UsageData InterpretProviderResult(string provider, WslResult result, ProviderStatus? prefetchedStatus, long generation)
    {
        try { return InterpretProviderResultCore(provider, result, prefetchedStatus, generation); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to interpret {Provider}", provider);
            return ProviderError(provider, "Unexpected error occurred", prefetchedStatus, generation);
        }
    }

    private UsageData InterpretProviderResultCore(string provider, WslResult result, ProviderStatus? prefetchedStatus, long generation)
    {
        if (!result.Success && (result.ExitCode == 124 ||
            (result.ExitCode == -1 && result.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase))))
            return ProviderError(provider, "Request timed out", prefetchedStatus, generation);
        if (result.ExitCode == 127)
            return ProviderError(provider, string.IsNullOrWhiteSpace(result.Error) ? "Command not found (exit code 127)" : result.Error.Trim(),
                prefetchedStatus, generation, result.ExitCode);
        if (!result.Success && result.ExitCode is 125 or 429 or >= 500 and <= 599)
            return ProviderError(provider, string.IsNullOrWhiteSpace(result.Error)
                ? $"Command failed (exit code {result.ExitCode})" : result.Error.Trim(), prefetchedStatus, generation, result.ExitCode);
        if (!string.IsNullOrWhiteSpace(result.Output) &&
            (result.Success || result.Output.TrimStart().StartsWith('[') || result.Output.TrimStart().StartsWith('{')))
        {
            var data = ParseUsageJsonArray(result.Output).FirstOrDefault(d => string.Equals(d.Provider, provider, StringComparison.OrdinalIgnoreCase));
            if (data != null && (result.Success || data.HasError || data.AuthRequired))
            {
                if (!result.Success && !data.AuthRequired && AuthFailureDetector.IsAuthFailure(provider, result.ExitCode, null, result.Error))
                    data = data with { AuthRequired = true, AuthMessage = AuthFailureDetector.SanitizeMessage(result.Error) };
                data = AttachProviderStatus(data, prefetchedStatus);
                return ResolveUsage(data with { Provider = provider }, generation, prefetchedStatus != null);
            }
            if (result.Success) return ProviderError(provider, "No data available", prefetchedStatus, generation);
        }
        _logger.LogDebug("{Provider} failed (exit {ExitCode})", provider, result.ExitCode);
        return ProviderError(provider, string.IsNullOrWhiteSpace(result.Error)
            ? string.IsNullOrWhiteSpace(result.Output) ? $"Command failed (exit code {result.ExitCode})" : result.Output.Trim()
            : result.Error.Trim(), prefetchedStatus, generation, result.ExitCode);
    }

    private Dictionary<string, StatusPrefetch> StartProviderStatusPrefetches(IReadOnlyList<string> providers, CancellationToken ct)
    {
        var tasks = new Dictionary<string, StatusPrefetch>(StringComparer.OrdinalIgnoreCase);
        if (_settingsService.Settings.DeveloperModeEnabled || providers.Count == 0 ||
            (_incidentFetcher == null && _geminiStatusFallback == null))
        {
            return tasks;
        }

        var distinct = providers
            .Where(ProviderConstants.IsValidProvider)
            .Select(ProviderConstants.ValidateAndNormalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var p in distinct)
        {
            if (_incidentFetcher != null)
            {
                if (string.Equals(p, "claude", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, "codex", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, "gemini", StringComparison.OrdinalIgnoreCase))
                {
                    tasks[p] = new(_timeProvider.GetTimestamp(), FetchProviderStatusAsync(p, ct));
                }
            }
            else if (_geminiStatusFallback != null && string.Equals(p, "gemini", StringComparison.OrdinalIgnoreCase))
            {
                tasks[p] = new(_timeProvider.GetTimestamp(), FetchProviderStatusAsync(p, ct));
            }
        }
        return tasks;
    }

    /// <summary>
    /// Waits for a prefetched status until <see cref="StatusDeadline"/> after its prefetch started. When that time has
    /// already passed (a slow usage read), a finished status is used and an unfinished one is not waited for.
    /// </summary>
    private async Task<ProviderStatus?> AwaitPrefetchedStatusAsync(string provider, Dictionary<string, StatusPrefetch> prefetchTasks, CancellationToken ct)
    {
        if (!prefetchTasks.TryGetValue(provider, out var prefetch))
            return null;

        try
        {
            var remaining = StatusDeadline - _timeProvider.GetElapsedTime(prefetch.StartedAt);
            if (remaining <= TimeSpan.Zero)
                return prefetch.Task.IsCompleted ? await prefetch.Task.ConfigureAwait(false) : null;
            return await prefetch.Task.WaitAsync(remaining, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Provider status unavailable within refresh budget for {Provider}", provider);
            return null;
        }
    }

    private async Task<ProviderStatus?> FetchProviderStatusAsync(string provider, CancellationToken ct)
    {
        if (_incidentFetcher != null)
        {
            return await _incidentFetcher.GetIncidentAsync(provider, ct).ConfigureAwait(false);
        }
        if (string.Equals(provider, "gemini", StringComparison.OrdinalIgnoreCase) && _geminiStatusFallback != null)
        {
            return await _geminiStatusFallback.GetStatusAsync(ct).ConfigureAwait(false);
        }
        return null;
    }

    private UsageData AttachProviderStatus(UsageData data, ProviderStatus? status)
    {
        var provider = data.Provider;
        var cached = _cacheService.GetLastKnown(provider);
        if (status != null) data = data with { StatusFetchOutcome = FetchOutcome.Success };

        // Case 1: Provider had NO status from CLI (e.g. Gemini, where CLI gives no status)
        if (data.ServiceStatus == null)
        {
            return status == null ? data : data with { ServiceStatus = status };
        }

        // Case 2: CLI SUPPLIED a status (e.g. Claude, Codex, etc.)
        var cliStatus = data.ServiceStatus;

        // MERGE rule 0: a replayed (cached) CLI status is older than the status page the app just fetched.
        // Prefer the freshly fetched status over a replayed CLI status in both directions:
        // 1. Outage: replay says healthy, but fetched page reports an open incident.
        // 2. Recovery: replay says outage, but fetched page reports healthy (indicator None).
        var observationAt = data.DataUpdatedAt ?? data.CacheUpdatedAt;
        var isCliReplay = data.UsageIsCached || (!data.UsageIsCached && observationAt.HasValue && Helpers.DataAge.OldReadingUtc(data.FetchedAt, observationAt.Value) != null);
        if (isCliReplay && status != null)
        {
            if (status.Indicator == ProviderStatusIndicator.None)
            {
                return data with
                {
                    ServiceStatus = status with
                    {
                        IncidentTitle = null,
                        LatestUpdateText = null,
                        LatestUpdateAt = null,
                        Url = !string.IsNullOrWhiteSpace(status.Url) ? status.Url : cliStatus.Url
                    }
                };
            }

            if (cliStatus.Indicator == ProviderStatusIndicator.None &&
                status.Indicator != ProviderStatusIndicator.None &&
                (!string.IsNullOrWhiteSpace(status.IncidentTitle) || !string.IsNullOrWhiteSpace(status.LatestUpdateText)))
            {
                return data with { ServiceStatus = status };
            }
        }

        // MERGE rule 1: If CLI returned an indicator of 'None' (healthy), clear any old incident detail
        // (no stale incident text on a healthy provider).
        if (cliStatus.Indicator == ProviderStatusIndicator.None)
        {
            return data with
            {
                ServiceStatus = cliStatus with
                {
                    IncidentTitle = null,
                    LatestUpdateText = null,
                    LatestUpdateAt = null
                }
            };
        }

        // CLI reported non-None indicator (e.g. Minor, Major, Maintenance, etc.)
        if (status != null)
        {
            // MERGE rule 2: If the fetcher returns an indicator that is 'none' or no open incident,
            // clear any old incident detail (no stale incident text on a healthy provider).
            var hasOpenIncident = status.Indicator != ProviderStatusIndicator.None &&
                (!string.IsNullOrWhiteSpace(status.IncidentTitle) || !string.IsNullOrWhiteSpace(status.LatestUpdateText));

            if (!hasOpenIncident)
            {
                return data with
                {
                    ServiceStatus = cliStatus with
                    {
                        IncidentTitle = null,
                        LatestUpdateText = null,
                        LatestUpdateAt = null
                    }
                };
            }

            // MERGE rule 3: Fetcher has an open incident: ADD incident details.
            // CLI's status indicator/description/url stay authoritative (do not overwrite them).
            return data with
            {
                ServiceStatus = cliStatus with
                {
                    IncidentTitle = status.IncidentTitle,
                    LatestUpdateText = status.LatestUpdateText,
                    LatestUpdateAt = status.LatestUpdateAt,
                    Url = !string.IsNullOrWhiteSpace(cliStatus.Url) ? cliStatus.Url : status.Url
                }
            };
        }
        else
        {
            // Fetcher failed or timed out (status == null).
            // Keep StatusFetchOutcome semantics for ResolveUsage intact (a fetcher failure must not mark a CLI-successful status as failed).
            // If cached data had incident detail and CLI indicator is still non-None, retain previous detail as stale:
            var prevStatus = cached?.ServiceStatus;
            if (prevStatus != null && (!string.IsNullOrWhiteSpace(prevStatus.IncidentTitle) || !string.IsNullOrWhiteSpace(prevStatus.LatestUpdateText)))
            {
                return data with
                {
                    ServiceStatus = cliStatus with
                    {
                        IncidentTitle = prevStatus.IncidentTitle,
                        LatestUpdateText = prevStatus.LatestUpdateText,
                        LatestUpdateAt = prevStatus.LatestUpdateAt
                    }
                };
            }

            return data;
        }
    }

    private UsageData ProviderError(string provider, string error, ProviderStatus? prefetchedStatus = null, long generation = 0,
        int? exitCode = null) => ResolveUsage(AttachProviderStatus(new UsageData
    {
        Provider = provider, Error = AuthFailureDetector.SanitizeMessage(error), FetchedAt = _timeProvider.GetUtcNow().UtcDateTime,
        AuthRequired = AuthFailureDetector.IsAuthFailure(provider, exitCode, null, error),
        AuthMessage = AuthFailureDetector.IsAuthFailure(provider, exitCode, null, error) ? AuthFailureDetector.SanitizeMessage(error) : null,
        CommandNotFound = exitCode == 127,
        UsageFetchOutcome = FetchOutcome.Failed, StatusFetchOutcome = FetchOutcome.Failed
    }, prefetchedStatus), generation, prefetchedStatus != null);

    private UsageData ResolveUsage(UsageData data, long generation, bool statusFetchedThisRefresh = false)
    {
        var state = _providerRefreshes.GetOrAdd(data.Provider, _ => new());
        // Keep the entire read/merge/write atomic. Requests are numbered before network work,
        // so completion order cannot let a superseded result replace a newer success.
        UsageData resolved;
        lock (state) resolved = ResolveUsageCore(data, generation, state, statusFetchedThisRefresh);
        // In-memory comparison/dedupe and background persistence start after releasing provider state.
        if (_earlyResetStore != null)
        {
            try { ProcessEarlyResets(resolved, _timeProvider.GetUtcNow().UtcDateTime); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to process early resets for {Provider}", data.Provider); }
        }
        return resolved;
    }

    private UsageData ResolveUsageCore(UsageData data, long generation, ProviderRefreshState state, bool statusFetchedThisRefresh)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        bool usageSuperseded;
        bool statusSuperseded;
        lock (state)
        {
            usageSuperseded = generation < state.PublishedUsage;
            statusSuperseded = generation < state.PublishedStatus;
        }
        var cached = _cacheService.GetLastKnown(data.Provider);
        if (cached != null) cached = CacheRetention.PruneExpired(cached, now);
        var previous = cached?.HasUsage == true ? cached : null;
        if (previous != null)
        {
            var prevSuccess = Helpers.DashboardValues.Utc(previous.LastSuccessfulFetch ?? previous.FetchedAt);
            if (now - prevSuccess > CacheRetention.MaxAge) previous = null;
        }

        var observationAt = data.DataUpdatedAt ?? data.CacheUpdatedAt;
        var obsAtUtc = observationAt.HasValue ? Helpers.DashboardValues.Utc(observationAt.Value) : (DateTime?)null;
        DateTime? prevObsAt = previous != null
            ? Helpers.DashboardValues.Utc(previous.DataUpdatedAt ?? previous.CacheUpdatedAt ?? previous.LastSuccessfulFetch ?? previous.FetchedAt)
            : null;

        var isCached = data.UsageIsCached || (!data.UsageIsCached && observationAt.HasValue && Helpers.DataAge.OldReadingUtc(data.FetchedAt, observationAt.Value) != null);
        var isObservationRetained = obsAtUtc.HasValue && CacheRetention.IsRetained(obsAtUtc.Value, now);

        // Auth belongs to this session, not the last-good cache. Only the newest request may
        // publish it. A generic failure or cache replay cannot prove that sign-in recovered.
        if (generation >= state.PublishedAuth && generation >= state.Issued)
        {
            state.PublishedAuth = generation;
            if (data.AuthRequired)
            {
                state.AuthRequired = true;
                state.AuthMessage = data.AuthMessage;
            }
            else if (!isCached && !data.UsageIsStale && data.UsageFetchOutcome == FetchOutcome.Success)
            {
                state.AuthRequired = false;
                state.AuthMessage = null;
            }
        }
        var freshSuccess = !usageSuperseded && !state.AuthRequired && !isCached && !data.UsageIsStale && data.UsageFetchOutcome == FetchOutcome.Success;
        var isNewerCachedReading = !usageSuperseded && isCached && data.HasUsage && isObservationRetained &&
            (previous == null || prevObsAt == null || obsAtUtc!.Value > prevObsAt.Value);
        var isReplayOfHeld = !usageSuperseded && isCached && data.HasUsage && obsAtUtc.HasValue &&
            (previous != null && prevObsAt != null && obsAtUtc!.Value <= prevObsAt.Value);

        var acceptsUsage = freshSuccess || isNewerCachedReading || isReplayOfHeld;
        var usageAt = isNewerCachedReading ? (obsAtUtc!.Value > now ? now : obsAtUtc.Value) : now;

        var statusSuccess = !statusSuperseded && data.ServiceStatus != null &&
            (statusFetchedThisRefresh || (!isCached && data.StatusFetchOutcome == FetchOutcome.Success));
        var cachedStatusAt = data.CacheUpdatedAt ?? observationAt;
        var cachedStatus = !statusSuperseded && isCached && data.ServiceStatus != null &&
            cached?.ServiceStatus == null && cachedStatusAt.HasValue && CacheRetention.IsRetained(cachedStatusAt.Value, now);
        // A held replay may still clear incident detail or supply the CLI's authoritative indicator,
        // but cannot renew status freshness or restore a status that has expired independently.
        var replayStatus = !statusSuperseded && isReplayOfHeld && data.ServiceStatus != null && cached?.ServiceStatus != null;

        var isHeldStale = prevObsAt.HasValue && (now - prevObsAt.Value > ShimTtl);
        var isNewerStale = obsAtUtc.HasValue && (now - obsAtUtc.Value > ShimTtl);
        var isUsageStale = !freshSuccess && (isReplayOfHeld ? isHeldStale : (isNewerCachedReading ? isNewerStale : (acceptsUsage || previous?.HasUsage == true)));

        var resolved = data with
        {
            Session = acceptsUsage ? (isReplayOfHeld ? previous!.Session : data.Session) : previous?.Session,
            Weekly = acceptsUsage ? (isReplayOfHeld ? previous!.Weekly : data.Weekly) : previous?.Weekly,
            Tertiary = acceptsUsage ? (isReplayOfHeld ? previous!.Tertiary : data.Tertiary) : previous?.Tertiary,
            ExtraWindows = acceptsUsage ? (isReplayOfHeld ? previous!.ExtraWindows : data.ExtraWindows) : previous?.ExtraWindows ?? [],
            Credits = acceptsUsage ? (isReplayOfHeld ? previous!.Credits : data.Credits) : previous?.Credits,
            Plan = acceptsUsage ? (isReplayOfHeld ? previous!.Plan : data.Plan) : previous?.Plan,
            Status = acceptsUsage ? (isReplayOfHeld ? previous!.Status : data.Status) : previous?.Status,
            // A replay inside the shim TTL is a current answer (the shim vouches for it for ShimTtl): the app fetched it now, so it is
            // not "stale" just because the observation is a few minutes old. Beyond the TTL the original time is kept so a replay
            // can never renew its own expiry. LastSuccessfulFetch (below) always keeps the original observation time.
            FetchedAt = acceptsUsage ? (isReplayOfHeld ? (isHeldStale ? previous!.FetchedAt : now) : usageAt) : previous?.FetchedAt ?? data.FetchedAt,
            DataUpdatedAt = acceptsUsage ? (isReplayOfHeld ? previous!.DataUpdatedAt : data.DataUpdatedAt) : previous?.DataUpdatedAt,
            CacheUpdatedAt = acceptsUsage ? (isReplayOfHeld ? previous!.CacheUpdatedAt : data.CacheUpdatedAt) : previous?.CacheUpdatedAt,
            LastSuccessfulFetch = acceptsUsage ? (isReplayOfHeld ? previous!.LastSuccessfulFetch : usageAt) : previous?.LastSuccessfulFetch ?? previous?.FetchedAt,
            UsageFetchOutcome = acceptsUsage ? FetchOutcome.Success : (usageSuperseded || isCached ? FetchOutcome.Unavailable : data.UsageFetchOutcome),
            UsageIsCached = isCached || isReplayOfHeld || (previous?.UsageIsCached == true && !freshSuccess),
            UsageIsStale = isUsageStale || state.AuthRequired,
            AuthRequired = state.AuthRequired,
            AuthMessage = state.AuthRequired ? state.AuthMessage : null,
            ServiceStatus = statusSuccess || cachedStatus || replayStatus ? data.ServiceStatus : cached?.ServiceStatus,
            LastSuccessfulStatusFetch = statusSuccess ? now : cachedStatus ? Helpers.DashboardValues.Utc(cachedStatusAt!.Value) : cached?.LastSuccessfulStatusFetch,
            StatusIsStale = !statusSuccess && (cachedStatus || cached?.ServiceStatus != null),
            Cost = CacheRetention.RetainedCost(previous?.Cost, now),
            Error = acceptsUsage ? null : data.Error ?? "No usage reading available"
        };
        // A held or replayed reading is not fresh: a window whose reset time has passed belongs to a period that has
        // ended, so it shows 0% used instead of its old numbers (as the native clients' last-good readings do).
        if (!freshSuccess) resolved = ClearEndedWindows(resolved, now);
        // Persist complete values independently: a status-only success must not discard the usage reading.
        if (acceptsUsage || statusSuccess || cachedStatus)
        {
            lock (state)
            {
                if (acceptsUsage)
                {
                    state.PublishedUsage = Math.Max(state.PublishedUsage, generation);
                }
                if (statusSuccess || cachedStatus)
                {
                    state.PublishedStatus = Math.Max(state.PublishedStatus, generation);
                }
            }
            _cacheService.Set(data.Provider, freshSuccess ? resolved : resolved with
            {
                Error = null, IsLoading = false, CommandNotFound = false,
                AuthRequired = false, AuthMessage = null,
                UsageFetchOutcome = resolved.HasUsage ? FetchOutcome.Success : FetchOutcome.Unknown
            });
        }
        return resolved;
    }

    private static UsageData ClearEndedWindows(UsageData data, DateTime nowUtc)
    {
        static bool Ended(UsageWindow? window, DateTime now) =>
            window?.ResetAt is { } reset && Helpers.DashboardValues.Utc(reset) <= now;
        static UsageWindow? Clear(UsageWindow? window, DateTime now) =>
            Ended(window, now) ? window! with { Used = 0, ReportedPercent = 0, ResetAt = null, ResetIn = null } : window;

        if (!Ended(data.Session, nowUtc) && !Ended(data.Weekly, nowUtc) && !Ended(data.Tertiary, nowUtc) &&
            !(data.ExtraWindows ?? []).Any(e => Ended(e.Window, nowUtc)))
            return data;
        return data with
        {
            Session = Clear(data.Session, nowUtc),
            Weekly = Clear(data.Weekly, nowUtc),
            Tertiary = Clear(data.Tertiary, nowUtc),
            ExtraWindows = (data.ExtraWindows ?? []).Select(e => Ended(e.Window, nowUtc) ? e with { Window = Clear(e.Window, nowUtc)! } : e).ToList()
        };
    }

    private void ProcessEarlyResets(UsageData current, DateTime nowUtc)
    {
        var resets = _earlyResetStore!.Observe(current, nowUtc);
        foreach (var earlyReset in resets)
        {
            bool isNew;
            try
            {
                isNew = _earlyResetStore!.Record(earlyReset);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record early reset for {Provider}", earlyReset.Provider);
                continue;
            }

            if (isNew)
            {
                _logger.LogInformation("Early reset detected for {Provider} {Window}: dropped from {Prev:F0}% to {Cur:F0}%",
                    earlyReset.Provider, earlyReset.WindowKey, earlyReset.PreviousPercent, earlyReset.CurrentPercent);

                if (_settingsService?.Settings?.AlertsEnabled == true)
                {
                    // Reserve notification synchronously; persist the reservation before showing it.
                    // Save failures are logged by the store; the in-memory reservation still prevents repeats.
                    _earlyResetStore.MarkNotified(earlyReset);
                    var durable = _earlyResetStore.FlushAsync();
                    lock (_earlyResetTaskGate)
                    {
                        var preceding = _earlyResetNotifications;
                        _earlyResetNotifications = Task.Run(async () =>
                        {
                            await preceding.ConfigureAwait(false);
                            await durable.ConfigureAwait(false);
                            ShowEarlyResetNotification(earlyReset, nowUtc);
                        });
                    }
                }
            }
        }
    }

    private Task DrainEarlyResetNotificationsAsync()
    {
        lock (_earlyResetTaskGate) return _earlyResetNotifications;
    }

    private void ShowEarlyResetNotification(EarlyReset earlyReset, DateTime nowUtc)
    {
        var title = EarlyResetCaption.ToastTitle(earlyReset.Provider, earlyReset.WindowTitle);
        var message = EarlyResetCaption.ToastBody(earlyReset.PreviousPercent, earlyReset.CurrentPercent, earlyReset.PreviousResetAtUtc, nowUtc);
        try { _toastNotifier?.Show(title, message); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to show toast notification for early reset"); }
        try
        {
            _activityLog?.RecordAlert(new DashboardAlertEntry
            {
                TimestampUtc = nowUtc, ProviderId = earlyReset.Provider, Title = title, Message = message
            });
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to record alert in activity log"); }
    }

    /// <summary>Version reported when no enabled provider reads usage through the CLI.</summary>
    public const string NativeVersion = "native";

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        // Native usage needs no CLI: report the route instead of starting WSL on every dashboard build.
        if (AllUsageNative()) return NativeVersion;
        try
        {
            var result = await _wslService.ExecuteAsync("codexbar --version", ct);
            return result.Success ? result.Output.Trim() : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get codexbar version");
            return null;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (AllUsageNative()) return true;
        try
        {
            var result = await _wslService.ExecuteAsync("which codexbar", ct);
            return result.Success && !string.IsNullOrWhiteSpace(result.Output);
        }
        catch
        {
            return false;
        }
    }

    private List<UsageData> ParseUsageJsonArray(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            var fetchTime = _timeProvider.GetUtcNow().UtcDateTime;
            if (json.TrimStart().StartsWith('['))
            {
                var dtos = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageDataDto);
                return dtos?.Select(d => d.ToUsageData(fetchTime)).ToList() ?? [];
            }

            // Try single object
            var dto = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.UsageDataDto);
            return dto != null ? [dto.ToUsageData(fetchTime)] : [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse usage JSON array");
            return [];
        }
    }
}
