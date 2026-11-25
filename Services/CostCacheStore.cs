using System.Text.Json;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Atomic cost-cache.json beside the settings file. Last-known cost values are retained indefinitely,
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
            CostCacheFile? file;
            await using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (stream.Length == 0) return items;
                file = await JsonSerializer.DeserializeAsync(stream, AppJsonSerializerContext.Default.CostCacheFile, ct);
            }
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
            var jsonFile = new CostCacheFile
            {
                Items = items.Where(pair => pair.Value.HasData)
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var temporaryPath = FilePath + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, jsonFile, AppJsonSerializerContext.Default.CostCacheFile, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporaryPath, FilePath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Cost cache could not be saved"); }
        finally { if (acquired) _ioGate.Release(); }
        return false;
    }
}
