using System.Text.Json;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Services;

/// <summary>
/// A notification the user should see.
/// </summary>
public sealed record UsageAlert(string Key, string Title, string Message);

/// <summary>Persisted root structure for alerts-sent.json.</summary>
public sealed record AlertsSentFile
{
    public int Version { get; init; } = 1;
    public List<AlertSentEntry> Entries { get; init; } = [];
}

/// <summary>One reported situation and when it was first reported.</summary>
public sealed record AlertSentEntry
{
    public string Key { get; init; } = string.Empty;
    public DateTime ReportedAtUtc { get; init; }
}

/// <summary>
/// Decides when usage is worth a heads-up, and makes sure each situation is reported only once per
/// usage window. Without a file path the memory lives in the process only and never touches the disk;
/// with one, the reported keys survive app restarts in alerts-sent.json. No UI, so it can be unit tested.
/// </summary>
public class UsageAlertEvaluator
{
    /// <summary>Usage at or above this is "nearly out".</summary>
    public const double NearLimitPercent = UsageThresholds.Critical;

    /// <summary>How many points usage must be ahead of the time elapsed to count as "far ahead of pace".</summary>
    public const double AheadOfPacePoints = 25;

    /// <summary>Don't warn about running out when the reset is this close anyway.</summary>
    public const double MinMinutesBeforeReset = 30;

    /// <summary>File name of the persisted memory, beside settings.json.</summary>
    public const string FileName = "alerts-sent.json";

    /// <summary>Maximum number of reported situations kept.</summary>
    public const int MaxKeys = 500;

    /// <summary>Files larger than this are ignored (the real file is a few KB).</summary>
    public const long MaxFileBytes = 256 * 1024;

    /// <summary>A key whose window bucket lies further in the past than this is dropped.</summary>
    public static readonly TimeSpan ExpiredWindowAge = TimeSpan.FromDays(1);

    /// <summary>A key without a reset time is dropped this long after it was first reported.</summary>
    public static readonly TimeSpan NoResetRetention = TimeSpan.FromDays(7);

    private const int MaxKeyLength = 512;
    private static readonly long BucketTicks = TimeSpan.FromMinutes(30).Ticks;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _alreadyAlerted = new(StringComparer.Ordinal);
    private readonly string? _filePath;
    private readonly ILogger _logger;
    private bool _loaded;
    private bool _dirty;

    /// <summary>In-memory only: nothing is read from or written to the disk.</summary>
    public UsageAlertEvaluator()
    {
        _logger = NullLogger.Instance;
    }

    /// <summary>Remembers reported situations in <paramref name="filePath"/> across restarts.</summary>
    public UsageAlertEvaluator(string filePath, ILogger<UsageAlertEvaluator> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>
    /// Persistent evaluator whose file sits in the folder of settings.json, falling back to the default
    /// data folder when that path is unknown (same rule as <see cref="EarlyResetStore"/>).
    /// </summary>
    public static UsageAlertEvaluator CreatePersistent(string? settingsFilePath, ILogger<UsageAlertEvaluator> logger)
    {
        string path;
        if (!string.IsNullOrEmpty(settingsFilePath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(settingsFilePath));
            path = Path.Combine(dir ?? string.Empty, FileName);
        }
        else
        {
            path = AppPaths.DataFile(FileName);
        }

        return new UsageAlertEvaluator(path, logger);
    }

    /// <summary>
    /// Returns the alerts that are new since the last call (and, when persistent, since the last run).
    /// </summary>
    public IReadOnlyList<UsageAlert> Evaluate(IEnumerable<UsageData> providers, DateTime nowUtc)
    {
        lock (_gate)
        {
            EnsureLoaded(nowUtc);
            var alerts = EvaluateLocked(providers, nowUtc);
            if (_filePath != null && _dirty)
            {
                Save(nowUtc);
            }

            return alerts;
        }
    }

    private List<UsageAlert> EvaluateLocked(IEnumerable<UsageData> providers, DateTime nowUtc)
    {
        var alerts = new List<UsageAlert>();

        foreach (var data in providers)
        {
            if (data.AuthRequired || data.HasError || data.IsLoading || data.UsageIsStale)
            {
                continue;
            }

            var name = Capitalize(data.Provider);
            Check(alerts, data, $"{name} {data.SessionLabel.ToLowerInvariant()}", "session", data.Session, nowUtc);
            Check(alerts, data, $"{name} {data.WeeklyLabel.ToLowerInvariant()}", "weekly", data.Weekly, nowUtc);

            // Extra windows (Claude and GPT quota, Fable only, ...) get the same alerts
            foreach (var extra in data.ExtraWindows)
            {
                Check(alerts, data, $"{name} · {extra.Title}", $"extra:{extra.Title}", extra.Window, nowUtc);
            }
        }

        return alerts;
    }

    /// <param name="subject">What the alert is about, e.g. "Claude session" or "Gemini · Claude and GPT 5-hour".</param>
    private void Check(List<UsageAlert> alerts, UsageData data, string subject, string slot, UsageWindow? window, DateTime nowUtc)
    {
        if (window == null || data.UsageIsStale)
        {
            return;
        }

        if (window.ResetAt is { } resetAt && DashboardValues.Utc(resetAt) <= DashboardValues.Utc(nowUtc))
        {
            return;
        }

        var pace = PaceCalculator.Project(window.Percent, window.WindowMinutes, window.ResetAt, nowUtc);
        var elapsedPercent = ResetTimeFormatter.ElapsedPercent(window.ResetAt, window.WindowMinutes, nowUtc);
        var title = $"{subject} usage";

        // Highest-priority situation only, so one window never produces several toasts at once
        string? level = null;
        string? message = null;

        if (window.Percent >= 100)
        {
            level = "limit";
            title = $"{subject} limit reached";
            message = pace.Kind == PaceKind.LimitReached ? PaceCalculator.Summarize(pace) : "You've used all of it.";
        }
        else if (window.Percent >= NearLimitPercent)
        {
            level = "near";
            message = $"{window.Percent:F0}% used" +
                      (pace.MinutesUntilReset > 0 ? $", resets in {PaceCalculator.FormatDuration(pace.MinutesUntilReset)}" : string.Empty);
        }
        else if (pace.Kind == PaceKind.WillRunOut
                 && pace.MinutesBeforeReset >= MinMinutesBeforeReset
                 && elapsedPercent is { } elapsed
                 && window.Percent - elapsed >= AheadOfPacePoints)
        {
            level = "pace";
            title = $"{subject}: using it fast";
            message = $"{window.Percent:F0}% used with {elapsed:F0}% of the time gone. {PaceCalculator.Summarize(pace)}.";
        }

        if (level == null || message == null)
        {
            return;
        }

        // The same situation in the same window is reported once; a new window (new reset time) starts fresh
        var key = $"{data.Provider}|{slot}|{WindowKey(window.ResetAt)}|{level}";
        if (_alreadyAlerted.TryAdd(key, DashboardValues.Utc(nowUtc)))
        {
            _dirty = true;
            alerts.Add(new UsageAlert(key, title, message));
        }
    }

    /// <summary>
    /// Buckets the reset time to 30 minutes so small jitter between fetches doesn't look like a new window.
    /// </summary>
    private static long WindowKey(DateTime? resetAt) =>
        resetAt is { } value ? value.ToUniversalTime().Ticks / BucketTicks : 0;

    private static string Capitalize(string text) =>
        text.Length == 0 ? "?" : char.ToUpperInvariant(text[0]) + text[1..];

    // ---- Persistence (all callers hold _gate) ----

    /// <summary>Loads the file once. A missing, empty, oversized or corrupt file means nothing was reported yet.</summary>
    private void EnsureLoaded(DateTime nowUtc)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (_filePath == null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists)
            {
                return;
            }

            if (info.Length > MaxFileBytes)
            {
                _logger.LogWarning("Alerts file is larger than {MaxBytes} bytes, starting empty", MaxFileBytes);
                return;
            }

            AlertsSentFile? file;
            using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                file = JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.AlertsSentFile);
            }

            if (file == null || file.Version < 1)
            {
                _logger.LogDebug("Alerts file is empty or has no usable schema version, starting empty");
                return;
            }

            foreach (var entry in file.Entries ?? [])
            {
                if (entry == null || string.IsNullOrEmpty(entry.Key) || entry.Key.Length > MaxKeyLength)
                {
                    continue;
                }

                _alreadyAlerted[entry.Key] = DashboardValues.Utc(entry.ReportedAtUtc);
            }

            if (Prune(nowUtc))
            {
                _dirty = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _alreadyAlerted.Clear();
            _logger.LogWarning(ex, "Failed to read the alerts file, starting empty");
        }
    }

    /// <summary>Writes the file atomically (temp file, then replace). Never throws.</summary>
    private void Save(DateTime nowUtc)
    {
        Prune(nowUtc);
        try
        {
            var file = new AlertsSentFile
            {
                Version = 1,
                Entries = _alreadyAlerted
                    .OrderBy(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new AlertSentEntry { Key = pair.Key, ReportedAtUtc = pair.Value })
                    .ToList()
            };

            var dir = Path.GetDirectoryName(Path.GetFullPath(_filePath!));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _filePath + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, AppJsonSerializerContext.Default.AlertsSentFile);
                stream.Flush();
            }

            File.Move(tempPath, _filePath!, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Stays dirty, so the next evaluation retries. Worst case an alert repeats after a restart.
            _logger.LogWarning(ex, "Failed to save the alerts file");
        }
    }

    /// <summary>Drops expired keys and enforces the cap. Returns true when anything was removed.</summary>
    private bool Prune(DateTime nowUtc)
    {
        var now = DashboardValues.Utc(nowUtc);
        var removed = false;

        foreach (var (key, reportedAt) in _alreadyAlerted.ToArray())
        {
            var expired = TryGetBucket(key, out var bucket) && bucket != 0
                ? now - new DateTime(bucket * BucketTicks, DateTimeKind.Utc) > ExpiredWindowAge
                : now - reportedAt > NoResetRetention;
            if (expired)
            {
                _alreadyAlerted.Remove(key);
                removed = true;
            }
        }

        if (_alreadyAlerted.Count > MaxKeys)
        {
            var oldest = _alreadyAlerted
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(_alreadyAlerted.Count - MaxKeys)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in oldest)
            {
                _alreadyAlerted.Remove(key);
            }

            removed = true;
        }

        return removed;
    }

    /// <summary>Reads the bucket out of <c>provider|slot|bucket|level</c> from the end (a slot may contain a pipe).</summary>
    private static bool TryGetBucket(string key, out long bucket)
    {
        bucket = 0;
        var levelStart = key.LastIndexOf('|');
        if (levelStart <= 0)
        {
            return false;
        }

        var bucketStart = key.LastIndexOf('|', levelStart - 1);
        if (bucketStart < 0 ||
            !long.TryParse(key.AsSpan(bucketStart + 1, levelStart - bucketStart - 1), out bucket) ||
            bucket < 0 || bucket > DateTime.MaxValue.Ticks / BucketTicks)
        {
            bucket = 0;
            return false;
        }

        return true;
    }
}
