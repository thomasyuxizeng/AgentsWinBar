using System.Text.Json;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Service for interacting with codexbar CLI via WSL.
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
    private readonly ICostCacheStore? _costStore;
    private readonly ICostLedger? _costLedger;
    private readonly UsageHistoryStore? _history;
    private readonly IGeminiStatusFallback? _geminiStatusFallback;
    internal static readonly TimeSpan GeminiStatusBudget = TimeSpan.FromMilliseconds(500);
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
        public long Published;
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
        ICostLedger? costLedger = null)
    {
        _history = history;
        _costStore = costStore;
        _costLedger = costLedger;
        _geminiStatusFallback = geminiStatusFallback;
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
        return data;
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
                yield return result;
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

    private async Task FetchCostsAsync(IReadOnlyList<string> providers, bool forceRefresh, bool useBatch,
        Action<string, CostData?> onCompleted, CancellationToken ct)
    {
        var normalized = providers.Select(ProviderConstants.ValidateAndNormalize).Distinct(StringComparer.Ordinal).ToArray();
        ct.ThrowIfCancellationRequested();
        if (normalized.Length == 0 || !CostsEnabled) return;
        await _costGate.WaitAsync(ct);
        var previousAttempts = new Dictionary<string, (DateTimeOffset AttemptedAt, CostData? Data)?>();
        var completedProviders = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!CostsEnabled) return;
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
                    if (!forceRefresh && _costCache.TryGetValue(provider, out var cached) && now - cached.AttemptedAt < CostCacheTtl)
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
            var scanTimeout = TimeSpan.FromSeconds(Math.Max(CostScanTimeout.TotalSeconds,
                _settingsService.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds));
            var commands = due.Select(provider => new WslBatchCommand(
                Helpers.CostCommandBuilder.Build(provider, _userProfile(), _directoryExists), scanTimeout)).ToArray();
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
                var data = costsEnabled ? InterpretCostResult(due[index], result, now) : null;
                if (data == null && costsEnabled)
                    lock (_costCache) data = LastGoodCost(due[index], now);
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
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Cost scan unavailable for {Provider}", due[index]);
                    Complete(index, new WslResult());
                }
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
            await SaveCostStoreAsync();
            _costGate.Release();
        }
    }

    /// <summary>The last complete result, with its original totals and timestamp preserved.</summary>
    private CostData? LastGoodCost(string provider, DateTimeOffset now)
    {
        if (!_lastGoodCost.TryGetValue(provider, out var data)) return null;
        var staleCost = data with { IsStale = true, LastSuccessfulFetch = data.LastSuccessfulFetch ?? data.FetchedAt };
        if (_costLedger != null)
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, _timeProvider.LocalTimeZone));
            var ledger30 = _costLedger.GetLast30Days(provider, today);
            if (ledger30.DaysWithDataCount > 0 || ledger30.Days.Any(d => d.HasLedgerRecord))
            {
                var daily = ledger30.Days.Select(d => new DailyCost
                {
                    Date = d.Date,
                    TotalCost = d.CostUSD,
                    TotalTokens = d.TotalTokens,
                    InputTokens = d.InputTokens,
                    OutputTokens = d.OutputTokens,
                    CacheReadTokens = d.CacheReadTokens,
                    CacheCreationTokens = d.CacheWriteTokens,
                    ReasoningTokens = d.ReasoningTokens,
                    ModelBreakdowns = d.Models.Select(m => new ModelCost
                    {
                        ModelName = m.ModelName,
                        Cost = m.CostUSD,
                        TotalTokens = m.TotalTokens
                    }).ToArray()
                }).ToArray();

                staleCost = staleCost with
                {
                    Last30DaysCostUSD = ledger30.TotalCostUSD,
                    Last30DaysTokens = ledger30.TotalTokens,
                    Daily = daily
                };
            }
        }
        return staleCost;
    }

    private async Task SaveCostStoreAsync()
    {
        if (_costStore == null) return;
        Dictionary<string, CostData>? snapshot = null;
        lock (_costCache)
        {
            if (!_costStoreDirty) return;
            snapshot = new Dictionary<string, CostData>(_lastGoodCost, StringComparer.Ordinal);
            _costStoreDirty = false;
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

    private CostData? InterpretCostResult(string provider, WslResult result, DateTimeOffset attemptedAt)
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
                }
            }
            if (data.HasData && _costLedger != null)
            {
                Helpers.AsyncHelper.SafeFireAndForget(
                    _costLedger.RecordScanAsync(data, attemptedAt.UtcDateTime),
                    onError: ex => _logger.LogDebug(ex, "Cost ledger update failed for {Provider}", provider));
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
        ProviderStatus? geminiStatus = null;
        try
        {
            geminiStatus = await PrefetchGeminiStatusAsync([normalizedProvider], ct);
            ct.ThrowIfCancellationRequested();
            var source = ProviderConstants.GetSource(normalizedProvider);
            var result = await _wslService.ExecuteAsync($"codexbar --provider {normalizedProvider} --format json --source {source} --status", ct);

            ct.ThrowIfCancellationRequested();
            return InterpretProviderResult(normalizedProvider, result, geminiStatus, generation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get usage for {Provider}", provider);
            return ProviderError(normalizedProvider, "Usage fetch unavailable", geminiStatus, generation);
        }
        finally { await SaveUsageCacheAsync(); }
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
            lifetime.Cancel();
            try { await producer; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
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
        var settings = _settingsService.Settings.Timeouts;
        var first = Volatile.Read(ref _isFirstFetch) == 1;
        var seconds = source == "cli"
            ? (first ? settings.CliProviderFirstFetchTimeoutSeconds : settings.CliProviderTimeoutSeconds)
            : (first ? settings.StandardProviderFirstFetchTimeoutSeconds : settings.StandardProviderTimeoutSeconds);
        var verbose = normalized == "gemini" ? " --verbose" : "";
        return new($"codexbar usage --provider {normalized} --format json --source {source} --status{verbose}",
            TimeSpan.FromSeconds(seconds));
    }

    private async Task<IReadOnlyList<UsageData>> FetchAllProvidersAsync(IReadOnlyList<string> providers,
        Action<UsageData>? onCompleted, CancellationToken ct)
    {
        try { return await FetchAllProvidersCoreAsync(providers, onCompleted, ct); }
        finally { if (!_settingsService.Settings.DeveloperModeEnabled) await SaveUsageCacheAsync(); }
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
            if (results[index] != null) return;
            results[index] = data;
            onCompleted?.Invoke(data);
        }

        if (_settingsService.Settings.DeveloperModeEnabled)
        {
            for (var i = 0; i < providers.Count; i++) Complete(i, LoadSample(providers[i]));
        }
        else
        {
            var generations = providers.Select(BeginProviderRefresh).ToArray();
            var geminiStatus = await PrefetchGeminiStatusAsync(providers, ct);
            ct.ThrowIfCancellationRequested();
            var commands = providers.Select(BuildProviderCommand).ToArray();
            var retryInIndividualShell = new bool[providers.Count];
            async Task RetryProviderAsync(int index)
            {
                if (retryInIndividualShell[index])
                {
                    _logger.LogInformation("WSL batch command not found for {Provider}; retrying individually", providers[index]);
                    RecordBatchFallback(providers[index], "Command failed (exit code 127)");
                }
                Complete(index, await FetchProviderAsync(providers[index], commands[index], geminiStatus, generations[index], ct));
            }
            try
            {
                var batch = await _wslService.ExecuteBatchAsync(commands, (index, result) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (index < 0 || index >= results.Length)
                        throw new InvalidDataException("WSL batch returned an invalid command index");
                    // The nested batch shell cannot see aliases or unexported functions from .bashrc.
                    if (result.ExitCode == 127)
                    {
                        retryInIndividualShell[index] = true;
                        return;
                    }
                    if (results[index] == null) Complete(index, InterpretProviderResult(providers[index], result, geminiStatus, generations[index]));
                }, ct);
                ct.ThrowIfCancellationRequested();
                if (batch == null || batch.Count != commands.Length)
                    throw new InvalidDataException("WSL batch returned an incomplete result list");
                for (var i = 0; i < batch.Count; i++)
                {
                    if (results[i] != null) continue;
                    if (batch[i].ExitCode == 127)
                    {
                        retryInIndividualShell[i] = true;
                    }
                    else Complete(i, InterpretProviderResult(providers[i], batch[i], geminiStatus, generations[i]));
                }
                await Task.WhenAll(Enumerable.Range(0, providers.Count)
                    .Where(i => results[i] == null && retryInIndividualShell[i]).Select(RetryProviderAsync));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogWarning(ex, "WSL batch unavailable; retrying unfinished providers individually");
                RecordBatchFallback("wsl", ex.Message);
                await Task.WhenAll(Enumerable.Range(0, providers.Count).Where(i => results[i] == null)
                    .Select(RetryProviderAsync));
            }
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

    private async Task<UsageData> FetchProviderAsync(string provider, WslBatchCommand command, ProviderStatus? geminiStatus, long generation, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(command.Timeout);
        try
        {
            var result = await _wslService.ExecuteAsync(command.Command, deadline.Token);
            ct.ThrowIfCancellationRequested();
            return deadline.IsCancellationRequested
                ? ProviderError(provider, "Request timed out", geminiStatus, generation)
                : InterpretProviderResult(provider, result, geminiStatus, generation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ProviderError(provider, "Request timed out", geminiStatus, generation); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Provider}", provider);
            return ProviderError(provider, "Unexpected error occurred", geminiStatus, generation);
        }
    }

    private UsageData InterpretProviderResult(string provider, WslResult result, ProviderStatus? geminiStatus, long generation)
    {
        try { return InterpretProviderResultCore(provider, result, geminiStatus, generation); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to interpret {Provider}", provider);
            return ProviderError(provider, "Unexpected error occurred", geminiStatus, generation);
        }
    }

    private UsageData InterpretProviderResultCore(string provider, WslResult result, ProviderStatus? geminiStatus, long generation)
    {
        if (!result.Success && (result.ExitCode == 124 ||
            (result.ExitCode == -1 && result.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase))))
            return ProviderError(provider, "Request timed out", geminiStatus, generation);
        if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
        {
            var data = ParseUsageJsonArray(result.Output).FirstOrDefault(d => string.Equals(d.Provider, provider, StringComparison.OrdinalIgnoreCase));
            if (data == null) return ProviderError(provider, "No data available", geminiStatus, generation);
            data = AttachGeminiStatus(data, geminiStatus);
            data = ResolveUsage(data with { Provider = provider }, generation);
            _logger.LogDebug("Fetched {Provider} successfully", provider);
            return data;
        }
        _logger.LogDebug("{Provider} failed: {Error}", provider, result.Error);
        return ProviderError(provider, string.IsNullOrWhiteSpace(result.Error)
            ? $"Command failed (exit code {result.ExitCode})" : result.Error.Trim(), geminiStatus, generation) with
        {
            CommandNotFound = result.ExitCode == 127
        };
    }

    private async Task<ProviderStatus?> PrefetchGeminiStatusAsync(IReadOnlyList<string> providers, CancellationToken ct)
    {
        if (_geminiStatusFallback == null || _settingsService.Settings.DeveloperModeEnabled ||
            !providers.Contains("gemini", StringComparer.OrdinalIgnoreCase)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(GeminiStatusBudget);
        Task<ProviderStatus?>? pending = null;
        try
        {
            pending = _geminiStatusFallback.GetStatusAsync(deadline.Token);
            // Also bound implementations that fail to observe cancellation.
            return await pending.WaitAsync(GeminiStatusBudget, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gemini status fallback unavailable within refresh budget");
            return null;
        }
        finally
        {
            deadline.Cancel();
            if (pending != null)
                Helpers.AsyncHelper.SafeFireAndForget(pending,
                    onError: ex => _logger.LogDebug(ex, "Gemini status fallback completed after refresh budget"));
        }
    }

    private static UsageData AttachGeminiStatus(UsageData data, ProviderStatus? status)
    {
        if (!string.Equals(data.Provider, "gemini", StringComparison.OrdinalIgnoreCase) || data.ServiceStatus != null)
            return data;
        // A fallback status is a fresh status reading: mark it so ResolveUsage keeps it instead of the cached one.
        return status == null ? data : data with { ServiceStatus = status, StatusFetchOutcome = FetchOutcome.Success };
    }

    private UsageData ProviderError(string provider, string error, ProviderStatus? geminiStatus = null, long generation = 0) => ResolveUsage(AttachGeminiStatus(new UsageData
    {
        Provider = provider, Error = error, FetchedAt = _timeProvider.GetUtcNow().UtcDateTime,
        UsageFetchOutcome = FetchOutcome.Failed, StatusFetchOutcome = FetchOutcome.Failed
    }, geminiStatus), generation);

    private UsageData ResolveUsage(UsageData data, long generation)
    {
        var state = _providerRefreshes.GetOrAdd(data.Provider, _ => new());
        // Keep the entire read/merge/write atomic. Requests are numbered before network work,
        // so completion order cannot let a superseded result replace a newer success.
        lock (state) return ResolveUsageCore(data, generation, state);
    }

    private UsageData ResolveUsageCore(UsageData data, long generation, ProviderRefreshState state)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var superseded = generation < state.Published;
        var cached = _cacheService.GetLastKnown(data.Provider);
        if (cached != null) cached = CacheRetention.PruneExpired(cached, now);
        var previous = cached?.HasUsage == true ? cached : _history?.GetLastKnownUsage(data.Provider, now);
        if (previous != null)
        {
            var prevSuccess = Helpers.DashboardValues.Utc(previous.LastSuccessfulFetch ?? previous.FetchedAt);
            if (now - prevSuccess > CacheRetention.MaxAge) previous = null;
        }
        var observationAt = data.DataUpdatedAt ?? data.CacheUpdatedAt;
        var cachedReading = !superseded && data.UsageIsCached && data.HasUsage &&
            observationAt.HasValue && CacheRetention.IsRetained(observationAt.Value, now) &&
            (previous == null || Helpers.DashboardValues.Utc(observationAt.Value) >=
                Helpers.DashboardValues.Utc(previous.LastSuccessfulFetch ?? previous.FetchedAt));
        var success = !superseded && !data.UsageIsCached && data.UsageFetchOutcome == FetchOutcome.Success;
        var acceptsUsage = success || cachedReading;
        var usageAt = cachedReading ? Helpers.DashboardValues.Utc(observationAt!.Value) : now;
        if (usageAt > now) usageAt = now;
        var statusSuccess = !superseded && !data.UsageIsCached && data.StatusFetchOutcome == FetchOutcome.Success;
        var cachedStatusAt = data.CacheUpdatedAt ?? observationAt;
        var cachedStatus = !superseded && data.UsageIsCached && data.ServiceStatus != null &&
            cached?.ServiceStatus == null && cachedStatusAt.HasValue && CacheRetention.IsRetained(cachedStatusAt.Value, now);
        var resolved = data with
        {
            Session = acceptsUsage ? data.Session : previous?.Session,
            Weekly = acceptsUsage ? data.Weekly : previous?.Weekly,
            Tertiary = acceptsUsage ? data.Tertiary : previous?.Tertiary,
            ExtraWindows = acceptsUsage ? data.ExtraWindows : previous?.ExtraWindows ?? [],
            Credits = acceptsUsage ? data.Credits : previous?.Credits,
            Plan = acceptsUsage ? data.Plan : previous?.Plan,
            Status = acceptsUsage ? data.Status : previous?.Status,
            FetchedAt = acceptsUsage ? usageAt : previous?.FetchedAt ?? data.FetchedAt,
            DataUpdatedAt = acceptsUsage ? data.DataUpdatedAt : previous?.DataUpdatedAt,
            LastSuccessfulFetch = acceptsUsage ? usageAt : previous?.LastSuccessfulFetch ?? previous?.FetchedAt,
            UsageFetchOutcome = superseded || (data.UsageIsCached && !cachedReading) ? FetchOutcome.Unavailable : data.UsageFetchOutcome,
            UsageIsStale = !success && (cachedReading || previous?.HasUsage == true),
            ServiceStatus = statusSuccess || cachedStatus ? data.ServiceStatus : cached?.ServiceStatus,
            LastSuccessfulStatusFetch = statusSuccess ? now : cachedStatus ? Helpers.DashboardValues.Utc(cachedStatusAt!.Value) : cached?.LastSuccessfulStatusFetch,
            StatusIsStale = !statusSuccess && (cachedStatus || cached?.ServiceStatus != null),
            Cost = CacheRetention.RetainedCost(previous?.Cost, now),
            Error = acceptsUsage ? null : data.Error ?? "No usage reading available"
        };
        // Persist complete values independently: a status-only success must not discard the usage reading.
        if (acceptsUsage || statusSuccess || cachedStatus)
        {
            state.Published = Math.Max(state.Published, generation);
            _cacheService.Set(data.Provider, success ? resolved : resolved with
            {
                Error = null, IsLoading = false, CommandNotFound = false,
                UsageFetchOutcome = resolved.HasUsage ? FetchOutcome.Success : FetchOutcome.Unknown
            });
        }
        return resolved;
    }
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
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

            // Try array first
            if (json.TrimStart().StartsWith('['))
            {
                var dtos = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageDataDto);
                return dtos?.Select(d => d.ToUsageData()).ToList() ?? [];
            }

            // Try single object
            var dto = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.UsageDataDto);
            return dto != null ? [dto.ToUsageData()] : [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse usage JSON array");
            return [];
        }
    }
}
