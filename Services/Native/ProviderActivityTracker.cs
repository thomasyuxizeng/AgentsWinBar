using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Services.Native.Cost;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services.Native;

/// <summary>When each provider's tool last wrote its local logs: the signal for refreshing a provider faster.</summary>
public interface IProviderActivitySource
{
    /// <summary>When the provider's tool last wrote its local logs (UTC), or null when no write was seen.</summary>
    DateTime? LastActivityUtc(string provider);

    /// <summary>
    /// Raised with the provider id when its logs change after a quiet <see cref="ProviderRefreshPlanner.ActiveWindow"/>
    /// (at most once per quiet period), on a thread-pool thread. Handlers must return quickly.
    /// </summary>
    event EventHandler<string>? ActivityStarted;

    /// <summary>Starts watching log folders that exist now and are not watched yet (cheap; throttled).</summary>
    void EnsureWatching();
}

/// <summary>
/// Watches the folders Claude Code, Codex and Antigravity write their logs to (the roots of
/// <see cref="NativeCostService.DefaultRoots"/>) and keeps one timestamp per provider. The notification handler only
/// stores the time: no file is opened, no name is read, logged or kept, and nothing runs on the UI thread. Name filters
/// (<c>*.jsonl</c>, Antigravity's <c>*.db</c> and <c>*.db-wal</c>) are matched by the watcher before any event object
/// is created, so unrelated writes cost nothing in managed code. A lost-notifications overflow counts as activity (it
/// means many writes); any other watcher error drops the watcher, and the next <see cref="EnsureWatching"/> after
/// <see cref="RetryInterval"/> starts it again, as it does for a folder that did not exist yet.
/// Separate from <see cref="CostLogWatcher"/>, whose watchers live only while a cost view is open.
/// </summary>
public sealed class ProviderActivityTracker : IProviderActivitySource, IDisposable
{
    /// <summary>Missing or failed folders are looked for again at most this often.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    /// <summary>The smallest buffer Windows accepts; an overflow only means "busy", which is activity anyway.</summary>
    private const int NotificationBufferBytes = 4096;

    private static readonly string[] ProviderIds = ["claude", "codex", "gemini"];

    private readonly Lock _gate = new();
    private readonly IReadOnlyList<CostLogRoot> _roots;
    private readonly FileSystemWatcher?[] _watchers;
    private readonly long[] _stamps = new long[ProviderIds.Length];
    private readonly ILogger<ProviderActivityTracker> _logger;
    private readonly TimeProvider _clock;
    private readonly bool _watch;
    private DateTimeOffset? _lastEnsure;
    private bool _disposed;

    public event EventHandler<string>? ActivityStarted;

    /// <summary>Production constructor: the user's log folders, as the native cost scanner reads them.</summary>
    public ProviderActivityTracker(ILogger<ProviderActivityTracker> logger, NativeTestOverrides? testOverrides = null)
        : this(NativeCostService.DefaultRoots(
            testOverrides?.ProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            testOverrides is null ? Environment.GetEnvironmentVariable(NativeCostService.GeminiHomeVariable) : null), logger)
    {
    }

    /// <param name="watch">False keeps the bookkeeping without OS watchers (tests report activity directly).</param>
    public ProviderActivityTracker(IReadOnlyList<CostLogRoot> roots, ILogger<ProviderActivityTracker> logger,
        TimeProvider? clock = null, bool watch = true)
    {
        _roots = roots;
        _watchers = new FileSystemWatcher?[roots.Count];
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _watch = watch;
    }

    public DateTime? LastActivityUtc(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider) || !ProviderConstants.IsValidProvider(provider.Trim())) return null;
        var slot = Slot(ProviderConstants.ValidateAndNormalize(provider));
        var stamp = Volatile.Read(ref _stamps[slot]);
        if (stamp == 0) return null;
        var now = _clock.GetUtcNow().UtcTicks;
        if (stamp > now)
        {
            // The clock was set back: count the write as just now, so the provider is active for one window, not until
            // the clock catches up.
            Interlocked.CompareExchange(ref _stamps[slot], now, stamp);
            stamp = now;
        }
        return new DateTime(stamp, DateTimeKind.Utc);
    }

    /// <summary>A log of <paramref name="provider"/> was written (the watcher callback; tests call it directly).</summary>
    internal void NotifyActivity(CostProvider provider)
    {
        try
        {
            var slot = (int)provider;
            var now = _clock.GetUtcNow().UtcTicks;
            var previous = Interlocked.Exchange(ref _stamps[slot], now);
            // Only the first write after a quiet period is announced; every other write just moves the stamp.
            if (previous == 0 || now - previous > ProviderRefreshPlanner.ActiveWindow.Ticks || now < previous)
                ActivityStarted?.Invoke(this, ProviderIds[slot]);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Activity notification failed ({Type})", ex.GetType().Name);
        }
    }

    /// <summary>How many OS watchers are running (for tests).</summary>
    internal int ActiveWatchers { get { lock (_gate) return _watchers.Count(watcher => watcher is not null); } }

    public void EnsureWatching()
    {
        if (!_watch) return;
        lock (_gate)
        {
            if (_disposed || Array.TrueForAll(_watchers, watcher => watcher is not null)) return;
            var now = _clock.GetUtcNow();
            if (_lastEnsure is { } last && now >= last && now - last < RetryInterval) return;
            _lastEnsure = now;
        }
        for (var i = 0; i < _roots.Count; i++)
        {
            lock (_gate)
            {
                if (_disposed || _watchers[i] is not null) continue;
            }
            var watcher = TryWatch(i);
            if (watcher is null) continue;
            lock (_gate)
            {
                if (!_disposed && _watchers[i] is null)
                {
                    _watchers[i] = watcher;
                    watcher = null;
                }
            }
            watcher?.Dispose();
        }
    }

    private FileSystemWatcher? TryWatch(int index)
    {
        var (provider, directory) = _roots[index];
        FileSystemWatcher? watcher = null;
        try
        {
            if (!Directory.Exists(directory)) return null;
            watcher = new FileSystemWatcher(directory)
            {
                // Antigravity's databases sit directly in their roots; the folders around them see unrelated churn.
                IncludeSubdirectories = provider != CostProvider.Gemini,
                InternalBufferSize = NotificationBufferBytes,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            if (provider == CostProvider.Gemini)
            {
                // agy writes new calls to the write-ahead log first, then to the database.
                watcher.Filters.Add("*.db");
                watcher.Filters.Add("*.db-wal");
            }
            else watcher.Filters.Add("*.jsonl");
            watcher.Created += (_, _) => NotifyActivity(provider);
            watcher.Changed += (_, _) => NotifyActivity(provider);
            watcher.Renamed += (_, _) => NotifyActivity(provider);
            watcher.Error += (sender, e) => OnError(index, provider, sender as FileSystemWatcher, e.GetException());
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            _logger.LogDebug("Activity folder {Index} cannot be watched ({Type})", index, ex.GetType().Name);
            return null;
        }
    }

    private void OnError(int index, CostProvider provider, FileSystemWatcher? watcher, Exception? error)
    {
        try
        {
            if (error is InternalBufferOverflowException)
            {
                // Too many writes to report one by one: the tool is busy. The watcher keeps running.
                NotifyActivity(provider);
                return;
            }
            _logger.LogDebug("Activity folder {Index} stopped reporting ({Type}); watching it again later", index,
                error?.GetType().Name ?? "unknown");
            lock (_gate)
            {
                if (watcher is null || _watchers[index] != watcher) return;
                _watchers[index] = null;
            }
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Activity watcher error handling failed ({Type})", ex.GetType().Name);
        }
    }

    private static int Slot(string provider) => provider switch
    {
        "claude" => (int)CostProvider.Claude,
        "codex" => (int)CostProvider.Codex,
        _ => (int)CostProvider.Gemini
    };

    public void Dispose()
    {
        FileSystemWatcher?[] watchers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watchers = [.. _watchers];
            Array.Clear(_watchers);
        }
        foreach (var watcher in watchers) watcher?.Dispose();
    }
}
