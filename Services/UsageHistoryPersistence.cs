using System.Text;
using System.Text.Json;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

/// <summary>
/// History persistence beside the settings file. The file stays a JSON array (any version of the loader reads it), written
/// one sample per line. A save appends the new samples in place of the closing bracket instead of rewriting the file; the
/// whole file is rewritten atomically (compacted) at most once a day, after a load that found much expired or invalid data,
/// after a failed append, and whenever the in-memory history was replaced.
/// </summary>
public sealed class UsageHistoryPersistence
{
    /// <summary>How often the file is rewritten without the samples that have expired since.</summary>
    public static readonly TimeSpan CompactionInterval = TimeSpan.FromDays(1);

    private static readonly byte[] Separator = ",\n"u8.ToArray();

    private readonly UsageHistoryStore _store;
    private readonly ILogger<UsageHistoryPersistence> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private DateTimeOffset _lastSavedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCompactedAt = DateTimeOffset.MinValue;
    private long _savedRevision = -1;
    private long _savedGeneration = -1;
    private bool _compactPending = true;
    private bool _isLoaded;
    public bool IsLoaded => _isLoaded;
    public string FilePath { get; }

    public UsageHistoryPersistence(UsageHistoryStore store, ISettingsService settings,
        ILogger<UsageHistoryPersistence> logger, TimeProvider? timeProvider = null)
    {
        _store = store;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        FilePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.SettingsFilePath))!, "usage-history.json");
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct); acquired = true;
            if (!File.Exists(FilePath))
            {
                _compactPending = true;
                _isLoaded = true;
                return;
            }
            var (samples, intact) = await LoadFromFileAsync(FilePath, ct).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            _store.Restore(samples, now.UtcDateTime);
            _store.TakeUnsaved();
            _savedGeneration = _store.Generation;
            _savedRevision = _store.Revision;
            _lastCompactedAt = now;
            // A file that is mostly fine is only appended to; one with a damaged tail or much dead weight is rewritten at the next save
            _compactPending = !intact || ShouldCompact(samples.Count, _store.Count);
            _isLoaded = true;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Usage history could not be loaded"); }
        finally { if (acquired) _ioGate.Release(); }
    }

    public async Task SaveAsync(bool force = false, CancellationToken ct = default)
    {
        if (!_isLoaded) return;
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct); acquired = true;
            if (!_isLoaded) return;
            var now = _clock.GetUtcNow();
            _store.Prune(now.UtcDateTime);
            var revision = _store.Revision;
            if (revision == _savedRevision || (!force && now - _lastSavedAt < TimeSpan.FromMinutes(1))) return;
            var generation = _store.Generation;
            var compact = _compactPending || generation != _savedGeneration || now - _lastCompactedAt >= CompactionInterval;
            if (!compact)
            {
                // Taken first: anything added meanwhile has a newer revision and is appended by the next save
                var added = _store.TakeUnsaved();
                _compactPending = true;   // until the append below has completed
                if (added.Count > 0 && !await TryAppendAsync(added, ct)) compact = true;
            }
            if (compact)
            {
                _store.TakeUnsaved();
                await WriteAllAsync(_store.Samples, ct);
                _lastCompactedAt = now;
                _savedGeneration = generation;
            }
            _compactPending = false;
            _savedRevision = revision;
            _lastSavedAt = now;
        }
        catch (Exception ex)
        {
            // A failed or interrupted write may have lost samples taken for appending: the next save rewrites everything
            _compactPending = true;
            _logger.LogDebug(ex, "Usage history could not be saved");
        }
        finally { if (acquired) _ioGate.Release(); }
    }

    /// <summary>Rewrite after a load when the file holds more than 10% (and at least 50) samples the history no longer keeps.</summary>
    internal static bool ShouldCompact(int samplesInFile, int samplesKept) =>
        samplesInFile - samplesKept > Math.Max(50, samplesKept / 10);

    /// <summary>
    /// Streams the JSON array from disk in UTF-8 chunks without allocating a full-file UTF-16 string.
    /// A tail cut off by an interrupted append keeps every complete sample before it.
    /// </summary>
    internal static async Task<(List<UsageHistorySample> Samples, bool Intact)> LoadFromFileAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ParseStreamAsync(stream, ct).ConfigureAwait(false);
    }

    internal static async Task<(List<UsageHistorySample> Samples, bool Intact)> ParseStreamAsync(Stream stream, CancellationToken ct)
    {
        if (stream.CanSeek && stream.Length >= 3)
        {
            var start = stream.Position;
            var bom = new byte[3];
            var read = await stream.ReadAsync(bom.AsMemory(0, 3), ct).ConfigureAwait(false);
            if (read != 3 || bom[0] != 0xEF || bom[1] != 0xBB || bom[2] != 0xBF)
            {
                stream.Position = start;
            }
        }

        var samples = new List<UsageHistorySample>();
        var intact = true;
        try
        {
            await foreach (var sample in JsonSerializer.DeserializeAsyncEnumerable(
                stream, AppJsonSerializerContext.Default.UsageHistorySample, ct).ConfigureAwait(false))
            {
                if (sample != null)
                {
                    samples.Add(sample);
                }
            }
        }
        catch (JsonException)
        {
            intact = false;
        }
        return (samples, intact);
    }

    /// <summary>
    /// Reads the array. A tail cut off by an interrupted append (power loss) keeps every complete sample before it;
    /// anything else that is not a JSON array of samples reads as empty, as before.
    /// </summary>
    internal static List<UsageHistorySample> Parse(string json, out bool intact)
    {
        intact = true;
        try
        {
            return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageHistorySample) ?? [];
        }
        catch (JsonException)
        {
            intact = false;
        }
        var lastObject = json.LastIndexOf('}');
        if (lastObject > 0 && json.TrimStart().StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize(json[..(lastObject + 1)] + "]", AppJsonSerializerContext.Default.ListUsageHistorySample) ?? [];
            }
            catch (JsonException) { }
        }
        return [];
    }

    /// <summary>The array with one compact sample per line: "[\n{...},\n{...}\n]".</summary>
    internal static byte[] Format(IReadOnlyList<UsageHistorySample> samples)
    {
        using var buffer = new MemoryStream(samples.Count * 128 + 8);
        buffer.Write("["u8);
        WriteLines(buffer, samples, leadingSeparator: false);
        buffer.Write("\n]"u8);
        return buffer.ToArray();
    }

    private static void WriteLines(MemoryStream buffer, IReadOnlyList<UsageHistorySample> samples, bool leadingSeparator)
    {
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        for (var i = 0; i < samples.Count; i++)
        {
            buffer.Write(i == 0 && !leadingSeparator ? "\n"u8 : Separator);
            writer.Reset(buffer);
            JsonSerializer.Serialize(writer, samples[i], AppJsonSerializerContext.Default.UsageHistorySample);
            writer.Flush();
        }
    }

    private async Task WriteAllAsync(IReadOnlyList<UsageHistorySample> samples, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.Asynchronous))
        {
            using var chunk = new MemoryStream(32 * 1024);
            chunk.Write("["u8);
            using var writer = new Utf8JsonWriter(chunk, new JsonWriterOptions { Indented = false });
            for (var i = 0; i < samples.Count; i++)
            {
                chunk.Write(i == 0 ? "\n"u8 : Separator);
                writer.Reset(chunk);
                JsonSerializer.Serialize(writer, samples[i], AppJsonSerializerContext.Default.UsageHistorySample);
                writer.Flush();
                if (chunk.Length >= 16 * 1024)
                {
                    await stream.WriteAsync(chunk.GetBuffer().AsMemory(0, (int)chunk.Length), ct).ConfigureAwait(false);
                    chunk.SetLength(0);
                }
            }
            chunk.Write("\n]"u8);
            await stream.WriteAsync(chunk.GetBuffer().AsMemory(0, (int)chunk.Length), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    /// <summary>
    /// Writes the samples in place of the closing bracket and closes the array again. False (nothing written) when the file does
    /// not end like an array this class can extend, so the caller rewrites it instead.
    /// </summary>
    private async Task<bool> TryAppendAsync(IReadOnlyList<UsageHistorySample> samples, CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return false;
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1, useAsync: true);
        var tailLength = (int)Math.Min(stream.Length, 256);
        if (tailLength < 2) return false;
        var tail = new byte[tailLength];
        stream.Seek(-tailLength, SeekOrigin.End);
        await stream.ReadExactlyAsync(tail, ct);

        var close = LastNonWhitespace(tail, tailLength - 1);
        if (close < 0 || tail[close] != (byte)']') return false;
        var before = LastNonWhitespace(tail, close - 1);
        if (before < 0) return false;
        var empty = tail[before] == (byte)'[';
        if (!empty && tail[before] != (byte)'}') return false;

        using var buffer = new MemoryStream(samples.Count * 128 + 8);
        WriteLines(buffer, samples, leadingSeparator: !empty);
        buffer.Write("\n]"u8);

        // Overwrite from just after the last sample (or the opening bracket), so the separator follows it directly
        stream.Seek(stream.Length - tailLength + before + 1, SeekOrigin.Begin);
        await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct);
        stream.SetLength(stream.Position);
        await stream.FlushAsync(ct);
        return true;
    }

    private static int LastNonWhitespace(byte[] bytes, int from)
    {
        var i = from;
        while (i >= 0 && bytes[i] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') i--;
        return i;
    }
}
