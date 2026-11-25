using System.Collections.Concurrent;
using System.Text.Json;
using CodexBarWin.Models;
using CodexBarWin.Helpers;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Service for caching usage data in memory and on disk.
/// </summary>
public class CacheService : ICacheService
{
    private readonly ILogger<CacheService> _logger;
    private readonly ISettingsService? _settingsService;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, UsageData> _cache = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    private bool DeveloperMode => _settingsService?.Settings?.DeveloperModeEnabled == true;

    private TimeSpan Expiry => _settingsService != null
        ? TimeSpan.FromMinutes(_settingsService.Settings.CacheExpiryMinutes)
        : TimeSpan.FromMinutes(5);

    public CacheService(ILogger<CacheService> logger, ISettingsService? settingsService = null,
        string? cachePath = null)
    {
        _logger = logger;
        _settingsService = settingsService;

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataPath, "CodexBarWin");
        _cachePath = cachePath ?? Path.Combine(appFolder, "cache.json");
    }

    public UsageData? Get(string provider)
    {
        if (DeveloperMode) return null;
        if (_cache.TryGetValue(provider, out var data))
        {
            if (IsFresh(data.FetchedAt))
            {
                return CacheRetention.PruneExpired(data, DateTime.UtcNow);
            }

            _logger.LogDebug("Cache expired for {Provider}", provider);
        }

        return null;
    }

    public void Set(string provider, UsageData data)
    {
        if (DeveloperMode) return;
        provider = ProviderConstants.ValidateAndNormalize(provider);
        if (!string.Equals(provider, data.Provider, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(data.Status, "offline", StringComparison.OrdinalIgnoreCase) || data.HasError || data.CommandNotFound || data.IsLoading ||
            data.UsageFetchOutcome is FetchOutcome.Failed or FetchOutcome.Unavailable) return;
        _cache[provider] = data.AuthRequired || data.AuthMessage != null || data.SignInExpired
            ? data with { AuthRequired = false, AuthMessage = null, SignInExpired = false } : data;
        _logger.LogDebug("Cached data for {Provider}", provider);
    }

    public void Clear()
    {
        _cache.Clear();
        _logger.LogDebug("Cache cleared");
    }

    public UsageData? GetLastKnown(string provider) => !DeveloperMode && ProviderConstants.IsValidProvider(provider) &&
        _cache.TryGetValue(ProviderConstants.ValidateAndNormalize(provider), out var data) &&
        CacheRetention.IsRetained(data, DateTime.UtcNow) ? CacheRetention.PruneExpired(data, DateTime.UtcNow) : null;

    public IReadOnlyDictionary<string, UsageData> GetAll()
    {
        if (DeveloperMode) return new Dictionary<string, UsageData>();
        return _cache
            .Where(kv => IsFresh(kv.Value.FetchedAt))
            .Select(kv => (kv.Key, Data: CacheRetention.PruneExpired(kv.Value, DateTime.UtcNow)))
            .Where(kv => kv.Data != null).ToDictionary(kv => kv.Key, kv => kv.Data!);
    }

    public async Task SaveAsync()
    {
        if (DeveloperMode) return;
        await _ioGate.WaitAsync();
        try
        {
            if (DeveloperMode) return;
            var now = DateTime.UtcNow;
            foreach (var (k, v) in _cache.ToArray())
            {
                var retained = CacheRetention.PruneExpired(v, now);
                if (retained == null) _cache.TryRemove(new KeyValuePair<string, UsageData>(k, v));
                else _cache.TryUpdate(k, retained, v);
            }
            var cacheData = new CacheData
            {
                Timestamp = now,
                Items = _cache.ToDictionary(kv => kv.Key, kv => kv.Value)
            };

            var json = System.Text.Json.JsonSerializer.Serialize(cacheData, AppJsonSerializerContext.Default.CacheData);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cachePath))!);
            var temporaryPath = _cachePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, _cachePath, overwrite: true);
            _logger.LogDebug("Cache saved to {Path}", _cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save cache");
        }
        finally { _ioGate.Release(); }
    }

    public async Task LoadAsync()
    {
        if (DeveloperMode) return;
        await _ioGate.WaitAsync();
        try
        {
            if (DeveloperMode) return;
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(_cachePath);
            // Parse structurally and deserialize entry by entry, so one corrupt sibling cannot discard valid providers.
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            var timestamp = FindProperty(document.RootElement, "timestamp");
            if (timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTime(out var savedAt)) return;
            var now = DateTime.UtcNow;
            var savedAtUtc = Utc(savedAt);
            if (savedAtUtc - now > CacheRetention.ClockSkewTolerance)
            {
                _logger.LogDebug("Cache file timestamp is in the future, ignoring");
                return;
            }
            if (savedAtUtc > now) savedAtUtc = now;

            var items = FindProperty(document.RootElement, "items");
            if (items.ValueKind != JsonValueKind.Object) return;
            foreach (var item in items.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object
                    || FindProperty(item.Value, "fetchedAt").ValueKind != JsonValueKind.String
                    || FindProperty(item.Value, "extraWindows").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Array)
                    || !ProviderConstants.IsValidProvider(item.Name)) continue;
                UsageData? data;
                try
                {
                    var single = "{\"items\":{" + JsonSerializer.Serialize(item.Name) + ":" + item.Value.GetRawText() + "}}";
                    var parsed = JsonSerializer.Deserialize(single, AppJsonSerializerContext.Default.CacheData);
                    if (parsed?.Items == null || !parsed.Items.TryGetValue(item.Name, out data) || data == null) continue;
                }
                catch (JsonException) { continue; }
                // An entry that omits extraWindows deserializes it as null; treat that as none rather than as corruption.
                data = data with { ExtraWindows = data.ExtraWindows ?? [], AuthRequired = false, AuthMessage = null };
                var fetchedAtUtc = Utc(data.FetchedAt);
                if (fetchedAtUtc - now > CacheRetention.ClockSkewTolerance) continue;
                if (fetchedAtUtc > now) data = data with { FetchedAt = now };
                if (data.LastSuccessfulFetch.HasValue)
                {
                    var lastSuccessUtc = Utc(data.LastSuccessfulFetch.Value);
                    if (lastSuccessUtc - now > CacheRetention.ClockSkewTolerance) continue;
                    if (lastSuccessUtc > now) data = data with { LastSuccessfulFetch = now };
                }
                data = CacheRetention.PruneExpired(data, now);
                if (data == null) continue;
                if (!string.Equals(item.Name, data.Provider, StringComparison.OrdinalIgnoreCase)
                    || data.ExtraWindows.Any(window => window == null || window.Window == null)
                    || string.Equals(data.Status, "offline", StringComparison.OrdinalIgnoreCase)
                    || data.HasError || data.CommandNotFound || data.IsLoading
                    || ((data.UsageFetchOutcome is FetchOutcome.Failed or FetchOutcome.Unavailable) && data.ServiceStatus == null)) continue;
                _cache[ProviderConstants.ValidateAndNormalize(item.Name)] = data;
            }

            _logger.LogDebug("Cache loaded from {Path}", _cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load cache");
        }
        finally { _ioGate.Release(); }
    }

    private bool IsFresh(DateTime timestamp)
    {
        var utc = DashboardValues.Utc(timestamp);
        var age = DateTime.UtcNow - utc;
        return age >= TimeSpan.Zero && age < Expiry;
    }

    private static DateTime Utc(DateTime timestamp) => DashboardValues.Utc(timestamp);

    private static JsonElement FindProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
}
