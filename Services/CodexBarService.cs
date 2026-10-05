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
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _userProfile;
    private readonly Func<string, bool> _directoryExists;
    private readonly SemaphoreSlim _costGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset AttemptedAt, CostData? Data)> _costCache = [];


    public CodexBarService(
        IWslService wslService,
        ICacheService cacheService,
        ISettingsService settingsService,
        ISampleDataLoader sampleDataLoader,
        ILogger<CodexBarService> logger,
        TimeProvider? timeProvider = null,
        Func<string>? userProfile = null,
        Func<string, bool>? directoryExists = null)
    {
        _wslService = wslService;
        _cacheService = cacheService;
        _settingsService = settingsService;
        _sampleDataLoader = sampleDataLoader;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public async Task<CostData?> GetCostAsync(string provider, bool forceRefresh = false, CancellationToken ct = default)
    {
        var acquired = false;
        string? attemptedProvider = null;
        (DateTimeOffset AttemptedAt, CostData? Data)? previousAttempt = null;
        try
        {
            var normalized = ProviderConstants.ValidateAndNormalize(provider);
            if (!_settingsService.Settings.ShowCostTotals || _settingsService.Settings.DeveloperModeEnabled) return null;
            await _costGate.WaitAsync(ct);
            acquired = true;
            if (!_settingsService.Settings.ShowCostTotals || _settingsService.Settings.DeveloperModeEnabled) return null;
            var now = _timeProvider.GetUtcNow();
            lock (_costCache)
            {
                if (!forceRefresh && _costCache.TryGetValue(normalized, out var cached) &&
                    now - cached.AttemptedAt < TimeSpan.FromMinutes(15)) return cached.Data;

                previousAttempt = _costCache.TryGetValue(normalized, out var prior) ? prior : null;
                attemptedProvider = normalized;
                _costCache[normalized] = (now, null); // Back off on errors too.
            }
            var command = Helpers.CostCommandBuilder.Build(normalized, _userProfile(), _directoryExists);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, _settingsService.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds)));
            var result = await _wslService.ExecuteAsync(command, cts.Token);
            ct.ThrowIfCancellationRequested();
            if (!_settingsService.Settings.ShowCostTotals || _settingsService.Settings.DeveloperModeEnabled) return null;
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                _logger.LogDebug("Cost scan returned no data for {Provider} (exit {ExitCode})", normalized, result.ExitCode);
                return null;
            }
            var json = result.Output.Trim();
            var dto = json.StartsWith('[')
                ? JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListCostDataDto)?.FirstOrDefault()
                : JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CostDataDto);
            if (dto == null || !string.Equals(dto.Provider, normalized == "gemini" ? "antigravity" : normalized,
                StringComparison.OrdinalIgnoreCase)) return null;
            var data = dto.ToCostData(normalized, now.UtcDateTime);
            lock (_costCache) _costCache[normalized] = (now, data.HasData ? data : null);
            return data.HasData ? data : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cost scan unavailable for {Provider}", provider);
            return null;
        }
        finally
        {
            if (acquired)
            {
                // Obsolete scans are not failures: retain the last good cache or allow an immediate retry.
                if (attemptedProvider != null && (ct.IsCancellationRequested ||
                    !_settingsService.Settings.ShowCostTotals || _settingsService.Settings.DeveloperModeEnabled))
                {
                    lock (_costCache)
                    {
                        if (previousAttempt is { } previous) _costCache[attemptedProvider] = previous;
                        else _costCache.Remove(attemptedProvider);
                    }
                }
                _costGate.Release();
            }
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
        try
        {
            var normalizedProvider = ProviderConstants.ValidateAndNormalize(provider);
            var source = ProviderConstants.GetSource(normalizedProvider);
            var result = await _wslService.ExecuteAsync($"codexbar --provider {normalizedProvider} --format json --source {source} --status", ct);

            if (!result.Success)
            {
                _logger.LogWarning("codexbar command failed for {Provider}: {Error}", provider, result.Error);
                return _cacheService.Get(provider);
            }

            var data = ParseUsageJson(result.Output, provider);

            if (data != null)
            {
                _cacheService.Set(provider, data);
            }

            return data ?? _cacheService.Get(provider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get usage for {Provider}", provider);
            return _cacheService.Get(provider);
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
            try
            {
                var batch = await _wslService.ExecuteBatchAsync(commands, (index, result) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (index < 0 || index >= results.Length)
                        throw new InvalidDataException("WSL batch returned an invalid command index");
                    if (results[index] == null) Complete(index, InterpretProviderResult(providers[index], result));
                }, ct);
                ct.ThrowIfCancellationRequested();
                if (batch == null || batch.Count != commands.Length)
                    throw new InvalidDataException("WSL batch returned an incomplete result list");
                for (var i = 0; i < batch.Count; i++)
                    if (results[i] == null) Complete(i, InterpretProviderResult(providers[i], batch[i]));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogWarning(ex, "WSL batch unavailable; retrying unfinished providers individually");
                await Task.WhenAll(Enumerable.Range(0, providers.Count).Where(i => results[i] == null)
                    .Select(async i => Complete(i, await FetchProviderAsync(providers[i], commands[i], ct))));
            }
        }
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _isFirstFetch, 0);
        return results.Select(r => r!).ToArray();
    }

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
            var data = ParseUsageJsonArray(result.Output).FirstOrDefault();
            if (data == null) return ProviderError(provider, "No data available");
            _cacheService.Set(data.Provider, data);
            _logger.LogDebug("Fetched {Provider} successfully", provider);
            return data;
        }
        _logger.LogDebug("{Provider} failed: {Error}", provider, result.Error);
        return ProviderError(provider, string.IsNullOrWhiteSpace(result.Error)
            ? $"Command failed (exit code {result.ExitCode})" : result.Error.Trim());
    }

    private static UsageData ProviderError(string provider, string error) => new()
    {
        Provider = provider, Error = error, FetchedAt = DateTime.UtcNow
    };
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

    private UsageData? ParseUsageJson(string json, string provider)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.UsageDataDto);
            return dto?.ToUsageData() ?? new UsageData { Provider = provider };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse usage JSON for {Provider}", provider);
            return null;
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
