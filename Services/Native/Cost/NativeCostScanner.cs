using System.Buffers;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AgentsWinBar.Services.Native.Cost;

/// <param name="SkippedPaths">The logs that could not be opened (locked or unreadable), or null when none were.</param>
/// <param name="IndexRebuilt">
/// An existing index failed validation (damaged, or of another version) and was replaced by an empty one. Every log
/// was then listed, whatever the caller asked for, because only a full listing can fill a new index.
/// </param>
/// <param name="RecheckPaths">
/// Antigravity databases that were read while their writer had them open (through the write-ahead log). They are
/// looked at again on the next update, in case a change notification for the log is late. Null when there were none.
/// </param>
/// <param name="Gemini">What the Antigravity databases read by this update held (aggregates only), or null.</param>
public sealed record CostScanResult(long FilesRead, long BytesRead, long MalformedLines, long OversizedLines,
    long UnavailableFiles, int DiscoveredFiles, long DiscoveredBytes, IReadOnlyList<string>? SkippedPaths = null,
    bool IndexRebuilt = false, IReadOnlyList<string>? RecheckPaths = null, AntigravityScanStatistics? Gemini = null)
{
    public long SkippedFiles => UnavailableFiles;
}

/// <summary>Aggregate counts of the Antigravity databases one update read. Nothing here names a conversation.</summary>
public sealed class AntigravityScanStatistics
{
    public int Databases { get; private set; }
    public int HistoryDatabases { get; private set; }
    public long Rows { get; private set; }
    public long InvalidRows { get; private set; }
    public long Calls { get; private set; }
    public long DatedByOwnTime { get; private set; }
    public long DatedByBotId { get; private set; }
    public long DatedByPosition { get; private set; }
    /// <summary>Calls without an established time. They are not counted anywhere.</summary>
    public long Undated { get; private set; }

    internal void Add(AntigravityScanStatistics other)
    {
        Databases += other.Databases;
        HistoryDatabases += other.HistoryDatabases;
        Rows += other.Rows;
        InvalidRows += other.InvalidRows;
        Calls += other.Calls;
        DatedByOwnTime += other.DatedByOwnTime;
        DatedByBotId += other.DatedByBotId;
        DatedByPosition += other.DatedByPosition;
        Undated += other.Undated;
    }

    internal void Add(AntigravityReadResult result)
    {
        Databases++;
        if (result.IsHistory) HistoryDatabases++;
        Rows += result.Rows;
        InvalidRows += result.InvalidRows;
        Undated += result.Undated;
        foreach (var call in result.Calls)
        {
            Calls++;
            switch (call.Dating)
            {
                case AntigravityDating.Embedded: DatedByOwnTime++; break;
                case AntigravityDating.BotId: DatedByBotId++; break;
                default: DatedByPosition++; break;
            }
        }
    }
}

public readonly record struct CostLogRoot(CostProvider Provider, string Directory);

/// <summary>Incrementally indexes complete JSONL records into a caller-owned directory.</summary>
public sealed partial class NativeCostScanner
{
    private const int MaxLineBytes = 512 * 1024;
    private const int BufferBytes = MaxLineBytes * 2;
    private readonly string _indexPath;
    private const int BatchFiles = 100;
    private const long BatchBytes = 256 * 1024 * 1024;
    private readonly TimeZoneInfo? _zone;
    private readonly ILogger<NativeCostScanner> _logger;
    /// <summary>The zone the index was last bucketed in by this instance (null before the first update).</summary>

    public NativeCostScanner(string indexDirectory, ILogger<NativeCostScanner> logger, TimeZoneInfo? zone = null)
    {
        _indexPath = Path.Combine(Path.GetFullPath(indexDirectory), "index.sqlite");
        _zone = zone;
        _logger = logger;
    }

    /// <summary>The system time zone, read fresh (tests replace it). Used only when no fixed zone was given.</summary>
    internal Func<TimeZoneInfo> SystemZone { get; init; } = () =>
    {
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local;
    };

    /// <summary>An archive is decompressed up to this many bytes; the rest (a damaged or hostile file) is not counted.</summary>
    internal long MaxArchiveBytes { get; init; } = 1L << 30;

    private int _quickChecks;

    /// <summary>1 while the next open of the index must run the full quick_check (the first open, and after an error).</summary>
    private int _fullCheckDue = 1;

    /// <summary>How many times PRAGMA quick_check has run over the index (tests watch this).</summary>
    internal int QuickChecks => Volatile.Read(ref _quickChecks);

    /// <summary>Called with the path of every log about to be read; tests make it throw to simulate a failing worker.</summary>
    internal Action<string>? ReadHook { get; init; }

    /// <summary>True when the index file exists (a missing index needs a full listing to rebuild).</summary>
    internal bool IndexExists => File.Exists(_indexPath);

    public CostScanResult Update(string claudeProjects, string codexSessions, int workers = 1,
        CancellationToken cancellationToken = default) =>
        Update([new(CostProvider.Claude, claudeProjects), new(CostProvider.Codex, codexSessions)], workers, cancellationToken);

    /// <summary>
    /// Indexes what changed under the roots. Without <paramref name="changedPaths"/> every root is listed; with it
    /// only those files and directories (from change notifications) are looked at, so an update with no changes
    /// touches neither the log folders nor the index (unless the system time zone changed since the last update,
    /// which re-buckets the stored days).
    /// </summary>
    public CostScanResult Update(IReadOnlyList<CostLogRoot> roots, int workers = 1,
        CancellationToken cancellationToken = default, IReadOnlyCollection<string>? changedPaths = null) =>
        UpdateAndReadReports(roots, workers, null, null, null, 30, cancellationToken, changedPaths).ScanResult;

    internal (CostScanResult ScanResult, IReadOnlyDictionary<CostProvider, CostDataDto> Reports) UpdateAndReadReports(
        IReadOnlyList<CostLogRoot> roots,
        int workers,
        IReadOnlyList<CostProvider>? reportProviders,
        CostPricing? pricing,
        DateTimeOffset? asOf,
        int historyDays,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? changedPaths = null)
    {
        var zone = _zone ?? SystemZone();
        if (changedPaths is not null)
        {
            changedPaths = RelevantPaths(roots, changedPaths);
            if (changedPaths.Count == 0 &&
                TryReadReportsFromDisk(reportProviders, pricing, asOf, historyDays, zone, cancellationToken) is { } skippedReports)
            {
                return (new(0, 0, 0, 0, 0, 0, 0), skippedReports);
            }
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        cancellationToken.ThrowIfCancellationRequested();
        using var background = new BackgroundPriority();
        var pathKey = CostKey.Hash(MemoryMarshal.AsBytes(_indexPath.ToUpperInvariant().AsSpan()));
        using var writer = new Mutex(false, $"AgentsWinBar.CostIndex.{pathKey.First:X16}{pathKey.Second:X16}");
        try
        {
            while (!writer.WaitOne(100))
                cancellationToken.ThrowIfCancellationRequested();
        }
        catch (AbandonedMutexException)
        {
            // A previous updater exited. SQLite rolls back its uncommitted batch.
        }

        try
        {
            return UpdateIndexCore(roots, workers, zone, changedPaths, reportProviders, pricing, asOf, historyDays, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever failed may have damaged the index: check all of it again at the next open.
            Volatile.Write(ref _fullCheckDue, 1);
            throw;
        }
        finally
        {
            writer.ReleaseMutex();
        }
    }

    private static List<string> RelevantPaths(IReadOnlyList<CostLogRoot> roots, IReadOnlyCollection<string> changedPaths)
    {
        if (changedPaths.Count == 0) return [];
        var prefixes = roots.Select(root => (root.Provider,
            Prefix: Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Directory)) + Path.DirectorySeparatorChar)).ToArray();
        var relevant = new List<string>();
        foreach (var changed in changedPaths)
        {
            string full;
            try { full = Path.GetFullPath(changed); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            foreach (var (provider, prefix) in prefixes)
            {
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // Antigravity databases count only directly inside their root ("antigravity" holds another root).
                var found = provider == CostProvider.Gemini
                    ? IsDirectChild(full, prefix) && IsLog(full, provider) && File.Exists(full)
                    : IsLog(full, provider) ? File.Exists(full) : Directory.Exists(full);
                if (found)
                {
                    relevant.Add(full);
                    break;
                }
            }
        }
        return relevant;
    }

    private (CostScanResult ScanResult, IReadOnlyDictionary<CostProvider, CostDataDto> Reports) UpdateIndexCore(
        IReadOnlyList<CostLogRoot> roots,
        int workers,
        TimeZoneInfo zone,
        IReadOnlyCollection<string>? changedPaths,
        IReadOnlyList<CostProvider>? reportProviders,
        CostPricing? pricing,
        DateTimeOffset? asOf,
        int historyDays,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        using var database = OpenForUpdate(cancellationToken, out var indexRebuilt, out var indexCreated);
        using var index = new CostIndex(database);
        database.Execute("BEGIN IMMEDIATE");
        index.SetZone(zone, cancellationToken);
        database.Execute("COMMIT");
        if (changedPaths is not null && (indexRebuilt || (indexCreated && changedPaths.Count == 0)))
        {
            // A new index holds nothing: filling it from the changed files alone would report far too little
            // while looking fresh. List every log instead.
            _logger.LogInformation("The native cost index is new during an incremental update; listing every log");
            changedPaths = null;
        }

        // A compressed archive stands for the plain log of the same name: one index entry, and the plain file wins.
        var candidates = new Dictionary<(CostProvider, CostKey), Discovered>();
        foreach (var (provider, root) in roots)
        {
            if (provider is not (CostProvider.Claude or CostProvider.Codex or CostProvider.Gemini))
                throw new ArgumentOutOfRangeException(nameof(roots));
            var directory = Path.GetFullPath(root);
            if (changedPaths is null)
            {
                if (Directory.Exists(directory))
                    Offer(provider, provider == CostProvider.Gemini ? EnumerateConversations(directory) : EnumerateLogs(directory, provider));
                continue;
            }

            // The changed paths are absolute already (see RelevantPaths).
            var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
            foreach (var changed in changedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!changed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // Keep the root's spelling so a notification and a listing produce the same path key.
                var path = string.Concat(prefix, changed.AsSpan(prefix.Length));
                try
                {
                    if (provider == CostProvider.Gemini)
                    {
                        if (IsDirectChild(path, prefix) && IsLog(path, provider) && Conversation(path) is { } conversation)
                            Offer(provider, [conversation]);
                    }
                    else if (IsLog(path, provider))
                    {
                        // The plain log wins over its archive, also when only the archive was named.
                        if (IsCompressed(path) && File.Exists(path[..^CompressedSuffix.Length]))
                            path = path[..^CompressedSuffix.Length];
                        var info = new FileInfo(path);
                        if (info.Exists && (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0)
                            Offer(provider, [new(path, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks)]);
                    }
                    else if (Directory.Exists(path) &&
                             (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    {
                        Offer(provider, EnumerateLogs(path, provider));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or locked since the notification; the next full listing covers it.
                }
            }
        }

        // Incremental changedPaths updates only query the affected keys instead of loading all historical files rows.
        Dictionary<(CostProvider, CostKey), IndexedFile>? previous = changedPaths is null ? index.ReadFiles() : null;
        var inputs = new List<Input>(changedPaths is null ? candidates.Count : Math.Min(candidates.Count, 16));
        var files = 0;
        long size = 0;
        foreach (var ((provider, key), entry) in candidates)
        {
            files++;
            size += entry.Length;
            var state = previous is not null
                ? (previous.TryGetValue((provider, key), out var found) ? found : null)
                : index.ReadFile(provider, key);
            // An Antigravity database is also new when its write-ahead log changed (agy writes there first).
            if (state is null || state.Length != entry.Length || state.WriteTime != entry.WriteTime ||
                state.Created != entry.Created || provider == CostProvider.Gemini &&
                (state.Offset != entry.WalLength || state.Generation != entry.WalWriteTime))
                inputs.Add(new(provider, key, entry, state));
        }

        void Offer(CostProvider provider, IEnumerable<Discovered> entries)
        {
            Span<char> scratch = stackalloc char[512];
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var compressed = IsCompressed(entry.Path);
                var logical = compressed ? entry.Path.AsSpan(..^CompressedSuffix.Length) : entry.Path.AsSpan();
                // Windows paths ignore case, and a notification can spell a path differently from a listing.
                var upper = logical.Length <= scratch.Length ? scratch[..logical.Length] : new char[logical.Length];
                logical.ToUpperInvariant(upper);
                var id = (provider, CostKey.Hash(MemoryMarshal.AsBytes((ReadOnlySpan<char>)upper)));
                if (candidates.TryGetValue(id, out var existing) && (!existing.Compressed || compressed)) continue;
                candidates[id] = entry with { Compressed = compressed };
            }
        }

        long opened = 0, bytes = 0, malformed = 0, oversized = 0, unavailable = 0;
        AntigravityScanStatistics? gemini = null;
        List<string>? recheck = null;
        var batch = new List<Input>(BatchFiles);
        long batchBytes = 0;
        foreach (var input in inputs)
        {
            // Estimate bytes from the enumeration snapshot and commit at file boundaries.
            var unread = Math.Max(0, input.Entry.Length - (input.State?.Offset ?? 0));
            if (input.Entry.Compressed || input.State is { } state &&
                (input.Entry.Length < state.Length || input.Entry.Created != state.Created))
                unread = input.Entry.Length;
            // A changed database is read whole, with its log.
            if (input.Provider == CostProvider.Gemini) unread = input.Entry.Length + Math.Max(0, input.Entry.WalLength);
            batchBytes += unread;
            batch.Add(input);
            if (batch.Count >= BatchFiles || batchBytes >= BatchBytes) CommitBatch();
        }
        if (batch.Count > 0) CommitBatch();

        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Cost scan opened {Files} files and read {Bytes} bytes. Malformed {Malformed}, oversized {Oversized}, skipped {Skipped}",
            opened, bytes, malformed, oversized, unavailable);
        List<string>? skippedPaths = null;
        foreach (var input in inputs)
            if (input.Skipped) (skippedPaths ??= []).Add(input.Entry.Path);
        var scanResult = new CostScanResult(opened, bytes, malformed, oversized, unavailable, files, size, skippedPaths,
            indexRebuilt, recheck, gemini);
        var reports = ReadReportsFromIndex(index, reportProviders, pricing, asOf, historyDays, zone, cancellationToken);
        return (scanResult, reports);

        void CommitBatch()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                database.Execute("BEGIN IMMEDIATE");
                long batchOpened = 0, bytesRead = 0, badLines = 0, oversizedLines = 0;
                var retry = 0;
                // Per attempt, so a batch that is rolled back and replayed does not count its databases twice.
                AntigravityScanStatistics? batchGemini = null;
                List<string>? batchRecheck = null;

                void Read(Input input, byte[] buffer)
                {
                    if (input.Skipped) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    var modified = false;
                    try
                    {
                        ReadHook?.Invoke(input.Entry.Path);
                        if (input.Provider == CostProvider.Gemini)
                        {
                            ReadConversation(input, ref modified);
                            Interlocked.Increment(ref batchOpened);
                            Interlocked.Add(ref bytesRead, input.Entry.Length + Math.Max(0, input.Entry.WalLength));
                            return;
                        }
                        using var stream = new FileStream(input.Entry.Path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                        var length = stream.Length;
                        var fileId = FileIdentity(stream.SafeFileHandle);
                        var old = input.State;
                        var directory = Path.GetDirectoryName(input.Entry.Path.AsSpan());
                        var subagent = directory.Contains("\\subagents\\", StringComparison.OrdinalIgnoreCase) ||
                            directory.EndsWith("\\subagents", StringComparison.OrdinalIgnoreCase);
                        void Accept(CostEvent evt, long offset, long rowGeneration)
                        {
                            var day = CostIndex.LocalDay(evt.Time, zone);
                            lock (index) index.Add(input.Provider, input.Key, rowGeneration, offset, day, evt);
                            modified = true;
                        }

                        CostLogParser parser;
                        (long CompleteOffset, long Bytes, long Oversized) result;
                        long generation;
                        if (input.Entry.Compressed)
                        {
                            // The archive holds the same bytes as the plain log it replaced, so the entry of that
                            // log (same key) says how much is already indexed: skip that much and read the rest.
                            generation = old?.Generation ?? 0;
                            var indexed = old?.Offset ?? 0;
                            var cursor = old?.Cursor ?? default;
                            long skipped;
                            while (true)
                            {
                                stream.Position = 0;
                                using var plain = new ZstdSharp.DecompressionStream(stream, leaveOpen: true);
                                skipped = Skip(plain, indexed, buffer, cancellationToken, out var last);
                                // The indexed part ends at a line break; anything else is a different file.
                                if (skipped == indexed && (indexed == 0 || last == (byte)'\n'))
                                {
                                    parser = new CostLogParser(input.Provider, subagent, cursor);
                                    var current = generation;
                                    // A damaged or hostile archive could decompress without end: stop at the cap.
                                    var limit = Math.Max(indexed, MaxArchiveBytes);
                                    result = ReadLines(plain, indexed, limit, buffer, parser,
                                        (evt, offset) => Accept(evt, offset, current), cancellationToken);
                                    if (indexed + result.Bytes >= limit)
                                        _logger.LogWarning("A compressed cost log holds more than {Limit} bytes; the rest is not counted", limit);
                                    break;
                                }

                                // Shorter than what was indexed, or not cut at a line: a different file, so index it
                                // from the start.
                                lock (index) index.RemoveOffsetUsage(input.Provider, input.Key, generation);
                                modified = true;
                                generation++;
                                indexed = 0;
                                cursor = default;
                            }
                            result.Bytes += skipped;
                        }
                        else
                        {
                            var reset = old is null || length < old.Length || length < old.Offset ||
                                old.Created != input.Entry.Created || (old.FileId != default && fileId != default && old.FileId != fileId);
                            generation = reset ? (old?.Generation ?? -1) + 1 : old!.Generation;
                            if (reset && old is not null)
                            {
                                lock (index) index.RemoveOffsetUsage(input.Provider, input.Key, old.Generation);
                                modified = true;
                            }

                            parser = new CostLogParser(input.Provider, subagent, reset ? default : old!.Cursor);
                            var start = reset ? 0 : old!.Offset;
                            stream.Position = start;
                            var current = generation;
                            result = ReadLines(stream, start, length, buffer, parser,
                                (evt, offset) => Accept(evt, offset, current), cancellationToken);
                        }
                        lock (index) index.Save(input.Provider, input.Key, new(length, input.Entry.WriteTime, input.Entry.Created,
                            result.CompleteOffset, generation, fileId, parser.Cursor));
                        modified = true;
                        Interlocked.Increment(ref batchOpened);
                        Interlocked.Add(ref bytesRead, result.Bytes);
                        Interlocked.Add(ref badLines, parser.BadLines);
                        Interlocked.Add(ref oversizedLines, result.Oversized);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or ZstdSharp.ZstdException ||
                        ex is IOException and not CostIndexDatabase.SqliteException)
                    {
                        input.Skipped = true;
                        Interlocked.Increment(ref unavailable);
                        if (modified) Interlocked.Exchange(ref retry, 1);
                    }
                }

                // A conversation database is decoded whole before anything is written: a database that cannot be read
                // (AntigravityDatabaseUnavailableException, an IOException) changes nothing and is tried again later.
                // A database read again replaces the calls it added before, as upstream recomputes a changed file:
                // a call that gained a response id, or a new time, is not counted under its old key as well.
                void ReadConversation(Input input, ref bool modified)
                {
                    var conversation = AntigravityConversationReader.Read(input.Entry.Path, cancellationToken);
                    lock (index)
                    {
                        modified = true;
                        if (input.State is not null) index.RemoveOffsetUsage(CostProvider.Gemini, input.Key, 0);
                        foreach (var call in conversation.Calls)
                        {
                            var time = DateTimeOffset.FromUnixTimeMilliseconds(call.TimestampMs);
                            index.Add(CostProvider.Gemini, input.Key, 0, 0, CostIndex.LocalDay(time, zone),
                                new CostEvent(call.Key, time, call.Model, call.Tokens, 0, 0, false));
                        }
                        // The log's size and time sit in the offset and generation columns, which a database does not use.
                        index.Save(CostProvider.Gemini, input.Key, new(input.Entry.Length, input.Entry.WriteTime,
                            input.Entry.Created, input.Entry.WalLength, input.Entry.WalWriteTime, default,
                            new CodexCursor { HasRequest = conversation.IsHistory }));
                        (batchGemini ??= new()).Add(conversation);
                        if (conversation.ReadThroughWal) (batchRecheck ??= []).Add(input.Entry.Path);
                    }
                }

                ScanBatch(batch, workers, Read, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (retry != 0)
                {
                    // Undo partial mutations from a failed read before replaying readable files.
                    database.Execute("ROLLBACK");
                    continue;
                }

                database.Execute("COMMIT");
                if (batchGemini is not null) (gemini ??= new()).Add(batchGemini);
                if (batchRecheck is not null) (recheck ??= []).AddRange(batchRecheck);
                opened += batchOpened;
                bytes += bytesRead;
                malformed += badLines;
                oversized += oversizedLines;
                _logger.LogDebug("Committed cost scan batch with {Files} files and {Bytes} bytes", batchOpened, bytesRead);
                batch.Clear();
                batchBytes = 0;
                return;
            }
        }
    }

    private static void ScanBatch(List<Input> batch, int workers, Action<Input, byte[]> read,
        CancellationToken cancellationToken)
    {
        if (workers == 1 || batch.Count == 1)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
            try
            {
                foreach (var input in batch) read(input, buffer);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            return;
        }

        try
        {
            Parallel.ForEach(batch, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
                () => (Buffer: ArrayPool<byte>.Shared.Rent(BufferBytes), Priority: new BackgroundPriority()),
                (input, _, local) =>
                {
                    read(input, local.Buffer);
                    return local;
                },
                local =>
                {
                    try { ArrayPool<byte>.Shared.Return(local.Buffer, clearArray: true); }
                    finally { local.Priority.Dispose(); }
                });
        }
        catch (AggregateException ex)
        {
            // Parallel.ForEach wraps what a worker throws. Callers match on the real exception, such as the
            // damaged-index error that triggers a rebuild, so hand it on as it was thrown.
            var failures = ex.Flatten().InnerExceptions;
            var first = failures.OfType<CostIndexDatabase.SqliteException>().FirstOrDefault(e => e.InvalidIndex) ??
                (failures.Count == 1 ? failures[0] : null);
            if (first is null) throw;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    public CostDataDto ReadReport(CostProvider provider, CostPricing? pricing = null, DateTimeOffset? asOf = null,
        int historyDays = 30, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyDays);
        pricing ??= new CostPricing();
        var zone = _zone ?? SystemZone();
        var today = CostIndex.LocalDay(asOf ?? DateTimeOffset.UtcNow, zone);
        var first = Math.Max(0, today.DayNumber - historyDays + 1);
        var rows = ReadDays(provider, first, today.DayNumber, cancellationToken);
        return BuildReport(provider, rows, pricing, today, historyDays);
    }

    private IReadOnlyDictionary<CostProvider, CostDataDto>? TryReadReportsFromDisk(
        IReadOnlyList<CostProvider>? reportProviders,
        CostPricing? pricing,
        DateTimeOffset? asOf,
        int historyDays,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_indexPath))
            return reportProviders is { Count: > 0 } ? null : new Dictionary<CostProvider, CostDataDto>();
        try
        {
            using var database = new CostIndexDatabase(_indexPath, readOnly: true);
            database.Execute("PRAGMA busy_timeout=5000; BEGIN");
            if (CostIndex.ReadVersion(database) != CostIndex.SchemaVersion || !ValidateIndex(database)) return null;
            using var index = new CostIndex(database);
            if (index.ReadZone() != zone.Id) return null;
            return ReadReportsFromIndex(index, reportProviders, pricing, asOf, historyDays, zone, cancellationToken);
        }
        catch (CostIndexDatabase.SqliteException ex) when (ex.InvalidIndex)
        {
            // Recheck under the writer mutex before replacing an unreadable index.
            Volatile.Write(ref _fullCheckDue, 1);
            return null;
        }
    }

    private static IReadOnlyDictionary<CostProvider, CostDataDto> ReadReportsFromIndex(
        CostIndex index,
        IReadOnlyList<CostProvider>? reportProviders,
        CostPricing? pricing,
        DateTimeOffset? asOf,
        int historyDays,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        if (reportProviders is null || reportProviders.Count == 0)
            return new Dictionary<CostProvider, CostDataDto>();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyDays);
        pricing ??= new CostPricing();
        var today = CostIndex.LocalDay(asOf ?? DateTimeOffset.UtcNow, zone);
        var first = Math.Max(0, today.DayNumber - historyDays + 1);
        var reports = new Dictionary<CostProvider, CostDataDto>(reportProviders.Count);
        foreach (var provider in reportProviders)
        {
            if (reports.ContainsKey(provider)) continue;
            // Without a single Antigravity database there is no Gemini history at all: no report, not a report of $0.
            if (provider == CostProvider.Gemini && !index.HasHistory(provider)) continue;
            var rows = index.ReadDays(provider, first, today.DayNumber, cancellationToken);
            reports[provider] = BuildReport(provider, rows, pricing, today, historyDays);
        }
        return reports;
    }

    private static CostDataDto BuildReport(CostProvider provider, List<IndexedDay> rows, CostPricing pricing,
        DateOnly today, int historyDays)
    {
        var providerName = provider switch
        {
            CostProvider.Claude => "claude",
            CostProvider.Codex => "codex",
            // The CodexBar CLI's name for the Gemini provider; the service matches reports on it.
            CostProvider.Gemini => "antigravity",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        long priced = 0, unpriced = 0, unpricedTokens = 0;
        var days = new List<DailyCost>();
        // CostIndex.ReadDays returns rows ordered by (Day, Model, Band), so a single pass needs no GroupBy allocations.
        var index = 0;
        while (index < rows.Count)
        {
            var currentDay = rows[index].Day;
            var day = new CostTokens();
            long dayUnpricedTokens = 0;
            decimal dayTotalCost = 0;
            var breakdown = new List<ModelCost>();

            while (index < rows.Count && rows[index].Day == currentDay)
            {
                var currentModel = rows[index].Model;
                if (currentModel == AntigravityConversationReader.ModelLessZeroUsage)
                {
                    // Upstream: a call with no model and no tokens counts for its day at $0, without a model row.
                    while (index < rows.Count && rows[index].Day == currentDay && rows[index].Model == currentModel)
                        priced += rows[index++].Count;
                    continue;
                }
                decimal? cost = null;
                var modelTokens = new CostTokens();

                while (index < rows.Count && rows[index].Day == currentDay &&
                       string.Equals(rows[index].Model, currentModel, StringComparison.Ordinal))
                {
                    var row = rows[index++];
                    var price = pricing.PriceTokens(provider, row.Model, (row.Band & 1) != 0, (row.Band & 2) != 0,
                        (row.Band & 4) != 0, row.Band >> 3, row.Tokens);
                    if (price is { } known)
                    {
                        cost = (cost ?? 0) + known;
                        priced += row.Count;
                    }
                    else
                    {
                        // No price is not a price of zero: these tokens are reported, not hidden in the total.
                        unpriced += row.Count;
                        dayUnpricedTokens += row.Tokens.Total;
                    }
                    modelTokens = modelTokens.Add(row.Tokens);
                }

                day = day.Add(modelTokens);
                dayTotalCost += cost ?? 0;
                breakdown.Add(new ModelCost { ModelName = currentModel, Cost = cost, TotalTokens = modelTokens.Total });
            }

            days.Add(new DailyCost
            {
                Date = DateOnly.FromDayNumber(currentDay), TotalCost = dayTotalCost,
                TotalTokens = day.Total, InputTokens = provider == CostProvider.Codex ? day.Input + day.Read + day.Write : day.Input,
                OutputTokens = day.Output, CacheReadTokens = day.Read, CacheCreationTokens = day.Write,
                ReasoningTokens = day.Reasoning, UnpricedTokens = dayUnpricedTokens, ModelBreakdowns = breakdown
            });
            unpricedTokens += dayUnpricedTokens;
        }

        decimal last30Cost = 0;
        long last30Tokens = 0;
        var last30Start = today.DayNumber - 29;
        for (var i = 0; i < days.Count; i++)
        {
            if (days[i].Date.DayNumber >= last30Start)
            {
                last30Cost += days[i].TotalCost;
                last30Tokens += days[i].TotalTokens;
            }
        }

        return new CostDataDto
        {
            Provider = providerName, CurrencyCode = "USD", HistoryDays = historyDays, Source = "local",
            Provenance = provider == CostProvider.Gemini ? "native-sqlite" : "native-jsonl",
            Coverage = new() { Priced = (int)Math.Min(int.MaxValue, priced), Unpriced = (int)Math.Min(int.MaxValue, unpriced) },
            SessionCostUSD = days.Count > 0 && days[^1].Date == today ? days[^1].TotalCost : 0,
            SessionTokens = days.Count > 0 && days[^1].Date == today ? days[^1].TotalTokens : 0,
            Last30DaysCostUSD = last30Cost,
            Last30DaysTokens = last30Tokens,
            UnpricedTokens = unpricedTokens,
            Daily = days
        };
    }

    private List<IndexedDay> ReadDays(CostProvider provider, int first, int last, CancellationToken token)
    {
        if (!File.Exists(_indexPath)) return [];
        using var database = new CostIndexDatabase(_indexPath, readOnly: true);
        database.Execute("PRAGMA busy_timeout=5000; BEGIN");
        if (CostIndex.ReadVersion(database) != CostIndex.SchemaVersion) return [];
        using var index = new CostIndex(database);
        return index.ReadDays(provider, first, last, token);
    }

    /// <param name="rebuilt">True when an existing index failed validation and was replaced by an empty one.</param>
    /// <param name="created">True when there was no usable index and a new one was created.</param>
    private CostIndexDatabase OpenForUpdate(CancellationToken token, out bool rebuilt, out bool created)
    {
        rebuilt = false;
        created = false;
        if (File.Exists(_indexPath))
        {
            CostIndexDatabase? existing = null;
            try
            {
                existing = new CostIndexDatabase(_indexPath);
                ConfigureWriter(existing);
                if (CostIndex.ReadVersion(existing) == CostIndex.SchemaVersion && ValidateIndex(existing))
                {
                    var validated = existing;
                    existing = null;
                    return validated;
                }
            }
            catch (CostIndexDatabase.SqliteException ex) when (ex.InvalidIndex)
            {
                _logger.LogDebug(ex, "Native cost index validation failed");
            }
            finally { existing?.Dispose(); }

            token.ThrowIfCancellationRequested();
            _logger.LogInformation("Rebuilding native cost index");
            rebuilt = true;
            File.Delete(_indexPath);
            File.Delete(_indexPath + "-journal");
            File.Delete(_indexPath + "-wal");
            File.Delete(_indexPath + "-shm");
        }

        var database = new CostIndexDatabase(_indexPath);
        try
        {
            ConfigureWriter(database);
            database.Execute(CostIndex.Schema);
            created = true;
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static void ConfigureWriter(CostIndexDatabase database) =>
        database.Execute("PRAGMA busy_timeout=5000; PRAGMA cache_size=-12288; PRAGMA temp_store=FILE");

    private bool ValidateIndex(CostIndexDatabase database)
    {
        using var objects = database.Prepare("""
            SELECT COUNT(*) FROM sqlite_master WHERE name IN
                ('settings','files','usage','days','usage_by_generation','usage_add','usage_remove','usage_replace','usage_delete')
            """);
        if (!objects.Step() || objects.Number(0) != 9) return false;
        using var layout = database.Prepare("""
            SELECT NOT EXISTS (SELECT utc_seconds, offset_generation FROM usage)
                OR EXISTS (SELECT 1 FROM settings WHERE id=1 AND zone<>'')
            """);
        if (!layout.Step() || layout.Number(0) != 1) return false;
        // A table with the right name can still have columns incompatible with the report query.
        using var reportLayout = CostIndex.PrepareReadDays(database);

        // quick_check reads the whole index, which grows with the lifetime history: run it on the first open of
        // this scanner (one per process) and again after an update failed, not on every refresh.
        if (Volatile.Read(ref _fullCheckDue) == 0) return true;
        Interlocked.Increment(ref _quickChecks);
        using var check = database.Prepare("PRAGMA quick_check(1)");
        if (!check.Step() || check.Text(0) != "ok") return false;
        Volatile.Write(ref _fullCheckDue, 0);
        return true;
    }

    private static (long CompleteOffset, long Bytes, long Oversized) ReadLines(Stream stream, long start, long length,
        byte[] buffer, CostLogParser parser, Action<CostEvent, long> accept, CancellationToken token)
    {
        var complete = start;
        var position = start;
        long readBytes = 0, oversized = 0;
        var skipping = false;
        var carry = 0;
        while (position < length)
        {
            token.ThrowIfCancellationRequested();
            var blockStart = position - carry;
            var read = stream.Read(buffer.AsSpan(carry, (int)Math.Min(BufferBytes - carry, length - position)));
            if (read == 0) break;
            position += read;
            readBytes += read;
            var view = buffer.AsSpan(0, carry + read);
            var consumed = 0;
            while (!view.IsEmpty)
            {
                var newline = view.IndexOf((byte)'\n');
                if (newline < 0) break;
                complete = blockStart + consumed + newline + 1;
                if (skipping || newline > MaxLineBytes) oversized++;
                else
                {
                    var line = view[..newline];
                    if (blockStart + consumed == 0 && line.StartsWith("\uFEFF"u8)) line = line[3..];
                    if (parser.Parse(line) is { } evt) accept(evt, complete);
                }
                skipping = false;
                consumed += newline + 1;
                view = view[(newline + 1)..];
            }
            skipping |= view.Length > MaxLineBytes;
            carry = skipping ? 0 : view.Length;
            if (carry > 0) view.CopyTo(buffer);
        }
        return (complete, readBytes, oversized);
    }

    /// <summary>
    /// Reads and discards up to <paramref name="count"/> bytes; returns how many were there and the last of them.
    /// </summary>
    private static long Skip(Stream stream, long count, byte[] buffer, CancellationToken token, out byte last)
    {
        long skipped = 0;
        last = 0;
        while (skipped < count)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, count - skipped)));
            if (read == 0) break;
            skipped += read;
            last = buffer[read - 1];
        }
        return skipped;
    }

    internal const string CompressedSuffix = ".zst";

    /// <summary>Codex compresses older session logs to "name.jsonl.zst".</summary>
    internal static bool IsCompressed(ReadOnlySpan<char> path) =>
        path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLog(ReadOnlySpan<char> path, CostProvider provider) => provider == CostProvider.Gemini
        ? path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).StartsWith('.')
        : path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || provider == CostProvider.Codex && IsCompressed(path);

    private static bool IsDirectChild(string path, string prefix) =>
        path.Length > prefix.Length && path.AsSpan(prefix.Length).IndexOfAny('\\', '/') < 0;

    /// <summary>
    /// The Antigravity databases directly inside <paramref name="directory"/> (upstream reads only the immediate
    /// entries of its roots). Hidden names are skipped, as upstream does, and so are links, as for every other log.
    /// </summary>
    private static IEnumerable<Discovered> EnumerateConversations(string directory)
    {
        var paths = new FileSystemEnumerable<string>(directory, (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && IsLog(entry.FileName, CostProvider.Gemini)
        };
        foreach (var path in paths)
            if (Conversation(path) is { } database) yield return database;
    }

    /// <summary>
    /// A database's state from the file itself, not from the listing (a directory listing can lag behind a file its
    /// writer keeps open), with its write-ahead log's size and time. Null when it is gone or is a link.
    /// </summary>
    private static Discovered? Conversation(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
            var wal = new FileInfo(path + "-wal");
            var walExists = wal.Exists;
            return new(path, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, false,
                walExists ? wal.Length : -1, walExists ? wal.LastWriteTimeUtc.Ticks : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileSystemEnumerable<Discovered> EnumerateLogs(string directory, CostProvider provider) =>
        new(directory,
            (ref FileSystemEntry entry) => new(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcTicks,
                entry.CreationTimeUtc.UtcTicks),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && IsLog(entry.FileName, provider)
        };

    private static CostKey FileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) return default;
        return new(info.Volume, ((long)info.IndexHigh << 32) | info.IndexLow);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    /// <param name="WalLength">For an Antigravity database: its write-ahead log's size, or -1 without one.</param>
    /// <param name="WalWriteTime">For an Antigravity database: its write-ahead log's write time, or 0 without one.</param>
    private readonly record struct Discovered(string Path, long Length, long WriteTime, long Created,
        bool Compressed = false, long WalLength = -1, long WalWriteTime = 0);
    private sealed record Input(CostProvider Provider, CostKey Key, Discovered Entry, IndexedFile? State)
    {
        internal bool Skipped { get; set; }
    }

    private readonly struct BackgroundPriority : IDisposable
    {
        private const int Begin = 0x00010000;
        private const int End = 0x00020000;
        private readonly bool _entered;

        public BackgroundPriority() => _entered = SetThreadPriority(GetCurrentThread(), Begin);

        public void Dispose()
        {
            if (_entered) SetThreadPriority(GetCurrentThread(), End);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(IntPtr thread, int priority);
}
