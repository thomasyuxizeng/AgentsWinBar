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
}

/// <summary>
/// One-time import of the data the app wrote under its old name (CodexBarWin). Runs at startup before settings and
/// caches load. When the data folder has no settings.json, it copies the files of the first old folder that has one:
/// the old packaged app's LocalCache (most recently written first), then the old unpackaged folder. Existing files
/// are never overwritten and the source is never changed. settings.json is copied last, so an interrupted import is
/// retried at the next start; a marker file records a finished import so it runs once.
/// </summary>
public sealed class LegacyDataImporter
{
    public const string MarkerFileName = "legacy-import.json";
    internal const string SettingsFileName = "settings.json";
    private const string TemporarySuffix = ".import.tmp";

    /// <summary>Subfolders copied besides the top-level files: the native cost index (index.sqlite, -wal, -shm).</summary>
    internal static readonly string[] Subfolders = [Native.NativeCostService.IndexFolderName];

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
            return new LegacyImportResult(LegacyImportOutcome.AlreadyDone);

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
        var failed = new List<string>();

        // settings.json goes last: until it is in place, the next start retries (existing files are skipped).
        var files = Directory.EnumerateFiles(source)
            .Select(f => Path.GetFileName(f))
            .Where(name => !name.EndsWith(TemporarySuffix, StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(name, MarkerFileName, StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(name, SettingsFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var sub in Subfolders)
        {
            var folder = Path.Combine(source, sub);
            if (!Directory.Exists(folder)) continue;
            files.AddRange(Directory.EnumerateFiles(folder)
                .Select(f => Path.Combine(sub, Path.GetFileName(f)))
                .Where(name => !name.EndsWith(TemporarySuffix, StringComparison.OrdinalIgnoreCase)));
        }
        files.Add(SettingsFileName);

        foreach (var relative in files)
        {
            switch (CopyOne(Path.Combine(source, relative), Path.Combine(target, relative)))
            {
                case CopyResult.Copied: copied++; break;
                case CopyResult.Skipped: skipped++; break;
                default: failed.Add(relative); break;
            }
        }

        var settingsCopied = File.Exists(Path.Combine(target, SettingsFileName));
        var outcome = !settingsCopied ? LegacyImportOutcome.Failed
            : failed.Count > 0 ? LegacyImportOutcome.Partial
            : LegacyImportOutcome.Imported;
        var result = new LegacyImportResult(outcome, source, copied, skipped, failed);
        if (outcome != LegacyImportOutcome.Failed) WriteMarker(target, result);
        return result;
    }

    private enum CopyResult { Copied, Skipped, Failed }

    /// <summary>Copies through a temporary name, so a half-written file never takes the real name.</summary>
    private CopyResult CopyOne(string sourceFile, string targetFile)
    {
        if (File.Exists(targetFile)) return CopyResult.Skipped;
        var temporary = targetFile + TemporarySuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, temporary, overwrite: true);
            File.Move(temporary, targetFile, overwrite: false);
            return CopyResult.Copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            if (File.Exists(targetFile)) return CopyResult.Skipped; // created meanwhile; never overwritten
            _logger.LogWarning("Could not import {File}: {Error}", Path.GetFileName(targetFile), ex.GetType().Name);
            return CopyResult.Failed;
        }
    }

    private void WriteMarker(string target, LegacyImportResult result)
    {
        try
        {
            Directory.CreateDirectory(target);
            using var stream = File.Create(Path.Combine(target, MarkerFileName));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("outcome", result.Outcome.ToString());
            writer.WriteString("source", result.SourceDirectory);
            writer.WriteNumber("copied", result.Copied);
            writer.WriteNumber("skipped", result.Skipped);
            writer.WriteNumber("failed", result.FailedFiles.Count);
            writer.WriteString("at", DateTimeOffset.UtcNow);
            writer.WriteEndObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
