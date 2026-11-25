using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>Thread-safe, in-memory history with no filesystem or WinUI dependencies.</summary>
public sealed class UsageHistoryStore
{
    private readonly object _gate = new();
    private readonly List<UsageHistorySample> _samples = [];
    // Samples accepted since the persistence last took them, in the order they were added (see TakeUnsaved)
    private readonly List<UsageHistorySample> _unsaved = [];
    private long _revision;
    private long _generation;
    public long Revision { get { lock (_gate) return _revision; } }
    /// <summary>Changes whenever the whole history is replaced (<see cref="Restore"/>), so a writer knows appending is not enough.</summary>
    public long Generation { get { lock (_gate) return _generation; } }
    public int Count { get { lock (_gate) return _samples.Count; } }
    public IReadOnlyList<UsageHistorySample> Samples { get { lock (_gate) return _samples.ToArray(); } }

    /// <summary>Returns the samples added since the previous call (oldest first) and forgets them.</summary>
    public IReadOnlyList<UsageHistorySample> TakeUnsaved()
    {
        lock (_gate)
        {
            var taken = _unsaved.ToArray();
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
        sample = sample with { Provider = ProviderConstants.ValidateAndNormalize(sample.Provider),
            Timestamp = Utc(sample.Timestamp), ResetsAt = sample.ResetsAt.HasValue ? Utc(sample.ResetsAt.Value) : null };
        nowUtc = Utc(nowUtc);
        if (sample.Timestamp < nowUtc.AddDays(-14) || sample.Timestamp > nowUtc) return false;
        lock (_gate)
        {
            var last = _samples.LastOrDefault(s => s.Provider == sample.Provider && s.WindowKey == sample.WindowKey);
            if (last != null)
            {
                if (sample.Timestamp <= last.Timestamp) return false;
                if (sample.ResetsAt == last.ResetsAt && Math.Abs(sample.UsedPercent - last.UsedPercent) < UsageChangeTracker.ChangeThresholdPoints &&
                    sample.Timestamp - last.Timestamp < TimeSpan.FromMinutes(10)) return false;
            }
            _samples.Add(sample);
            _unsaved.Add(sample);
            _revision++;
            return true;
        }
    }

    public int Record(UsageData data, DateTime nowUtc)
    {
        if (data.IsLoading || data.HasError || data.UsageIsStale || data.UsageIsCached) return 0;
        var windows = new List<(string Key, UsageWindow Window)>();
        if (data.Session != null) windows.Add(("session", data.Session));
        if (data.Weekly != null) windows.Add(("weekly", data.Weekly));
        if (data.Tertiary != null) windows.Add(("tertiary", data.Tertiary));
        windows.AddRange(data.ExtraWindows.Select(e => (ExtraWindowKey(e), e.Window)));
        var count = 0;
        foreach (var (key, window) in windows)
            if (Add(new UsageHistorySample { Timestamp = nowUtc, Provider = data.Provider, WindowKey = key,
                UsedPercent = window.Percent, ResetsAt = window.ResetAt }, nowUtc)) count++;
        Prune(nowUtc);
        return count;
    }

    public IReadOnlyList<UsageHistorySample> Get(string provider, string windowKey)
    {
        lock (_gate) return _samples.Where(s => s.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
            s.WindowKey == windowKey).OrderBy(s => s.Timestamp).ToArray();
    }

    public void Restore(IEnumerable<UsageHistorySample> samples, DateTime nowUtc)
    {
        lock (_gate)
        {
            _samples.Clear();
            _revision++;
            foreach (var sample in samples.OfType<UsageHistorySample>().OrderBy(s => s.Timestamp)) Add(sample, nowUtc);
            _unsaved.Clear();
            _generation++;
        }
    }

    public int Prune(DateTime nowUtc)
    {
        lock (_gate)
        {
            var removed = _samples.RemoveAll(s => s.Timestamp < Utc(nowUtc).AddDays(-14) || s.Timestamp > Utc(nowUtc));
            if (removed > 0) _revision++;
            return removed;
        }
    }

    private static DateTime Utc(DateTime value) => Helpers.DashboardValues.Utc(value);
}
