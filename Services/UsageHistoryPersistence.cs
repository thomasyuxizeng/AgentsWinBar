using System.Text.Json;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>Thin, atomic history persistence beside the settings file.</summary>
public sealed class UsageHistoryPersistence
{
    private readonly UsageHistoryStore _store;
    private readonly ILogger<UsageHistoryPersistence> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private DateTimeOffset _lastSavedAt = DateTimeOffset.MinValue;
    private long _savedRevision = -1;
    public string FilePath { get; }

    public UsageHistoryPersistence(UsageHistoryStore store, ISettingsService settings,
        ILogger<UsageHistoryPersistence> logger, TimeProvider? timeProvider = null)
    {
        _store = store;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        FilePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.SettingsFilePath))!, "usage-history.json");
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct); acquired = true;
            if (!File.Exists(FilePath)) return;
            var json = await File.ReadAllTextAsync(FilePath, ct);
            var samples = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageHistorySample);
            _store.Restore(samples ?? [], _clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Usage history could not be loaded"); }
        finally { if (acquired) _ioGate.Release(); }
    }

    public async Task SaveAsync(bool force = false, CancellationToken ct = default)
    {
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct); acquired = true;
            var now = _clock.GetUtcNow();
            _store.Prune(now.UtcDateTime);
            var revision = _store.Revision;
            if (revision == _savedRevision || (!force && now - _lastSavedAt < TimeSpan.FromMinutes(1))) return;
            var json = JsonSerializer.Serialize(_store.Samples, AppJsonSerializerContext.Default.IReadOnlyListUsageHistorySample);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporaryPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, ct);
            File.Move(temporaryPath, FilePath, overwrite: true);
            _savedRevision = revision;
            _lastSavedAt = now;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Usage history could not be saved"); }
        finally { if (acquired) _ioGate.Release(); }
    }
}
