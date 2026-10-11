using System.Text.Json;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Persists detected early reset events to early-resets.json beside settings/cache.
/// Thread-safe, non-blocking in-memory queries with atomic disk writes and corrupt file recovery.
/// </summary>
public sealed class EarlyResetStore : IEarlyResetStore
{
    /// <summary>Maximum number of recorded events retained.</summary>
    public const int MaxEvents = 50;

    /// <summary>Retention period: events older than 7 days are discarded.</summary>
    public static readonly TimeSpan MaxRetentionAge = TimeSpan.FromDays(7);

    /// <summary>Active display window: events newer than 24 hours are shown on the dashboard.</summary>
    public static readonly TimeSpan ActiveDisplayWindow = TimeSpan.FromHours(24);

    private readonly ISettingsService? _settings;
    private readonly ILogger<EarlyResetStore> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _gate = new();
    private readonly List<EarlyResetEntry> _entries = [];
    private readonly Dictionary<string, EarlyResetWindowReading> _baselines = new(StringComparer.OrdinalIgnoreCase);
    private Task _pendingWrites = Task.CompletedTask;

    public string FilePath { get; }

    public EarlyResetStore(
        ISettingsService? settings,
        ILogger<EarlyResetStore> logger,
        TimeProvider? timeProvider = null,
        string? filePath = null)
    {
        _settings = settings;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;

        if (!string.IsNullOrEmpty(filePath))
        {
            FilePath = filePath;
        }
        else if (settings != null && !string.IsNullOrEmpty(settings.SettingsFilePath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(settings.SettingsFilePath));
            FilePath = Path.Combine(dir ?? string.Empty, "early-resets.json");
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            FilePath = Path.Combine(appData, "CodexBarWin", "early-resets.json");
        }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            if (!File.Exists(FilePath))
            {
                return;
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read early resets file");
                return;
            }

            EarlyResetStoreFile? file = null;
            try
            {
                file = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.EarlyResetStoreFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Early resets file is corrupt, renaming aside");
                RenameCorruptFile();
                return;
            }

            if (file == null || file.Version < 1)
            {
                _logger.LogWarning("Early resets file had invalid schema version, renaming aside");
                RenameCorruptFile();
                return;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            lock (_gate)
            {
                _entries.Clear();
                _baselines.Clear();
                foreach (var baseline in file.Baselines ?? [])
                {
                    if (baseline != null && IsFreshBaseline(baseline, now))
                        _baselines[BaselineKey(baseline)] = baseline;
                }
                foreach (var entry in file.Events ?? [])
                {
                    if (entry?.Event == null) continue;
                    if (!ProviderConstants.IsValidProvider(entry.Event.Provider)) continue;
                    if (string.IsNullOrWhiteSpace(entry.Event.WindowKey)) continue;

                    var key = DedupeKey(entry.Event);
                    if (_entries.Any(e => DedupeKey(e.Event) == key)) continue;

                    _entries.Add(entry);
                }
                PruneLocked(now);
            }

            _logger.LogDebug("Loaded early resets from {Path}", FilePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading early resets");
        }
        finally
        {
            if (acquired) _ioGate.Release();
        }
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        // Register before returning, and start ALL serialization and file work on the pool.
        // Chaining also guarantees FlushAsync observes Record/MarkNotified writes in order.
        lock (_gate)
        {
            var preceding = _pendingWrites;
            _pendingWrites = Task.Run(async () =>
            {
                try { await preceding.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* A cancelled caller must not poison later writes. */ }
                await SaveCoreAsync(ct).ConfigureAwait(false);
            });
            return _pendingWrites;
        }
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        lock (_gate) return _pendingWrites.WaitAsync(ct);
    }

    private async Task SaveCoreAsync(CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            EarlyResetStoreFile file;
            var now = _clock.GetUtcNow().UtcDateTime;
            lock (_gate)
            {
                PruneLocked(now);
                file = new EarlyResetStoreFile
                {
                    Version = 1,
                    Events = _entries.ToList(),
                    Baselines = _baselines.Values.ToList()
                };
            }

            var json = JsonSerializer.Serialize(file, AppJsonSerializerContext.Default.EarlyResetStoreFile);
            var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            File.Move(tempPath, FilePath, overwrite: true);

            _logger.LogDebug("Saved early resets to {Path}", FilePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save early resets to {Path}", FilePath);
        }
        finally
        {
            if (acquired) _ioGate.Release();
        }
    }

    /// <summary>
    /// Replays, failures and stale observations do not replace or invalidate fresh baselines.
    /// Metadata changes replace a baseline without an event. UsageData contains no account identity;
    /// switching accounts with the same plan/limit/window length is a known limitation.
    /// </summary>
    public IReadOnlyList<EarlyReset> Observe(UsageData data, DateTime nowUtc)
    {
        var now = DashboardValues.Utc(nowUtc);
        if (!ProviderConstants.IsValidProvider(data.Provider) || data.UsageIsCached || data.UsageIsStale ||
            data.UsageFetchOutcome != FetchOutcome.Success || data.HasError) return [];

        var observedAt = DashboardValues.Utc(data.DataUpdatedAt ?? data.CacheUpdatedAt ?? data.FetchedAt);
        if (observedAt > now + CacheRetention.ClockSkewTolerance || now - observedAt > EarlyResetDetector.MaxPreviousReadingAge)
            return [];

        var readings = EarlyResetDetector.Readings(data).Where(r => IsFreshBaseline(r, now)).ToArray();
        var events = new List<EarlyReset>();
        var changed = false;
        lock (_gate)
        {
            // A fresh ambiguous snapshot breaks identity continuity: neither duplicate is a baseline.
            foreach (var group in (data.ExtraWindows ?? []).GroupBy(UsageHistoryStore.ExtraWindowKey, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                var key = ProviderConstants.ValidateAndNormalize(data.Provider) + "|" + group.Key;
                if (_baselines.TryGetValue(key, out var held) && observedAt > DashboardValues.Utc(held.ReadingAtUtc))
                    changed |= _baselines.Remove(key);
            }
            foreach (var reading in readings)
            {
                var key = BaselineKey(reading);
                _baselines.TryGetValue(key, out var baseline);
                if (baseline != null && reading.ReadingAtUtc <= DashboardValues.Utc(baseline.ReadingAtUtc)) continue;
                var detected = EarlyResetDetector.Detect(baseline, reading, now);
                if (detected != null) events.Add(detected);
                _baselines[key] = reading;
                changed = true;
            }
        }
        if (changed) SaveAsync().SafeFireAndForget(ex => _logger.LogWarning(ex, "Failed to save fresh reset baselines"));
        return events;
    }

    private static string BaselineKey(EarlyResetWindowReading reading) =>
        ProviderConstants.ValidateAndNormalize(reading.Provider) + "|" + reading.WindowKey;

    private static bool IsFreshBaseline(EarlyResetWindowReading reading, DateTime now) =>
        ProviderConstants.IsValidProvider(reading.Provider) && !string.IsNullOrWhiteSpace(reading.WindowKey) &&
        !reading.IsCached && !reading.IsStale && reading.FetchOutcome == FetchOutcome.Success &&
        double.IsFinite(reading.UsedPercent) && reading.UsedPercent >= 0 &&
        DashboardValues.Utc(reading.ReadingAtUtc) <= now + CacheRetention.ClockSkewTolerance &&
        now - DashboardValues.Utc(reading.ReadingAtUtc) <= EarlyResetDetector.MaxPreviousReadingAge;

    public bool Record(EarlyReset @event)
    {
        if (@event == null || !ProviderConstants.IsValidProvider(@event.Provider) || string.IsNullOrWhiteSpace(@event.WindowKey))
        {
            return false;
        }

        var normalizedEvent = @event with
        {
            Provider = ProviderConstants.ValidateAndNormalize(@event.Provider),
            PreviousResetAtUtc = @event.PreviousResetAtUtc.HasValue ? DashboardValues.Utc(@event.PreviousResetAtUtc.Value) : null,
            NewResetAtUtc = @event.NewResetAtUtc.HasValue ? DashboardValues.Utc(@event.NewResetAtUtc.Value) : null,
            DetectedAtUtc = DashboardValues.Utc(@event.DetectedAtUtc)
        };

        var key = DedupeKey(normalizedEvent);
        var now = _clock.GetUtcNow().UtcDateTime;

        lock (_gate)
        {
            if (_entries.Any(e => DedupeKey(e.Event) == key))
            {
                return false;
            }

            _entries.Add(new EarlyResetEntry
            {
                Event = normalizedEvent,
                Notified = false,
                NotifiedAtUtc = null
            });

            PruneLocked(now);
        }

        SaveAsync().SafeFireAndForget(ex => _logger.LogWarning(ex, "Failed to save recorded early reset"));
        return true;
    }

    public bool MarkNotified(EarlyReset @event)
    {
        if (@event == null) return false;
        var key = DedupeKey(@event);
        var now = _clock.GetUtcNow().UtcDateTime;
        var changed = false;

        lock (_gate)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (DedupeKey(_entries[i].Event) == key)
                {
                    if (!_entries[i].Notified)
                    {
                        _entries[i] = _entries[i] with
                        {
                            Notified = true,
                            NotifiedAtUtc = now
                        };
                        changed = true;
                    }
                    break;
                }
            }
        }

        if (changed)
        {
            SaveAsync().SafeFireAndForget(ex => _logger.LogWarning(ex, "Failed to save marked early reset"));
        }

        return changed;
    }

    public bool IsNotified(EarlyReset @event)
    {
        if (@event == null) return false;
        var key = DedupeKey(@event);
        lock (_gate)
        {
            return _entries.Any(e => DedupeKey(e.Event) == key && e.Notified);
        }
    }

    public IReadOnlyList<EarlyReset> GetActive(DateTime nowUtc)
    {
        var now = DashboardValues.Utc(nowUtc);
        var cutoff = now - ActiveDisplayWindow;
        lock (_gate)
        {
            return _entries
                .Select(e => e.Event)
                .Where(e => DashboardValues.Utc(e.DetectedAtUtc) >= cutoff && DashboardValues.Utc(e.DetectedAtUtc) <= now.AddHours(1))
                .OrderByDescending(e => e.DetectedAtUtc)
                .ToArray();
        }
    }

    public IReadOnlyList<EarlyReset> GetRecent(string provider, string windowKey, DateTime sinceUtc)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(windowKey))
        {
            return [];
        }

        var normProvider = ProviderConstants.IsValidProvider(provider)
            ? ProviderConstants.ValidateAndNormalize(provider)
            : provider.Trim().ToLowerInvariant();
        var normWindow = windowKey.Trim().ToLowerInvariant();
        var since = DashboardValues.Utc(sinceUtc);

        lock (_gate)
        {
            return _entries
                .Select(e => e.Event)
                .Where(e => string.Equals(e.Provider, normProvider, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(e.WindowKey, normWindow, StringComparison.OrdinalIgnoreCase) &&
                            DashboardValues.Utc(e.DetectedAtUtc) >= since)
                .OrderByDescending(e => e.DetectedAtUtc)
                .ToArray();
        }
    }

    private void PruneLocked(DateTime nowUtc)
    {
        var now = DashboardValues.Utc(nowUtc);
        var minTime = now - MaxRetentionAge;

        // Drop entries older than 7 days or unreasonably far in the future
        _entries.RemoveAll(e => DashboardValues.Utc(e.Event.DetectedAtUtc) < minTime ||
                                DashboardValues.Utc(e.Event.DetectedAtUtc) > now.AddHours(2));

        // Keep at most 50 events, newest first
        if (_entries.Count > MaxEvents)
        {
            _entries.Sort((a, b) => b.Event.DetectedAtUtc.CompareTo(a.Event.DetectedAtUtc));
            _entries.RemoveRange(MaxEvents, _entries.Count - MaxEvents);
        }
    }

    private void RenameCorruptFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var corruptPath = $"{FilePath}.corrupt.{_clock.GetUtcNow():yyyyMMddHHmmss}";
                File.Move(FilePath, corruptPath, overwrite: true);
                _logger.LogInformation("Renamed corrupt early resets file to {CorruptPath}", corruptPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rename corrupt early resets file");
        }
    }

    private static string DedupeKey(EarlyReset e) =>
        DedupeKey(e.Provider, e.WindowKey, e.PreviousResetAtUtc);

    private static string DedupeKey(string provider, string windowKey, DateTime? previousResetAtUtc)
    {
        var normProvider = ProviderConstants.IsValidProvider(provider)
            ? ProviderConstants.ValidateAndNormalize(provider)
            : (provider ?? string.Empty).Trim().ToLowerInvariant();
        var normWindow = (windowKey ?? string.Empty).Trim().ToLowerInvariant();
        var ticks = previousResetAtUtc.HasValue
            ? DashboardValues.Utc(previousResetAtUtc.Value).Ticks.ToString()
            : "none";
        return $"{normProvider}|{normWindow}|{ticks}";
    }
}
