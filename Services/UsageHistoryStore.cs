using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

/// <summary>Thread-safe, in-memory history with no filesystem or WinUI dependencies.</summary>
public sealed class UsageHistoryStore
{
    private const long TenMinutesTicks = TimeSpan.TicksPerMinute * 10;
    private const int MaxWindowKeyPoolSize = 256;

    private readonly record struct StoredSample(
        long TimestampTicks,
        long ResetsAtTicks,
        string Provider,
        string WindowKey,
        double UsedPercent)
    {
        public UsageHistorySample ToModel() => new()
        {
            Timestamp = new DateTime(TimestampTicks, DateTimeKind.Utc),
            Provider = Provider,
            WindowKey = WindowKey,
            UsedPercent = UsedPercent,
            ResetsAt = ResetsAtTicks < 0 ? null : new DateTime(ResetsAtTicks, DateTimeKind.Utc)
        };
    }

    private readonly object _gate = new();
    private readonly List<StoredSample> _samples = [];
    // Samples accepted since the persistence last took them, in the order they were added (see TakeUnsaved)
    private readonly List<StoredSample> _unsaved = [];
    private readonly Dictionary<(string Provider, string WindowKey), StoredSample> _lastByKey = new();
    private readonly Dictionary<string, string> _windowKeyPool = new(StringComparer.Ordinal);
    private long _revision;
    private long _generation;
    public long Revision { get { lock (_gate) return _revision; } }
    /// <summary>Changes whenever the whole history is replaced (<see cref="Restore"/>), so a writer knows appending is not enough.</summary>
    public long Generation { get { lock (_gate) return _generation; } }
    public int Count { get { lock (_gate) return _samples.Count; } }
    public IReadOnlyList<UsageHistorySample> Samples
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count == 0) return [];
                var result = new UsageHistorySample[_samples.Count];
                for (var i = 0; i < _samples.Count; i++)
                {
                    result[i] = _samples[i].ToModel();
                }
                return result;
            }
        }
    }

    /// <summary>Returns the samples added since the previous call (oldest first) and forgets them.</summary>
    public IReadOnlyList<UsageHistorySample> TakeUnsaved()
    {
        lock (_gate)
        {
            if (_unsaved.Count == 0) return [];
            var taken = new UsageHistorySample[_unsaved.Count];
            for (var i = 0; i < _unsaved.Count; i++)
            {
                taken[i] = _unsaved[i].ToModel();
            }
            _unsaved.Clear();
            return taken;
        }
    }

    public static string ExtraWindowKey(ExtraUsageWindow extra) =>
        "extra:" + (string.IsNullOrWhiteSpace(extra.Key) ? (extra.Title ?? string.Empty).Trim().ToLowerInvariant() : extra.Key);

    public bool Add(UsageHistorySample sample, DateTime nowUtc)
    {
        if (!ProviderConstants.IsValidProvider(sample.Provider) || string.IsNullOrWhiteSpace(sample.WindowKey) ||
            !double.IsFinite(sample.UsedPercent) || sample.UsedPercent < 0) return false;
        var provider = InternProvider(ProviderConstants.ValidateAndNormalize(sample.Provider));
        var timestampUtc = Utc(sample.Timestamp);
        DateTime? resetsAtUtc = sample.ResetsAt.HasValue ? Utc(sample.ResetsAt.Value) : null;
        var utcNow = Utc(nowUtc);
        lock (_gate)
        {
            return TryAddLocked(provider, sample.WindowKey, timestampUtc, sample.UsedPercent, resetsAtUtc, utcNow);
        }
    }

    public int Record(UsageData data, DateTime nowUtc, bool prune = true)
    {
        if (data.IsLoading || data.HasError || data.UsageIsStale || data.UsageIsCached) return 0;
        if (!ProviderConstants.IsValidProvider(data.Provider)) return 0;
        var provider = InternProvider(ProviderConstants.ValidateAndNormalize(data.Provider));
        var utcNow = Utc(nowUtc);

        lock (_gate)
        {
            var count = 0;
            if (data.Session != null && TryAddWindowLocked(provider, "session", data.Session, utcNow)) count++;
            if (data.Weekly != null && TryAddWindowLocked(provider, "weekly", data.Weekly, utcNow)) count++;
            if (data.Tertiary != null && TryAddWindowLocked(provider, "tertiary", data.Tertiary, utcNow)) count++;
            foreach (var extra in data.ExtraWindows)
            {
                if (TryAddWindowLocked(provider, ExtraWindowKey(extra), extra.Window, utcNow)) count++;
            }
            if (prune)
            {
                PruneLocked(utcNow);
            }
            return count;
        }
    }

    public IReadOnlyList<UsageHistorySample> Get(string provider, string windowKey)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(windowKey)) return [];
        lock (_gate)
        {
            var matches = new List<UsageHistorySample>();
            var inOrder = true;
            var prevTicks = long.MinValue;
            for (var i = 0; i < _samples.Count; i++)
            {
                var s = _samples[i];
                if (s.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.WindowKey, windowKey, StringComparison.Ordinal))
                {
                    if (s.TimestampTicks < prevTicks) inOrder = false;
                    prevTicks = s.TimestampTicks;
                    matches.Add(s.ToModel());
                }
            }
            if (!inOrder && matches.Count > 1)
            {
                matches.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
            }
            return matches.ToArray();
        }
    }

    public void Restore(IEnumerable<UsageHistorySample> samples, DateTime nowUtc)
    {
        var utcNow = Utc(nowUtc);
        lock (_gate)
        {
            _samples.Clear();
            _lastByKey.Clear();
            _revision++;
            foreach (var sample in samples.OfType<UsageHistorySample>().OrderBy(s => s.Timestamp))
            {
                if (!ProviderConstants.IsValidProvider(sample.Provider) || string.IsNullOrWhiteSpace(sample.WindowKey) ||
                    !double.IsFinite(sample.UsedPercent) || sample.UsedPercent < 0) continue;
                var provider = InternProvider(ProviderConstants.ValidateAndNormalize(sample.Provider));
                var timestampUtc = Utc(sample.Timestamp);
                DateTime? resetsAtUtc = sample.ResetsAt.HasValue ? Utc(sample.ResetsAt.Value) : null;
                TryAddLocked(provider, sample.WindowKey, timestampUtc, sample.UsedPercent, resetsAtUtc, utcNow);
            }
            _unsaved.Clear();
            _generation++;
        }
    }

    public int Prune(DateTime nowUtc)
    {
        var utcNow = Utc(nowUtc);
        lock (_gate)
        {
            return PruneLocked(utcNow);
        }
    }

    private bool TryAddWindowLocked(string provider, string windowKey, UsageWindow window, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(windowKey) || !double.IsFinite(window.Percent) || window.Percent < 0) return false;
        DateTime? resetsAtUtc = window.ResetAt.HasValue ? Utc(window.ResetAt.Value) : null;
        return TryAddLocked(provider, windowKey, utcNow, window.Percent, resetsAtUtc, utcNow);
    }

    private bool TryAddLocked(string normalizedProvider, string windowKey, DateTime timestampUtc, double usedPercent,
        DateTime? resetsAtUtc, DateTime utcNow)
    {
        var tsTicks = timestampUtc.Ticks;
        var minTicks = utcNow.AddDays(-14).Ticks;
        var maxTicks = utcNow.Ticks;
        if (tsTicks < minTicks || tsTicks > maxTicks) return false;

        var pooledKey = PoolWindowKeyLocked(windowKey);
        var resetsAtTicks = resetsAtUtc.HasValue ? resetsAtUtc.Value.Ticks : -1L;
        var lookupKey = (normalizedProvider, pooledKey);

        if (_lastByKey.TryGetValue(lookupKey, out var last))
        {
            if (tsTicks <= last.TimestampTicks) return false;
            // Reset times a few seconds apart are the same window (some providers report sub-second jitter)
            if (SameResetTicks(resetsAtTicks, last.ResetsAtTicks) &&
                Math.Abs(usedPercent - last.UsedPercent) < UsageChangeTracker.ChangeThresholdPoints &&
                tsTicks - last.TimestampTicks < TenMinutesTicks)
            {
                return false;
            }
        }

        var stored = new StoredSample(tsTicks, resetsAtTicks, normalizedProvider, pooledKey, usedPercent);
        _samples.Add(stored);
        _unsaved.Add(stored);
        _lastByKey[lookupKey] = stored;
        _revision++;
        return true;
    }

    private int PruneLocked(DateTime utcNow)
    {
        var minTicks = utcNow.AddDays(-14).Ticks;
        var maxTicks = utcNow.Ticks;
        var removed = _samples.RemoveAll(s => s.TimestampTicks < minTicks || s.TimestampTicks > maxTicks);
        if (removed > 0)
        {
            _revision++;
            if (_samples.Count == 0)
            {
                _lastByKey.Clear();
            }
            else
            {
                var needRebuild = false;
                foreach (var kv in _lastByKey)
                {
                    if (kv.Value.TimestampTicks < minTicks || kv.Value.TimestampTicks > maxTicks)
                    {
                        needRebuild = true;
                        break;
                    }
                }
                if (needRebuild)
                {
                    _lastByKey.Clear();
                    foreach (var s in _samples)
                    {
                        var key = (s.Provider, s.WindowKey);
                        if (!_lastByKey.TryGetValue(key, out var existing) || s.TimestampTicks > existing.TimestampTicks)
                        {
                            _lastByKey[key] = s;
                        }
                    }
                }
            }
        }
        return removed;
    }

    private string PoolWindowKeyLocked(string windowKey)
    {
        if (windowKey is "session" or "weekly" or "tertiary") return windowKey;
        if (_windowKeyPool.TryGetValue(windowKey, out var pooled)) return pooled;
        if (_windowKeyPool.Count < MaxWindowKeyPoolSize)
        {
            _windowKeyPool[windowKey] = windowKey;
        }
        return windowKey;
    }

    private static string InternProvider(string normalized) => normalized switch
    {
        "claude" => "claude",
        "codex" => "codex",
        "gemini" => "gemini",
        _ => normalized
    };

    /// <summary>Both missing, or both present and within <see cref="Helpers.BurndownBuilder.ResetTolerance"/>.</summary>
    private static bool SameResetTicks(long a, long b)
    {
        if (a < 0 || b < 0) return a < 0 && b < 0;
        return Math.Abs(a - b) <= Helpers.BurndownBuilder.ResetTolerance.Ticks;
    }

    private static DateTime Utc(DateTime value) => Helpers.DashboardValues.Utc(value);
}
