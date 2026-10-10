using System.Text.Json;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Atomic cost-cache.json beside the settings file. Last-known values are retained for up to seven days,
/// and developer mode never reads or writes it.
/// </summary>
public sealed class CostCacheStore : ICostCacheStore
{
    private readonly ISettingsService _settings;
    private readonly ILogger<CostCacheStore> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    public string FilePath { get; }

    public CostCacheStore(ISettingsService settings, ILogger<CostCacheStore> logger,
        TimeProvider? timeProvider = null, string? filePath = null)
    {
        _settings = settings;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        FilePath = filePath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.SettingsFilePath))!, "cost-cache.json");
    }

    private bool DeveloperMode => _settings.Settings.DeveloperModeEnabled;

    public async Task<IReadOnlyDictionary<string, CostData>> LoadAsync(CancellationToken ct = default)
    {
        var items = new Dictionary<string, CostData>(StringComparer.Ordinal);
        if (DeveloperMode) return items;
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct); acquired = true;
            if (!File.Exists(FilePath)) return items;
            var json = await File.ReadAllTextAsync(FilePath, ct);
            var file = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CostCacheFile);
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var (key, rawData) in file?.Items ?? [])
            {
                if (rawData == null || !ProviderConstants.IsValidProvider(key) || !rawData.HasData) continue;
                var provider = ProviderConstants.ValidateAndNormalize(key);
                var currentData = rawData;
                var fetchedAt = currentData.FetchedAt.ToUniversalTime();
                if (fetchedAt - now > CacheRetention.ClockSkewTolerance || !string.Equals(currentData.Provider, provider, StringComparison.OrdinalIgnoreCase)) continue;
                if (fetchedAt > now) currentData = currentData with { FetchedAt = now };
                if (currentData.LastSuccessfulFetch.HasValue)
                {
                    var lastSuccessUtc = currentData.LastSuccessfulFetch.Value.ToUniversalTime();
                    if (lastSuccessUtc - now > CacheRetention.ClockSkewTolerance) continue;
                    if (lastSuccessUtc > now) currentData = currentData with { LastSuccessfulFetch = now };
                }
                var successAt = (currentData.LastSuccessfulFetch ?? currentData.FetchedAt).ToUniversalTime();
                if (now - successAt > CacheRetention.MaxAge) continue;
                items[provider] = currentData with { Provider = provider, Daily = currentData.Daily ?? [] };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "Cost cache could not be loaded"); }
        finally { if (acquired) _ioGate.Release(); }
        return items;
    }

    public async Task<bool> SaveAsync(IReadOnlyDictionary<string, CostData> items, CancellationToken ct = default)
    {
        if (DeveloperMode) return false;
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct); acquired = true;
            if (DeveloperMode) return false;
            var now = _clock.GetUtcNow().UtcDateTime;
            var file = new CostCacheFile
            {
                Items = items.Where(pair => pair.Value.HasData &&
                    now - (pair.Value.LastSuccessfulFetch ?? pair.Value.FetchedAt).ToUniversalTime() <= CacheRetention.MaxAge)
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
            };
            var json = JsonSerializer.Serialize(file, AppJsonSerializerContext.Default.CostCacheFile);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var temporaryPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, ct);
            File.Move(temporaryPath, FilePath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Cost cache could not be saved"); }
        finally { if (acquired) _ioGate.Release(); }
        return false;
    }
}
