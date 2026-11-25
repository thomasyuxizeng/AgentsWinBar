using System.Buffers;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CodexBarWin.Services.Native.Cost;

public sealed record CostScanResult(long FilesRead, long BytesRead, long MalformedLines, long OversizedLines,
    long UnavailableFiles, int DiscoveredFiles, long DiscoveredBytes);

public readonly record struct CostLogRoot(CostProvider Provider, string Directory);

/// <summary>Incrementally indexes complete JSONL records into a caller-owned directory.</summary>
public sealed class NativeCostScanner
{
    private const int MaxLineBytes = 512 * 1024;
    private const int BufferBytes = MaxLineBytes * 2;
    private readonly string _indexPath;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger<NativeCostScanner> _logger;

    public NativeCostScanner(string indexDirectory, ILogger<NativeCostScanner> logger, TimeZoneInfo? zone = null)
    {
        Directory.CreateDirectory(indexDirectory);
        _indexPath = Path.Combine(Path.GetFullPath(indexDirectory), "index.sqlite");
        _zone = zone ?? TimeZoneInfo.Local;
        _logger = logger;
    }

    public CostScanResult Update(string claudeProjects, string codexSessions, int workers = 1,
        CancellationToken cancellationToken = default) =>
        Update([new(CostProvider.Claude, claudeProjects), new(CostProvider.Codex, codexSessions)], workers, cancellationToken);

    public CostScanResult Update(IReadOnlyList<CostLogRoot> roots, int workers = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        using var database = Open();
        database.Execute("BEGIN IMMEDIATE");
        using var index = new CostIndex(database);
        var previous = index.ReadFiles();
        var inputs = new List<Input>();
        var discovered = new HashSet<(CostProvider, CostKey)>();
        var files = 0;
        long size = 0;
        foreach (var (provider, root) in roots)
        {
            if (provider is not (CostProvider.Claude or CostProvider.Codex))
                throw new ArgumentOutOfRangeException(nameof(roots));
            var directory = Path.GetFullPath(root);
            if (!Directory.Exists(directory)) continue;
            var entries = new FileSystemEnumerable<Discovered>(directory,
                (ref FileSystemEntry entry) => new(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcTicks,
                    entry.CreationTimeUtc.UtcTicks),
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    !entry.IsDirectory && entry.FileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            };
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = CostKey.Hash(MemoryMarshal.AsBytes(entry.Path.AsSpan()));
                if (!discovered.Add((provider, key))) continue;
                files++;
                size += entry.Length;
                previous.TryGetValue((provider, key), out var state);
                if (state is null || state.Length != entry.Length || state.WriteTime != entry.WriteTime ||
                    state.Created != entry.Created)
                    inputs.Add(new(provider, key, entry, state));
            }
        }

        long opened = 0, bytes = 0, malformed = 0, oversized = 0, unavailable = 0;
        void Read(Input input, byte[] buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream stream;
            try
            {
                stream = new FileStream(input.Entry.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                Interlocked.Increment(ref unavailable);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                Interlocked.Increment(ref unavailable);
                return;
            }
            using (stream)
            {
                var length = stream.Length;
                var fileId = FileIdentity(stream.SafeFileHandle);
                var old = input.State;
                var reset = old is null || length < old.Length || length < old.Offset ||
                            old.Created != input.Entry.Created || (old.FileId != default && fileId != default && old.FileId != fileId);
                var generation = reset ? (old?.Generation ?? -1) + 1 : old!.Generation;
                var parser = new CostLogParser(input.Provider,
                    Path.GetDirectoryName(input.Entry.Path.AsSpan()).Contains("\\subagents\\", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetDirectoryName(input.Entry.Path.AsSpan()).EndsWith("\\subagents", StringComparison.OrdinalIgnoreCase),
                    reset ? default : old!.Cursor);

                var start = reset ? 0 : old!.Offset;
                stream.Position = start;
                var result = ReadLines(stream, length, buffer, parser, (evt, offset) =>
                {
                    var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(evt.Time, _zone).DateTime);
                    var identity = evt.Key == default ? input.Key.Combine(new(generation, offset)) : evt.Key;
                    lock (index) index.Add(input.Provider, input.Key, identity, day, evt);
                }, cancellationToken);
                lock (index) index.Save(input.Provider, input.Key, new(length, input.Entry.WriteTime, input.Entry.Created,
                    result.CompleteOffset, generation, fileId, parser.Cursor));
                Interlocked.Increment(ref opened);
                Interlocked.Add(ref bytes, result.Bytes);
                Interlocked.Add(ref malformed, parser.BadLines);
                Interlocked.Add(ref oversized, result.Oversized);
            }
        }

        if (inputs.Count > 0)
        {
            if (workers == 1)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
                try
                {
                    foreach (var input in inputs) Read(input, buffer);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            }
            else
            {
                Parallel.ForEach(inputs, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
                    () => ArrayPool<byte>.Shared.Rent(BufferBytes),
                    (input, _, buffer) =>
                    {
                        Read(input, buffer);
                        return buffer;
                    },
                    buffer => ArrayPool<byte>.Shared.Return(buffer, clearArray: true));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        database.Execute("COMMIT");
        _logger.LogDebug("Cost scan opened {Files} files and read {Bytes} bytes. Malformed {Malformed}, oversized {Oversized}, disappeared {Unavailable}",
            opened, bytes, malformed, oversized, unavailable);
        return new(opened, bytes, malformed, oversized, unavailable, files, size);
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
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOf ?? DateTimeOffset.UtcNow, _zone).DateTime);
        var first = Math.Max(0, today.DayNumber - historyDays + 1);
        using var database = Open();
        using var index = new CostIndex(database);
        var rows = index.ReadDays(provider, first, today.DayNumber, cancellationToken);
        long priced = 0, unpriced = 0;
        var days = new List<DailyCost>();
        foreach (var group in rows.GroupBy(row => row.Day))
        {
            var day = new CostTokens();
            var breakdown = new List<ModelCost>();
            foreach (var modelGroup in group.GroupBy(row => row.Model))
            {
                decimal? cost = null;
                var modelTokens = new CostTokens();
                foreach (var row in modelGroup)
                {
                    var rate = pricing.GetRates(provider, row.Model, (row.Band & 1) != 0, (row.Band & 2) != 0,
                        (row.Band & 4) != 0, row.Band >> 3);
                    if (rate is { } rates)
                    {
                        cost = (cost ?? 0) + rates.Price(row.Tokens);
                        priced += row.Count;
                    }
                    else unpriced += row.Count;
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
                ReasoningTokens = day.Reasoning, ModelBreakdowns = breakdown
            });
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
            Daily = days
        };
    }

    private CostIndexDatabase Open()
    {
        var database = new CostIndexDatabase(_indexPath);
        try
        {
            // Fit the initial index's dirty pages in the bounded cache and avoid repeated spill writes.
            database.Execute("PRAGMA busy_timeout=5000; PRAGMA cache_size=-12288;");
            using var version = database.Prepare("PRAGMA user_version");
            version.Step();
            var schema = version.Number(0);
            if (schema == 0) database.Execute(CostIndex.Schema);
            else if (schema != 1) throw new IOException("Unsupported native cost index schema.");
            using var setZone = database.Prepare("INSERT OR IGNORE INTO settings VALUES (1, ?)");
            var key = CostKey.Hash(MemoryMarshal.AsBytes(_zone.ToSerializedString().AsSpan()));
            var zone = $"{key.First:X16}{key.Second:X16}";
            setZone.Bind(1, zone);
            setZone.Write();
            using var readZone = database.Prepare("SELECT zone FROM settings WHERE id=1");
            if (!readZone.Step() || readZone.Text(0) != zone)
                throw new IOException("The native cost index uses a different reporting time zone.");
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static (long CompleteOffset, long Bytes, long Oversized) ReadLines(FileStream stream, long length, byte[] buffer,
        CostLogParser parser, Action<CostEvent, long> accept, CancellationToken token)
    {
        var complete = stream.Position;
        long readBytes = 0, oversized = 0;
        var skipping = false;
        var carry = 0;
        while (stream.Position < length)
        {
            token.ThrowIfCancellationRequested();
            var start = stream.Position - carry;
            var read = stream.Read(buffer.AsSpan(carry, (int)Math.Min(BufferBytes - carry, length - stream.Position)));
            if (read == 0) break;
            readBytes += read;
            var view = buffer.AsSpan(0, carry + read);
            var consumed = 0;
            while (!view.IsEmpty)
            {
                var newline = view.IndexOf((byte)'\n');
                if (newline < 0) break;
                complete = start + consumed + newline + 1;
                if (skipping || newline > MaxLineBytes) oversized++;
                else
                {
                    var line = view[..newline];
                    if (start + consumed == 0 && line.StartsWith("\uFEFF"u8)) line = line[3..];
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

    private readonly record struct Discovered(string Path, long Length, long WriteTime, long Created);
    private readonly record struct Input(CostProvider Provider, CostKey Key, Discovered Entry, IndexedFile? State);
}
