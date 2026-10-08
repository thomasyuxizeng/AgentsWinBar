using System.Text.Json;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Atomic cost-cache.json beside the settings file. Results older than <see cref="MaxAge"/> are dropped on load,
/// and developer mode never reads or writes it.
/// </summary>
public sealed class CostCacheStore : ICostCacheStore
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

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
            foreach (var (key, data) in file?.Items ?? [])
            {
                if (data == null || !ProviderConstants.IsValidProvider(key) || !data.HasData) continue;
                var provider = ProviderConstants.ValidateAndNormalize(key);
                var fetchedAt = data.FetchedAt.Kind == DateTimeKind.Local ? data.FetchedAt.ToUniversalTime() : data.FetchedAt;
                if (now - fetchedAt > MaxAge || fetchedAt - now > TimeSpan.FromMinutes(5)) continue;
                items[provider] = data with { Provider = provider, Daily = data.Daily ?? [] };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "Cost cache could not be loaded"); }
        finally { if (acquired) _ioGate.Release(); }
        return items;
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, CostData> items, CancellationToken ct = default)
    {
        if (DeveloperMode) return;
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct); acquired = true;
            if (DeveloperMode) return;
            var file = new CostCacheFile { Items = items.Where(pair => pair.Value.HasData).ToDictionary(pair => pair.Key, pair => pair.Value) };
            var json = JsonSerializer.Serialize(file, AppJsonSerializerContext.Default.CostCacheFile);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var temporaryPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, ct);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Cost cache could not be saved"); }
        finally { if (acquired) _ioGate.Release(); }
    }
}
