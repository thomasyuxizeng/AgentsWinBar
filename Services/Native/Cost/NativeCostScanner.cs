using System.Buffers;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CodexBarWin.Services.Native.Cost;

public sealed record CostScanResult(long FilesRead, long BytesRead, long MalformedLines, long OversizedLines,
    long UnavailableFiles, int DiscoveredFiles, long DiscoveredBytes)
{
    public long SkippedFiles => UnavailableFiles;
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

    public NativeCostScanner(string indexDirectory, ILogger<NativeCostScanner> logger, TimeZoneInfo? zone = null)
    {
        _indexPath = Path.Combine(Path.GetFullPath(indexDirectory), "index.sqlite");
        _zone = zone;
        _logger = logger;
    }

    public CostScanResult Update(string claudeProjects, string codexSessions, int workers = 1,
        CancellationToken cancellationToken = default) =>
        Update([new(CostProvider.Claude, claudeProjects), new(CostProvider.Codex, codexSessions)], workers, cancellationToken);

    /// <summary>
    /// Indexes what changed under the roots. Without <paramref name="changedPaths"/> every root is listed; with it
    /// only those files and directories (from change notifications) are looked at, so an update with no changes
    /// touches neither the log folders nor the index.
    /// </summary>
    public CostScanResult Update(IReadOnlyList<CostLogRoot> roots, int workers = 1,
        CancellationToken cancellationToken = default, IReadOnlyCollection<string>? changedPaths = null)
    {
        if (changedPaths is { Count: 0 }) return new(0, 0, 0, 0, 0, 0, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        cancellationToken.ThrowIfCancellationRequested();
        using var background = new BackgroundPriority();
        var pathKey = CostKey.Hash(MemoryMarshal.AsBytes(_indexPath.ToUpperInvariant().AsSpan()));
        using var writer = new Mutex(false, $"CodexBarWin.CostIndex.{pathKey.First:X16}{pathKey.Second:X16}");
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
            // TimeZoneInfo caches the system zone until explicitly refreshed.
            if (_zone is null) TimeZoneInfo.ClearCachedData();
            return UpdateIndex(roots, workers, _zone ?? TimeZoneInfo.Local, changedPaths, cancellationToken);
        }
        finally
        {
            writer.ReleaseMutex();
        }
    }

    private CostScanResult UpdateIndex(IReadOnlyList<CostLogRoot> roots, int workers, TimeZoneInfo zone,
        IReadOnlyCollection<string>? changedPaths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        using var database = OpenForUpdate(cancellationToken);
        using var index = new CostIndex(database);
        var previous = index.ReadFiles();
        database.Execute("BEGIN IMMEDIATE");
        index.SetZone(zone, cancellationToken);
        database.Execute("COMMIT");

        // A compressed archive stands for the plain log of the same name: one index entry, and the plain file wins.
        var candidates = new Dictionary<(CostProvider, CostKey), Discovered>();
        foreach (var (provider, root) in roots)
        {
            if (provider is not (CostProvider.Claude or CostProvider.Codex))
                throw new ArgumentOutOfRangeException(nameof(roots));
            var directory = Path.GetFullPath(root);
            if (changedPaths is null)
            {
                if (Directory.Exists(directory)) Offer(provider, EnumerateLogs(directory, provider));
                continue;
            }

            var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
            foreach (var changed in changedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!changed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // Keep the root's spelling so a notification and a listing produce the same path key.
                var path = string.Concat(prefix, changed.AsSpan(prefix.Length));
                try
                {
                    if (IsLog(path, provider))
                    {
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

        var inputs = new List<Input>();
        var files = 0;
        long size = 0;
        foreach (var ((provider, key), entry) in candidates)
        {
            files++;
            size += entry.Length;
            previous.TryGetValue((provider, key), out var state);
            if (state is null || state.Length != entry.Length || state.WriteTime != entry.WriteTime ||
                state.Created != entry.Created)
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
        var batch = new List<Input>(BatchFiles);
        long batchBytes = 0;
        foreach (var input in inputs)
        {
            // Estimate bytes from the enumeration snapshot and commit at file boundaries.
            var unread = Math.Max(0, input.Entry.Length - (input.State?.Offset ?? 0));
            if (input.Entry.Compressed || input.State is { } state &&
                (input.Entry.Length < state.Length || input.Entry.Created != state.Created))
                unread = input.Entry.Length;
            batchBytes += unread;
            batch.Add(input);
            if (batch.Count >= BatchFiles || batchBytes >= BatchBytes) CommitBatch();
        }
        if (batch.Count > 0) CommitBatch();

        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Cost scan opened {Files} files and read {Bytes} bytes. Malformed {Malformed}, oversized {Oversized}, skipped {Skipped}",
            opened, bytes, malformed, oversized, unavailable);
        return new(opened, bytes, malformed, oversized, unavailable, files, size);

        void CommitBatch()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                database.Execute("BEGIN IMMEDIATE");
                long batchOpened = 0, bytesRead = 0, badLines = 0, oversizedLines = 0;
                var retry = 0;

                void Read(Input input, byte[] buffer)
                {
                    if (input.Skipped) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    var modified = false;
                    try
                    {
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
                                skipped = Skip(plain, indexed, buffer, cancellationToken);
                                if (skipped == indexed)
                                {
                                    parser = new CostLogParser(input.Provider, subagent, cursor);
                                    var current = generation;
                                    result = ReadLines(plain, indexed, long.MaxValue, buffer, parser,
                                        (evt, offset) => Accept(evt, offset, current), cancellationToken);
                                    break;
                                }

                                // Shorter than what was indexed: a different file, so index it from the start.
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

                ScanBatch(batch, workers, Read, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (retry != 0)
                {
                    // Undo partial mutations from a failed read before replaying readable files.
                    database.Execute("ROLLBACK");
                    continue;
                }

                database.Execute("COMMIT");
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

    public CostDataDto ReadReport(CostProvider provider, CostPricing? pricing = null, DateTimeOffset? asOf = null,
        int historyDays = 30, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyDays);
        var providerName = provider switch
        {
            CostProvider.Claude => "claude",
            CostProvider.Codex => "codex",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        pricing ??= new CostPricing();
        var today = CostIndex.LocalDay(asOf ?? DateTimeOffset.UtcNow, _zone ?? TimeZoneInfo.Local);
        var first = Math.Max(0, today.DayNumber - historyDays + 1);
        var rows = ReadDays(provider, first, today.DayNumber, cancellationToken);
        long priced = 0, unpriced = 0, unpricedTokens = 0;
        var days = new List<DailyCost>();
        foreach (var group in rows.GroupBy(row => row.Day))
        {
            var day = new CostTokens();
            long dayUnpricedTokens = 0;
            var breakdown = new List<ModelCost>();
            foreach (var modelGroup in group.GroupBy(row => row.Model))
            {
                decimal? cost = null;
                var modelTokens = new CostTokens();
                foreach (var row in modelGroup)
                {
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
                breakdown.Add(new ModelCost { ModelName = modelGroup.Key, Cost = cost, TotalTokens = modelTokens.Total });
            }
            days.Add(new DailyCost
            {
                Date = DateOnly.FromDayNumber(group.Key), TotalCost = breakdown.Sum(model => model.Cost ?? 0),
                TotalTokens = day.Total, InputTokens = provider == CostProvider.Codex ? day.Input + day.Read + day.Write : day.Input,
                OutputTokens = day.Output, CacheReadTokens = day.Read, CacheCreationTokens = day.Write,
                ReasoningTokens = day.Reasoning, UnpricedTokens = dayUnpricedTokens, ModelBreakdowns = breakdown
            });
            unpricedTokens += dayUnpricedTokens;
        }

        return new CostDataDto
        {
            Provider = providerName, CurrencyCode = "USD", HistoryDays = historyDays, Source = "local",
            Provenance = "native-jsonl",
            Coverage = new() { Priced = (int)Math.Min(int.MaxValue, priced), Unpriced = (int)Math.Min(int.MaxValue, unpriced) },
            SessionCostUSD = days.Count > 0 && days[^1].Date == today ? days[^1].TotalCost : 0,
            SessionTokens = days.Count > 0 && days[^1].Date == today ? days[^1].TotalTokens : 0,
            Last30DaysCostUSD = days.Where(d => d.Date.DayNumber >= today.DayNumber - 29).Sum(d => d.TotalCost),
            Last30DaysTokens = days.Where(d => d.Date.DayNumber >= today.DayNumber - 29).Sum(d => d.TotalTokens),
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

    private CostIndexDatabase OpenForUpdate(CancellationToken token)
    {
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

    private static bool ValidateIndex(CostIndexDatabase database)
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
        using var check = database.Prepare("PRAGMA quick_check(1)");
        return check.Step() && check.Text(0) == "ok";
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

    /// <summary>Reads and discards up to <paramref name="count"/> bytes; returns how many were there.</summary>
    private static long Skip(Stream stream, long count, byte[] buffer, CancellationToken token)
    {
        long skipped = 0;
        while (skipped < count)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, count - skipped)));
            if (read == 0) break;
            skipped += read;
        }
        return skipped;
    }

    internal const string CompressedSuffix = ".zst";

    /// <summary>Codex compresses older session logs to "name.jsonl.zst".</summary>
    internal static bool IsCompressed(ReadOnlySpan<char> path) =>
        path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLog(ReadOnlySpan<char> path, CostProvider provider) =>
        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
        provider == CostProvider.Codex && IsCompressed(path);

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

    private readonly record struct Discovered(string Path, long Length, long WriteTime, long Created,
        bool Compressed = false);
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
