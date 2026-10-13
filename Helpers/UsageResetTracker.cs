using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Helpers;

/// <summary>Session-only reset signals. Never uses disk/cache baselines or persisted early-reset events.</summary>
public sealed class UsageResetTracker
{
    private readonly Dictionary<string, EarlyResetWindowReading> _baselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime At, double FromPercent)> _detected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastObservation = new(StringComparer.OrdinalIgnoreCase);

    public void Clear()
    {
        _baselines.Clear();
        _detected.Clear();
        _lastObservation.Clear();
    }

    public void RetainProviders(IEnumerable<string> providers)
    {
        var enabled = providers.Where(ProviderConstants.IsValidProvider)
            .Select(ProviderConstants.ValidateAndNormalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _lastObservation.Keys.Where(p => !enabled.Contains(p)).ToArray())
            _lastObservation.Remove(provider);
        foreach (var key in _baselines.Keys.Concat(_detected.Keys).Distinct().ToArray())
            if (!enabled.Contains(key.Split('|', 2)[0]))
            {
                _baselines.Remove(key);
                _detected.Remove(key);
            }
    }

    public UsageData Annotate(UsageData data, DateTime nowUtc)
    {
        if (!ProviderConstants.IsValidProvider(data.Provider)) return data;
        var provider = ProviderConstants.ValidateAndNormalize(data.Provider);
        var readings = EarlyResetDetector.Readings(data).ToDictionary(r => r.WindowKey, StringComparer.OrdinalIgnoreCase);
        var observedAt = DashboardValues.Utc(data.DataUpdatedAt ?? data.CacheUpdatedAt ?? data.FetchedAt);
        var now = DashboardValues.Utc(nowUtc);
        var fresh = !data.UsageIsCached && !data.UsageIsStale && data.UsageFetchOutcome == FetchOutcome.Success &&
            DataAge.OldReadingUtc(data.FetchedAt, data.DataUpdatedAt ?? data.CacheUpdatedAt) == null &&
            observedAt <= now + CacheRetention.ClockSkewTolerance && now - observedAt <= EarlyResetDetector.MaxPreviousReadingAge &&
            (!_lastObservation.TryGetValue(provider, out var lastAt) || observedAt > lastAt);
        if (fresh)
        {
            _lastObservation[provider] = observedAt;
            // Missing or ambiguous windows start over if they later reappear.
            foreach (var key in _baselines.Keys.Concat(_detected.Keys).Distinct().ToArray())
                if (key.StartsWith(provider + "|", StringComparison.OrdinalIgnoreCase) &&
                    !readings.ContainsKey(key[(provider.Length + 1)..]))
                {
                    _baselines.Remove(key);
                    _detected.Remove(key);
                }
            foreach (var reading in readings.Values)
            {
                var key = provider + "|" + reading.WindowKey;
                var at = DashboardValues.Utc(reading.ReadingAtUtc);
                if (!double.IsFinite(reading.UsedPercent) || reading.UsedPercent < 0 ||
                    at > now + CacheRetention.ClockSkewTolerance || now - at > EarlyResetDetector.MaxPreviousReadingAge)
                    continue;
                _baselines.TryGetValue(key, out var baseline);
                if (baseline != null && at <= DashboardValues.Utc(baseline.ReadingAtUtc)) continue;
                if (Detect(baseline, reading, now)) _detected[key] = (now, baseline!.UsedPercent);
                _baselines[key] = reading;
            }
        }

        // Each bar owns a provider + window-kind key; never propagate a provider aggregate to siblings.
        UsageWindow? Mark(string windowKey, UsageWindow? window) => window == null ? null : window with
        {
            ResetDetectedAtUtc = _detected.TryGetValue(provider + "|" + windowKey, out var hit) ? hit.At : null,
            ResetFromUsedPercent = _detected.TryGetValue(provider + "|" + windowKey, out var from) ? from.FromPercent : null
        };
        return data with
        {
            Session = Mark("session", data.Session), Weekly = Mark("weekly", data.Weekly),
            Tertiary = Mark("tertiary", data.Tertiary),
            ExtraWindows = data.ExtraWindows.Select(e => e with { Window = Mark(UsageHistoryStore.ExtraWindowKey(e), e.Window)! }).ToArray()
        };
    }

    public static bool Detect(EarlyResetWindowReading? previous, EarlyResetWindowReading? current, DateTime nowUtc)
    {
        if (!EarlyResetDetector.AreComparable(previous, current, nowUtc)) return false;
        if (EarlyResetDetector.Detect(previous, current, nowUtc) != null) return true;
        var oldReset = previous!.ResetAt is { } oldAt ? DashboardValues.Utc(oldAt) : (DateTime?)null;
        var newReset = current!.ResetAt is { } newAt ? DashboardValues.Utc(newAt) : (DateTime?)null;
        var readingAt = DashboardValues.Utc(current.ReadingAtUtc);
        // A scheduled rollover can occur even at low usage. A percentage drop alone needs a due window.
        var shifted = oldReset.HasValue && newReset > oldReset;
        var due = oldReset.HasValue && oldReset <= readingAt;
        var nearDue = oldReset.HasValue && oldReset <= readingAt + EarlyResetDetector.GraceBeforeReset;
        var dropped = previous.UsedPercent - current.UsedPercent >= EarlyResetDetector.EarlyResetMinDropPoints;
        return (due && shifted) || (nearDue && dropped);
    }
}
