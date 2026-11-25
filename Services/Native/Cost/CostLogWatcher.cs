using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services.Native.Cost;

/// <summary>What the next cost update has to look at: every root, or only the paths that changed.</summary>
public readonly record struct CostScanPlan(bool FullListing, IReadOnlyCollection<string> ChangedPaths);

/// <summary>
/// Turns change notifications on the log roots into the list of files the next cost update has to open, so an
/// update without changes costs nothing. The roots are listed in full only at startup, after a lost notification
/// (buffer overflow, watcher error, a root that could not be watched) and every
/// <see cref="FullListingInterval"/> as a safety net.
/// </summary>
public sealed class CostLogWatcher : IDisposable
{
    public static readonly TimeSpan FullListingInterval = TimeSpan.FromHours(6);
    /// <summary>More pending paths than this are cheaper to find with one listing.</summary>
    internal const int MaxChangedPaths = 2048;
    private const int NotificationBufferBytes = 64 * 1024;

    private readonly object _gate = new();
    private readonly IReadOnlyList<CostLogRoot> _roots;
    private readonly FileSystemWatcher?[] _watchers;
    private readonly ILogger<CostLogWatcher> _logger;
    private readonly TimeProvider _clock;
    private readonly bool _watch;
    private HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private bool _fullListingNeeded = true;
    private DateTimeOffset? _lastFullListing;
    private bool _disposed;

    /// <param name="watch">False keeps the bookkeeping without OS watchers (tests feed notifications directly).</param>
    public CostLogWatcher(IReadOnlyList<CostLogRoot> roots, ILogger<CostLogWatcher> logger,
        TimeProvider? clock = null, bool watch = true)
    {
        _roots = roots.Select(root => root with { Directory = Path.GetFullPath(root.Directory) }).ToArray();
        _watchers = new FileSystemWatcher?[_roots.Count];
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _watch = watch;
    }

    /// <summary>
    /// Hands out the pending work and clears it. Report the outcome with <see cref="Complete"/> so a failed update
    /// is not forgotten.
    /// </summary>
    public CostScanPlan TakePlan()
    {
        EnsureWatching();
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var full = _fullListingNeeded || _lastFullListing is not { } last || now < last ||
                now - last >= FullListingInterval || _changed.Count > MaxChangedPaths;
            var changed = _changed;
            _changed = new(StringComparer.OrdinalIgnoreCase);
            _fullListingNeeded = false;
            return full ? new(true, []) : new(false, changed);
        }
    }

    public void Complete(CostScanPlan plan, bool succeeded)
    {
        lock (_gate)
        {
            if (succeeded)
            {
                if (plan.FullListing) _lastFullListing = _clock.GetUtcNow();
                return;
            }
            if (plan.FullListing) _fullListingNeeded = true;
            else _changed.UnionWith(plan.ChangedPaths);
        }
    }

    /// <summary>The paths waiting for the next plan (a snapshot, for tests).</summary>
    internal IReadOnlyCollection<string> PendingPaths { get { lock (_gate) return [.. _changed]; } }

    /// <summary>A file or directory was created, or a log was written to.</summary>
    internal void NotifyChanged(CostProvider provider, string path, bool created)
    {
        // A new directory can arrive with logs already inside (a move), so it is listed on the next update.
        // A write to a directory only says that something inside it changed, which has its own notification.
        // Other new files (notes, temporary files) are not logs and are not queued.
        if (NativeCostScanner.IsLog(path, provider) || created && Directory.Exists(path)) Mark(path);
    }

    /// <summary>A rename moves a log, or a whole directory of logs, to a path the index has not seen.</summary>
    internal void NotifyRenamed(string newPath)
    {
        // Any provider's log name counts here; the scanner checks the name against the root's provider.
        if (NativeCostScanner.IsLog(newPath, CostProvider.Codex) || Directory.Exists(newPath)) Mark(newPath);
    }

    /// <summary>A log that could not be read (locked, for example) is looked at again on the next update.</summary>
    internal void Retry(string path) => Mark(path);

    /// <summary>Notifications were lost; only a listing can tell what changed.</summary>
    internal void NotifyLost()
    {
        lock (_gate)
        {
            _fullListingNeeded = true;
            _changed.Clear();
        }
    }

    private void Mark(string path)
    {
        lock (_gate)
        {
            // A pending listing finds everything; do not grow the set meanwhile.
            if (_fullListingNeeded || string.IsNullOrEmpty(path)) return;
            _changed.Add(path);
            if (_changed.Count <= MaxChangedPaths) return;
            _fullListingNeeded = true;
            _changed.Clear();
        }
    }

    private void EnsureWatching()
    {
        if (!_watch) return;
        for (var i = 0; i < _roots.Count; i++)
        {
            lock (_gate)
            {
                if (_disposed || _watchers[i] is not null) continue;
            }
            var (provider, directory) = _roots[i];
            if (!Directory.Exists(directory)) continue;
            FileSystemWatcher? watcher = null;
            try
            {
                var slot = i;
                watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = NotificationBufferBytes,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                                   NotifyFilters.Size
                };
                watcher.Created += (_, e) => NotifyChanged(provider, e.FullPath, created: true);
                watcher.Changed += (_, e) => NotifyChanged(provider, e.FullPath, created: false);
                watcher.Renamed += (_, e) => NotifyRenamed(e.FullPath);
                watcher.Error += (sender, e) => OnError(slot, sender as FileSystemWatcher, e.GetException());
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                watcher?.Dispose();
                watcher = null;
                _logger.LogDebug("Cost log folder {Index} cannot be watched ({Type}); listing it instead", i, ex.GetType().Name);
            }

            lock (_gate)
            {
                // Changes made before this watcher started, or while a root cannot be watched, need a listing.
                _fullListingNeeded = true;
                if (watcher is null) continue;
                if (_disposed || _watchers[i] is not null) watcher.Dispose();
                else _watchers[i] = watcher;
            }
        }
    }

    private void OnError(int slot, FileSystemWatcher? watcher, Exception? error)
    {
        try
        {
            _logger.LogDebug("Cost log notifications lost ({Type}); the next update lists the folders",
                error?.GetType().Name ?? "unknown");
            lock (_gate)
            {
                _fullListingNeeded = true;
                _changed.Clear();
                // An overflow leaves the watcher running; any other error may not, so it is replaced.
                if (error is InternalBufferOverflowException || watcher is null || _watchers[slot] != watcher) return;
                _watchers[slot] = null;
            }
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Cost log watcher error handling failed ({Type})", ex.GetType().Name);
        }
    }

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
