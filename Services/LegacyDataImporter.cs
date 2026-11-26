using System.Text.Json;
using AgentsWinBar.Helpers;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

/// <summary>What a run of <see cref="LegacyDataImporter"/> did.</summary>
public enum LegacyImportOutcome
{
    /// <summary>The marker shows an earlier run finished; nothing was looked at.</summary>
    AlreadyDone,
    /// <summary>The data folder already had settings.json; nothing was copied.</summary>
    TargetHasSettings,
    /// <summary>No old data folder with settings.json was found.</summary>
    NoSource,
    /// <summary>Every file was copied or already present.</summary>
    Imported,
    /// <summary>settings.json was copied but at least one other file failed.</summary>
    Partial,
    /// <summary>settings.json could not be copied (or the source could not be read); the next start tries again.</summary>
    Failed,
}

/// <summary>Counts only; never file contents.</summary>
public sealed record LegacyImportResult(
    LegacyImportOutcome Outcome, string? SourceDirectory = null, int Copied = 0, int Skipped = 0,
    IReadOnlyList<string>? FailedFiles = null)
{
    public IReadOnlyList<string> FailedFiles { get; init; } = FailedFiles ?? [];

    /// <summary>
    /// The old cost index could not be copied as a snapshot (it was busy, damaged or unreadable) and was left out.
    /// That is not a failure: the cost scanner rebuilds the index from the logs.
    /// </summary>
    public bool IndexSkipped { get; init; }
}

/// <summary>
/// One-time import of the data the app wrote under its old name (CodexBarWin). Runs at startup before settings and
/// caches load. When the data folder has no settings.json, it copies the files of the first old folder that has one:
/// the old packaged app's LocalCache (most recently written first), then the old unpackaged folder. Existing files
/// are never overwritten and the source is never changed. settings.json is copied last, so an interrupted import is
/// retried at the next start; a marker file records a finished import so it runs once. The cost index is copied as a
/// <c>VACUUM INTO</c> snapshot (left out when that fails), and a partial import records the names of the files that
/// failed in the marker, so the next start retries exactly those.
/// </summary>
public sealed class LegacyDataImporter
{
    public const string MarkerFileName = "legacy-import.json";
    internal const string SettingsFileName = "settings.json";
    private const string IndexFileName = "index.sqlite";
    private const string TemporarySuffix = ".import.tmp";
    internal const string RetryMarkerPrefix = "legacy-import.retry.";

    /// <summary>Subfolders copied besides the top-level files: the native cost index (index.sqlite, as a snapshot).</summary>
    internal static readonly string[] Subfolders = [Native.NativeCostService.IndexFolderName];
    private static readonly string IndexRelativePath = Path.Combine(Native.NativeCostService.IndexFolderName, IndexFileName);
    private static readonly string[] IndexSidecarSuffixes = ["-wal", "-shm", "-journal"];

    private readonly ILogger<LegacyDataImporter> _logger;
    private readonly string _targetDirectory;
    private readonly Func<IEnumerable<string>> _candidateSources;
    private readonly Action<string, Exception>? _reportFailure;

    /// <param name="targetDirectory">The data folder (normally %LOCALAPPDATA%\AgentsWinBar).</param>
    /// <param name="candidateSources">Old data folders in order of preference; see <see cref="DefaultSources"/>.</param>
    /// <param name="reportFailure">Records a failed or partial import where Release builds keep it (crash.log).</param>
    public LegacyDataImporter(ILogger<LegacyDataImporter> logger, string targetDirectory,
        Func<IEnumerable<string>> candidateSources, Action<string, Exception>? reportFailure = null)
    {
        _logger = logger;
        _targetDirectory = targetDirectory;
        _candidateSources = candidateSources;
        _reportFailure = reportFailure;
    }

    /// <summary>
    /// The old data folders under <paramref name="localAppData"/>: each Packages\CodexBarWin_*\LocalCache\Local\CodexBarWin
    /// (most recently written first), then %LOCALAPPDATA%\CodexBarWin.
    /// </summary>
    public static IEnumerable<string> DefaultSources(string localAppData)
    {
        var packaged = new List<(string Path, DateTime Written)>();
        try
        {
            var packages = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var package in Directory.EnumerateDirectories(packages, AppPaths.LegacyAppName + "_*"))
                {
                    var folder = Path.Combine(package, "LocalCache", "Local", AppPaths.LegacyAppName);
                    if (File.Exists(Path.Combine(folder, SettingsFileName)))
                        packaged.Add((folder, LastWritten(folder)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable Packages folder leaves the unpackaged source.
        }

        foreach (var (path, _) in packaged.OrderByDescending(p => p.Written))
            yield return path;
        yield return Path.Combine(localAppData, AppPaths.LegacyAppName);
    }

    /// <summary>Runs the import. Never throws: a failure is logged and startup continues.</summary>
    public LegacyImportResult Run()
    {
        try
        {
            var result = RunCore();
            Report(result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import of old data failed");
            _reportFailure?.Invoke("LegacyDataImporter", ex);
            return new LegacyImportResult(LegacyImportOutcome.Failed);
        }
    }

    private LegacyImportResult RunCore()
    {
        var target = Path.GetFullPath(_targetDirectory);
        if (File.Exists(Path.Combine(target, MarkerFileName)))
            return RetryFailedFiles(target) ?? new LegacyImportResult(LegacyImportOutcome.AlreadyDone);

        if (File.Exists(Path.Combine(target, SettingsFileName)))
        {
            WriteMarker(target, new LegacyImportResult(LegacyImportOutcome.TargetHasSettings));
            return new LegacyImportResult(LegacyImportOutcome.TargetHasSettings);
        }

        var source = _candidateSources()
            .Select(Path.GetFullPath)
            .FirstOrDefault(s => !SamePath(s, target) && File.Exists(Path.Combine(s, SettingsFileName)));
        if (source is null)
        {
            WriteMarker(target, new LegacyImportResult(LegacyImportOutcome.NoSource));
            return new LegacyImportResult(LegacyImportOutcome.NoSource);
        }

        Directory.CreateDirectory(target);
        int copied = 0, skipped = 0;
        var indexSkipped = false;
        var failed = new List<string>();

        // settings.json goes last: until it is in place, the next start retries (existing files are skipped).
        var files = Directory.EnumerateFiles(source)
            .Select(f => Path.GetFileName(f))
            .Where(name => !name.EndsWith(TemporarySuffix, StringComparison.OrdinalIgnoreCase) &&
                           !IsMarkerName(name) &&
                           !string.Equals(name, SettingsFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var sub in Subfolders)
        {
            var folder = Path.Combine(source, sub);
            if (!Directory.Exists(folder)) continue;
            // The index is copied as a snapshot of its own; its journal files are not copied at all.
            files.AddRange(Directory.EnumerateFiles(folder)
                .Select(f => Path.Combine(sub, Path.GetFileName(f)))
                .Where(name => !name.EndsWith(TemporarySuffix, StringComparison.OrdinalIgnoreCase) && !IsIndexSidecar(name)));
        }
        files.Add(SettingsFileName);

        foreach (var relative in files)
        {
            switch (CopyFile(source, target, relative))
            {
                case CopyResult.Copied: copied++; break;
                case CopyResult.Skipped: skipped++; break;
                case CopyResult.IndexSkipped: indexSkipped = true; break;
                default: failed.Add(relative); break;
            }
        }

        var settingsCopied = File.Exists(Path.Combine(target, SettingsFileName));
        var outcome = !settingsCopied ? LegacyImportOutcome.Failed
            : failed.Count > 0 ? LegacyImportOutcome.Partial
            : LegacyImportOutcome.Imported;
        var result = new LegacyImportResult(outcome, source, copied, skipped, failed) { IndexSkipped = indexSkipped };
        if (outcome != LegacyImportOutcome.Failed) WriteMarker(target, result);
        return result;
    }

    private enum CopyResult { Copied, Skipped, Failed, IndexSkipped }

    private static bool IsIndexFile(string relative) =>
        string.Equals(relative, IndexRelativePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The SQLite side files of the index (-wal, -shm, -journal); a snapshot needs none of them.</summary>
    private static bool IsIndexSidecar(string relative) =>
        IndexSidecarSuffixes.Any(suffix => string.Equals(relative, IndexRelativePath + suffix, StringComparison.OrdinalIgnoreCase));

    private static bool IsMarkerName(string name) =>
        string.Equals(name, MarkerFileName, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(RetryMarkerPrefix, StringComparison.OrdinalIgnoreCase);

    private CopyResult CopyFile(string source, string target, string relative)
    {
        var from = Path.Combine(source, relative);
        var to = Path.Combine(target, relative);
        return IsIndexFile(relative) ? CopyIndexSnapshot(from, to) : CopyOne(from, to);
    }

    /// <summary>
    /// The files that failed in an earlier run (named in the marker) are tried again; nothing else is. Null when the
    /// marker names nothing to retry: it finished, it predates the list of names, or the old folder is gone.
    /// </summary>
    private LegacyImportResult? RetryFailedFiles(string target)
    {
        var marker = ReadMarker(Path.Combine(target, MarkerFileName));
        var revision = 1;
        // Each retry records progress in a new marker, preserving existing files.
        while (marker?.Outcome == nameof(LegacyImportOutcome.Partial))
        {
            var next = Path.Combine(target, RetryMarkerName(revision));
            if (!File.Exists(next)) break;
            marker = ReadMarker(next);
            revision++;
        }
        if (marker is not { Source: { } recordedSource, FailedFiles.Count: > 0 } ||
            !string.Equals(marker.Outcome, nameof(LegacyImportOutcome.Partial), StringComparison.Ordinal))
            return null;

        string source;
        try { source = Path.GetFullPath(recordedSource); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (SamePath(source, target) || !Directory.Exists(source) ||
            !_candidateSources().Any(candidate => SamePath(Path.GetFullPath(candidate), source))) return null;

        int copied = 0, skipped = 0;
        var indexSkipped = marker.IndexSkipped;
        var failed = new List<string>();
        foreach (var name in marker.FailedFiles)
        {
            // Names come from a file; one that points outside the two data folders is ignored.
            if (!TryNormalize(source, target, name, out var relative)) continue;
            // A file that is gone from the old folder cannot be retried; one that exists in the new folder is kept.
            if (!File.Exists(Path.Combine(target, relative)) && !File.Exists(Path.Combine(source, relative))) continue;
            switch (CopyFile(source, target, relative))
            {
                case CopyResult.Copied: copied++; break;
                case CopyResult.Skipped: skipped++; break;
                case CopyResult.IndexSkipped: indexSkipped = true; break;
                default: failed.Add(relative); break;
            }
        }

        var outcome = failed.Count > 0 ? LegacyImportOutcome.Partial : LegacyImportOutcome.Imported;
        var result = new LegacyImportResult(outcome, source, copied, skipped, failed) { IndexSkipped = indexSkipped };
        // Rewrite the marker only when the list of failed names changed.
        var unchanged = failed.Count == marker.FailedFiles.Count &&
                        failed.All(f => marker.FailedFiles.Contains(f, StringComparer.OrdinalIgnoreCase));
        if (!unchanged)
            WriteMarker(target, new LegacyImportResult(outcome, source, marker.Copied + copied, marker.Skipped + skipped, failed)
                { IndexSkipped = indexSkipped }, RetryMarkerName(revision));
        return result;
    }

    private static string RetryMarkerName(int revision) => $"{RetryMarkerPrefix}{revision}.json";

    /// <summary>True for a plain file name, or a file directly inside one of <see cref="Subfolders"/>, that stays inside both folders.</summary>
    private static bool TryNormalize(string source, string target, string name, out string relative)
    {
        relative = "";
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name)) return false;
        var parts = name.Split('\\', '/');
        if (parts.Length > 2 || parts.Any(part => part is "" or "." or "..")) return false;
        if (parts.Length == 2 && !Subfolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return false;
        var leaf = parts[^1];
        if (leaf.EndsWith(TemporarySuffix, StringComparison.OrdinalIgnoreCase) ||
            IsMarkerName(leaf) ||
            leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        relative = Path.Combine(parts);
        return !IsIndexSidecar(relative) && Inside(source, Path.Combine(source, relative)) &&
               Inside(target, Path.Combine(target, relative));
    }

    private static bool Inside(string folder, string path) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private sealed record MarkerData(string? Outcome, string? Source, int Copied, int Skipped, bool IndexSkipped,
        IReadOnlyList<string> FailedFiles);

    private static MarkerData? ReadMarker(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            int Count(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                    ? number : 0;
            var failed = new List<string>();
            if (root.TryGetProperty("failedFiles", out var names) && names.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in names.EnumerateArray())
                    if (name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text) failed.Add(text);
            }
            var indexSkipped = root.TryGetProperty("indexSkipped", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new MarkerData(Text("outcome"), Text("source"), Count("copied"), Count("skipped"), indexSkipped, failed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable marker counts as a finished import, as before.
            return null;
        }
    }

    /// <summary>
    /// Copies the cost index as a consistent snapshot: the old index is opened read-only and <c>VACUUM INTO</c> writes
    /// a new file, so a running old app cannot tear the copy (a byte-wise copy of the file and its -wal/-shm could).
    /// When that fails the index is left out and the scanner rebuilds it from the logs.
    /// </summary>
    private CopyResult CopyIndexSnapshot(string sourceFile, string targetFile)
    {
        if (File.Exists(targetFile)) return CopyResult.Skipped;
        if (IndexSidecarSuffixes.Any(suffix => File.Exists(targetFile + suffix))) return CopyResult.IndexSkipped;
        var temporary = TemporaryName(targetFile);
        var ownsTemporary = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            if (!CanOpenIndexReadOnly(sourceFile)) return CopyResult.IndexSkipped;
            using (File.Open(temporary, FileMode.CreateNew)) ownsTemporary = true;
            using (var database = new Native.Cost.CostIndexDatabase(sourceFile, readOnly: true))
            {
                database.Execute("PRAGMA busy_timeout=5000");
                using var snapshot = database.Prepare("VACUUM INTO ?");
                snapshot.Bind(1, temporary);
                snapshot.Write();
            }
            File.Move(temporary, targetFile, overwrite: false);
            return CopyResult.Copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (ownsTemporary) TryDelete(temporary);
            if (File.Exists(targetFile)) return CopyResult.Skipped; // created meanwhile; never overwritten
            _logger.LogWarning("Could not copy the old cost index ({Error}); the scanner rebuilds it", ex.GetType().Name);
            return CopyResult.IndexSkipped;
        }
    }

    private static bool CanOpenIndexReadOnly(string path)
    {
        // A read-only SQLite connection can still initialize WAL shared memory. Only open a WAL
        // database when its sidecars already exist, and leave stale WAL files on rollback databases alone.
        using var header = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        header.Position = 18; // SQLite file format write and read versions.
        var writeVersion = header.ReadByte();
        var readVersion = header.ReadByte();
        var hasWal = File.Exists(path + "-wal");
        var hasSharedMemory = File.Exists(path + "-shm");
        return writeVersion == 2 && readVersion == 2
            ? hasWal && hasSharedMemory
            : writeVersion == 1 && readVersion == 1 && !hasWal && !hasSharedMemory;
    }

    /// <summary>Copies through a temporary name, so a half-written file never takes the real name.</summary>
    private CopyResult CopyOne(string sourceFile, string targetFile)
    {
        if (File.Exists(targetFile)) return CopyResult.Skipped;
        var temporary = TemporaryName(targetFile);
        var ownsTemporary = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            using (var source = File.OpenRead(sourceFile))
            using (var destination = File.Open(temporary, FileMode.CreateNew))
            {
                ownsTemporary = true;
                source.CopyTo(destination);
            }
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(sourceFile));
            File.Move(temporary, targetFile, overwrite: false);
            return CopyResult.Copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ownsTemporary) TryDelete(temporary);
            if (File.Exists(targetFile)) return CopyResult.Skipped; // created meanwhile; never overwritten
            _logger.LogWarning("Could not import {File}: {Error}", Path.GetFileName(targetFile), ex.GetType().Name);
            return CopyResult.Failed;
        }
    }

    private static string TemporaryName(string path) => $"{path}.{Guid.NewGuid():N}{TemporarySuffix}";

    private void WriteMarker(string target, LegacyImportResult result, string markerName = MarkerFileName)
    {
        var marker = Path.Combine(target, markerName);
        var temporary = TemporaryName(marker);
        var ownsTemporary = false;
        try
        {
            Directory.CreateDirectory(target);
            using (var stream = File.Open(temporary, FileMode.CreateNew))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                ownsTemporary = true;
                writer.WriteStartObject();
                writer.WriteString("outcome", result.Outcome.ToString());
                writer.WriteString("source", result.SourceDirectory);
                writer.WriteNumber("copied", result.Copied);
                writer.WriteNumber("skipped", result.Skipped);
                writer.WriteNumber("failed", result.FailedFiles.Count);
                // The names let the next start retry exactly these files and nothing else.
                writer.WriteStartArray("failedFiles");
                foreach (var name in result.FailedFiles) writer.WriteStringValue(name);
                writer.WriteEndArray();
                writer.WriteBoolean("indexSkipped", result.IndexSkipped);
                writer.WriteString("at", DateTimeOffset.UtcNow);
                writer.WriteEndObject();
            }
            File.Move(temporary, marker, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ownsTemporary) TryDelete(temporary);
            // Without a marker the next start checks again; settings.json then stops a second copy.
            _logger.LogWarning("Could not write the import marker: {Error}", ex.GetType().Name);
        }
    }

    private void Report(LegacyImportResult result)
    {
        switch (result.Outcome)
        {
            case LegacyImportOutcome.Imported:
                _logger.LogInformation("Imported old data: {Copied} file(s) copied, {Skipped} already present",
                    result.Copied, result.Skipped);
                break;
            case LegacyImportOutcome.Partial:
            case LegacyImportOutcome.Failed:
                var message = $"Import of old data {(result.Outcome == LegacyImportOutcome.Partial ? "was partial" : "failed")}: " +
                              $"{result.Copied} copied, {result.Skipped} already present, {result.FailedFiles.Count} failed " +
                              $"({string.Join(", ", result.FailedFiles)})";
                _logger.LogWarning("{Message}", message);
                _reportFailure?.Invoke("LegacyDataImporter", new IOException(message));
                break;
            default:
                _logger.LogDebug("Import of old data: {Outcome}", result.Outcome);
                break;
        }
    }

    private static DateTime LastWritten(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
