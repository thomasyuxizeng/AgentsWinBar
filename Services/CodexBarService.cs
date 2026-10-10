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
    private readonly UsageHistoryStore? _history;
    // The last complete result per provider (guarded by _costCache). A failed, empty or timed-out scan serves it
    // instead of blanking the totals; only a newer good result replaces it.
    private readonly Dictionary<string, CostData> _lastGoodCost = [];
    // Results restored from disk that this session hasn't rescanned yet; published once, before the first scan.
    private readonly HashSet<string> _restoredCost = [];
    private bool _costStoreLoaded;
    private bool _costStoreDirty;


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
        UsageHistoryStore? history = null)
    {
        _history = history;
        _costStore = costStore;
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
        var successAt = (data.LastSuccessfulFetch ?? data.FetchedAt).ToUniversalTime();
        if (now.UtcDateTime - successAt > CacheRetention.MaxAge)
        {
            _lastGoodCost.Remove(provider);
            _costStoreDirty = true;
            return null;
        }
        return data with { IsStale = true, LastSuccessfulFetch = data.LastSuccessfulFetch ?? data.FetchedAt };
    }

    private async Task SaveCostStoreAsync()
    {
        if (_costStore == null) return;
        Dictionary<string, CostData>? snapshot = null;
        lock (_costCache)
        {
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var (k, v) in _lastGoodCost.ToList())
            {
                var successAt = (v.LastSuccessfulFetch ?? v.FetchedAt).ToUniversalTime();
                if (nowUtc - successAt > CacheRetention.MaxAge)
                {
                    _lastGoodCost.Remove(k);
                    _costStoreDirty = true;
                }
            }
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
        try
        {
            var source = ProviderConstants.GetSource(normalizedProvider);
            var result = await _wslService.ExecuteAsync($"codexbar --provider {normalizedProvider} --format json --source {source} --status", ct);

            ct.ThrowIfCancellationRequested();
            return InterpretProviderResult(normalizedProvider, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get usage for {Provider}", provider);
            return ProviderError(normalizedProvider, "Usage fetch unavailable");
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
            var commands = providers.Select(BuildProviderCommand).ToArray();
            var retryInIndividualShell = new bool[providers.Count];
            async Task RetryProviderAsync(int index)
            {
                if (retryInIndividualShell[index])
                {
                    _logger.LogInformation("WSL batch command not found for {Provider}; retrying individually", providers[index]);
                    RecordBatchFallback(providers[index], "Command failed (exit code 127)");
                }
                Complete(index, await FetchProviderAsync(providers[index], commands[index], ct));
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
                    if (results[index] == null) Complete(index, InterpretProviderResult(providers[index], result));
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
                    else Complete(i, InterpretProviderResult(providers[i], batch[i]));
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

    private async Task<UsageData> FetchProviderAsync(string provider, WslBatchCommand command, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(command.Timeout);
        try
        {
            var result = await _wslService.ExecuteAsync(command.Command, deadline.Token);
            ct.ThrowIfCancellationRequested();
            return deadline.IsCancellationRequested
                ? ProviderError(provider, "Request timed out")
                : InterpretProviderResult(provider, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ProviderError(provider, "Request timed out"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Provider}", provider);
            return ProviderError(provider, "Unexpected error occurred");
        }
    }

    private UsageData InterpretProviderResult(string provider, WslResult result)
    {
        try { return InterpretProviderResultCore(provider, result); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to interpret {Provider}", provider);
            return ProviderError(provider, "Unexpected error occurred");
        }
    }

    private UsageData InterpretProviderResultCore(string provider, WslResult result)
    {
        if (!result.Success && (result.ExitCode == 124 ||
            (result.ExitCode == -1 && result.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase))))
            return ProviderError(provider, "Request timed out");
        if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
        {
            var data = ParseUsageJsonArray(result.Output).FirstOrDefault(d => string.Equals(d.Provider, provider, StringComparison.OrdinalIgnoreCase));
            if (data == null) return ProviderError(provider, "No data available");
            data = ResolveUsage(data with { Provider = provider });
            _logger.LogDebug("Fetched {Provider} successfully", provider);
            return data;
        }
        _logger.LogDebug("{Provider} failed: {Error}", provider, result.Error);
        return ProviderError(provider, string.IsNullOrWhiteSpace(result.Error)
            ? $"Command failed (exit code {result.ExitCode})" : result.Error.Trim()) with
        {
            CommandNotFound = result.ExitCode == 127
        };
    }

    private UsageData ProviderError(string provider, string error) => ResolveUsage(new UsageData
    {
        Provider = provider, Error = error, FetchedAt = _timeProvider.GetUtcNow().UtcDateTime,
        UsageFetchOutcome = FetchOutcome.Failed, StatusFetchOutcome = FetchOutcome.Failed
    });

    private UsageData ResolveUsage(UsageData data)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cached = _cacheService.GetLastKnown(data.Provider);
        var previous = cached?.HasUsage == true ? cached : _history?.GetLastKnownUsage(data.Provider);
        if (previous != null)
        {
            var prevSuccess = Helpers.DashboardValues.Utc(previous.LastSuccessfulFetch ?? previous.FetchedAt);
            if (now - prevSuccess > CacheRetention.MaxAge) previous = null;
        }
        var success = data.UsageFetchOutcome == FetchOutcome.Success;
        var statusSuccess = data.StatusFetchOutcome == FetchOutcome.Success;
        var resolved = data with
        {
            Session = success ? data.Session : previous?.Session,
            Weekly = success ? data.Weekly : previous?.Weekly,
            Tertiary = success ? data.Tertiary : previous?.Tertiary,
            ExtraWindows = success ? data.ExtraWindows : previous?.ExtraWindows ?? [],
            Credits = success ? data.Credits : previous?.Credits,
            Plan = success ? data.Plan : previous?.Plan,
            Status = success ? data.Status : previous?.Status,
            FetchedAt = success ? now : previous?.FetchedAt ?? data.FetchedAt,
            DataUpdatedAt = success ? data.DataUpdatedAt : previous?.DataUpdatedAt,
            LastSuccessfulFetch = success ? now : previous?.LastSuccessfulFetch ?? previous?.FetchedAt,
            UsageIsStale = !success && previous?.HasUsage == true,
            ServiceStatus = statusSuccess ? data.ServiceStatus : cached?.ServiceStatus,
            LastSuccessfulStatusFetch = statusSuccess ? now : cached?.LastSuccessfulStatusFetch ??
                cached?.ServiceStatus?.UpdatedAt ?? (cached?.ServiceStatus != null ? cached.FetchedAt : null),
            StatusIsStale = !statusSuccess && cached?.ServiceStatus != null,
            Error = success ? null : data.Error ?? "No usage reading available"
        };
        // Persist complete values independently: a status-only success must not discard the usage reading.
        if (success || statusSuccess)
            _cacheService.Set(data.Provider, success ? resolved : resolved with
            {
                Error = null, IsLoading = false, CommandNotFound = false,
                UsageFetchOutcome = resolved.HasUsage ? FetchOutcome.Success : FetchOutcome.Unknown
            });
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
