using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

/// <summary>An immutable snapshot of usage values relevant to adaptive refresh.</summary>
public sealed class UsageFingerprint
{
    private readonly Dictionary<string, WindowSnapshot[]> _providers;
    private readonly bool _hasError;

    private UsageFingerprint(Dictionary<string, WindowSnapshot[]> providers, bool hasError)
    {
        _providers = providers;
        _hasError = hasError;
    }

    public static UsageFingerprint Create(IEnumerable<UsageData> providers)
    {
        var snapshots = new Dictionary<string, WindowSnapshot[]>(StringComparer.OrdinalIgnoreCase);
        var hasError = false;
        foreach (var provider in providers)
        {
            hasError |= provider.HasError || provider.IsLoading;
            var windows = new List<WindowSnapshot>();
            AddWindow("session", provider.Session);
            AddWindow("weekly", provider.Weekly);
            AddWindow("tertiary", provider.Tertiary);
            foreach (var extra in provider.ExtraWindows)
                AddWindow("extra:" + (string.IsNullOrEmpty(extra.Key) ? extra.Title : extra.Key), extra.Window);

            snapshots[provider.Provider] = windows.OrderBy(w => w.Key, StringComparer.Ordinal).ToArray();

            void AddWindow(string key, UsageWindow? window)
            {
                if (window != null)
                    windows.Add(new WindowSnapshot(key, window.Percent, window.ResetAt));
            }
        }
        return new UsageFingerprint(snapshots, hasError);
    }

    /// <summary>
    /// A rolling window with no usage (Gemini's 5-hour windows, an idle Codex session) reports "now plus the window length"
    /// as its reset time, so that time drifts by the refresh interval while nothing else moves. Drift up to this much is
    /// not a change; a real reset also moves the percentage.
    /// </summary>
    public static readonly TimeSpan ResetDriftTolerance = TimeSpan.FromHours(1);

    public bool HasChangedFrom(UsageFingerprint? previous, double tolerance = 0.05)
    {
        if (previous == null || _hasError || previous._hasError || _providers.Count != previous._providers.Count)
            return true;

        foreach (var (provider, windows) in _providers)
        {
            if (!previous._providers.TryGetValue(provider, out var oldWindows) || windows.Length != oldWindows.Length)
                return true;

            for (var i = 0; i < windows.Length; i++)
            {
                var current = windows[i];
                var old = oldWindows[i];
                if (current.Key != old.Key || ResetChanged(current.ResetAt, old.ResetAt) ||
                    !double.IsFinite(current.Percent) || !double.IsFinite(old.Percent) ||
                    Math.Abs(current.Percent - old.Percent) > tolerance + 1e-9)
                    return true;
            }
        }
        return false;
    }

    private static bool ResetChanged(DateTime? current, DateTime? old) =>
        current.HasValue != old.HasValue ||
        (current.HasValue && (current.Value - old!.Value).Duration() > ResetDriftTolerance);

    private sealed record WindowSnapshot(string Key, double Percent, DateTime? ResetAt);
}
