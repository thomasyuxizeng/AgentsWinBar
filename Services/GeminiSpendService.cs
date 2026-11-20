using System.Text;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Reads this month's Gemini API spend from the local spend log.
/// Tolerates concurrent append writes, missing log files, and large logs.
/// </summary>
public sealed class GeminiSpendService : IGeminiSpendService, IDisposable
{
    public const string SpendLogEnvironmentVariable = "CODEXBAR_GEMINI_SPEND_LOG";
    private const int MaxReadBytes = 8 * 1024 * 1024; // 8 MiB

    private readonly ISettingsService _settings;
    private readonly ILogger<GeminiSpendService> _logger;
    private readonly TimeProvider _clock;
    private readonly Func<string, EnvironmentVariableTarget, string?> _readEnvironment;
    private readonly Func<string, Stream>? _fileOpener;
    private readonly TimeZoneInfo _zone;
    private readonly Func<string, (bool Exists, long Length, DateTime LastWriteUtc)>? _fileInfoReader;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GeminiApiSpendInfo? _cached;
    private string? _cachedPath;
    private long _cachedLength;
    private DateTime _cachedLastWriteUtc;
    private DateOnly _cachedMonthStart;

    public TimeZoneInfo Zone => _zone;

    public GeminiSpendService(
        ISettingsService settings,
        ILogger<GeminiSpendService> logger,
        TimeProvider? timeProvider = null,
        Func<string, EnvironmentVariableTarget, string?>? environmentReader = null,
        Func<string, Stream>? fileOpener = null,
        TimeZoneInfo? timeZone = null,
        Func<string, (bool Exists, long Length, DateTime LastWriteUtc)>? fileInfoReader = null)
    {
        _settings = settings;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        _readEnvironment = environmentReader ?? Environment.GetEnvironmentVariable;
        _fileOpener = fileOpener;
        _zone = timeZone ?? TimeZoneInfo.Local;
        _fileInfoReader = fileInfoReader;
    }

    public async Task<GeminiApiSpendInfo> GetAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _zone);
        var currentMonthStart = new DateOnly(localNow.Year, localNow.Month, 1);
        var monthEnd = currentMonthStart.AddMonths(1);
        var localDate = DateOnly.FromDateTime(localNow);
        var daysLeft = Math.Max(1, monthEnd.DayNumber - localDate.DayNumber);

        if (_settings.Settings.DeveloperModeEnabled)
        {
            return new GeminiApiSpendInfo
            {
                State = GeminiApiSpendState.Ok,
                MonthStart = currentMonthStart,
                MonthEnd = monthEnd,
                DaysLeft = daysLeft,
                TotalUsd = 24.90m,
                AiStudioUsd = 8.30m,
                VertexUsd = 12.45m,
                UnrecordedRouteUsd = 4.15m,
                Calls = 142,
                Models =
                [
                    new() { Model = "gemini-4-neon", Usd = 18.20m, Calls = 98 },
                    new() { Model = "gemini-4-carbon", Usd = 6.70m, Calls = 44 }
                ],
                LastCallUtc = nowUtc.AddMinutes(-12),
                FetchedAtUtc = nowUtc,
                Message = null
            };
        }

        var entered = false;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;

            nowUtc = _clock.GetUtcNow().UtcDateTime;
            localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _zone);
            currentMonthStart = new DateOnly(localNow.Year, localNow.Month, 1);
            monthEnd = currentMonthStart.AddMonths(1);
            localDate = DateOnly.FromDateTime(localNow);
            daysLeft = Math.Max(1, monthEnd.DayNumber - localDate.DayNumber);

            var path = ResolvePath();
            var (exists, length, lastWriteUtc) = ProbeFile(path, nowUtc);

            if (!exists)
            {
                _cached = null;
                return GeminiSpendCalculator.BuildNoLog(nowUtc, _zone);
            }

            // Cache check: file length + last-write time; invalidate when the month changes
            if (_cached != null &&
                string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                _cachedLength == length &&
                _cachedLastWriteUtc == lastWriteUtc &&
                _cachedMonthStart == currentMonthStart)
            {
                return _cached with { DaysLeft = daysLeft };
            }

            ct.ThrowIfCancellationRequested();
            var text = await ReadFileContentAsync(path, length, ct).ConfigureAwait(false);

            var result = GeminiSpendCalculator.Build(text, nowUtc, _zone, nowUtc);
            _cached = result;
            _cachedPath = path;
            _cachedLength = length;
            _cachedLastWriteUtc = lastWriteUtc;
            _cachedMonthStart = currentMonthStart;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Gemini spend log could not be read");
            return GeminiSpendCalculator.BuildUnavailable(nowUtc, _zone, "Gemini spend log could not be read");
        }
        catch (Exception)
        {
            _logger.LogWarning("Gemini spend log could not be read");
            return GeminiSpendCalculator.BuildUnavailable(nowUtc, _zone, "Gemini spend log could not be read");
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private string ResolvePath()
    {
        var path = _readEnvironment(SpendLogEnvironmentVariable, EnvironmentVariableTarget.Process);
        if (string.IsNullOrWhiteSpace(path))
            path = _readEnvironment(SpendLogEnvironmentVariable, EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(path))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(userProfile, ".claude", "gemini-api", "spend.jsonl");
        }
        return path;
    }

    private (bool Exists, long Length, DateTime LastWriteUtc) ProbeFile(string path, DateTime nowUtc)
    {
        if (_fileInfoReader != null)
        {
            return _fileInfoReader(path);
        }

        if (_fileOpener == null)
        {
            var info = new FileInfo(path);
            return (info.Exists, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
        }

        var diskInfo = new FileInfo(path);
        if (diskInfo.Exists)
        {
            return (true, diskInfo.Length, diskInfo.LastWriteTimeUtc);
        }

        // Virtual file provided through test seam
        try
        {
            using var stream = _fileOpener(path);
            if (stream != null)
            {
                return (true, stream.Length, nowUtc);
            }
            return (false, 0, DateTime.MinValue);
        }
        catch (FileNotFoundException)
        {
            return (false, 0, DateTime.MinValue);
        }
        catch (DirectoryNotFoundException)
        {
            return (false, 0, DateTime.MinValue);
        }
    }

    private async Task<string> ReadFileContentAsync(string path, long fileLength, CancellationToken ct)
    {
        Stream stream;
        var ownsStream = false;
        if (_fileOpener != null)
        {
            var opened = _fileOpener(path);
            if (opened == null) throw new FileNotFoundException("File not found", path);
            stream = opened;
            ownsStream = true;
        }
        else
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ownsStream = true;
        }

        try
        {
            if (stream.CanSeek && stream.Length > MaxReadBytes)
            {
                stream.Seek(stream.Length - MaxReadBytes, SeekOrigin.Begin);
                var buffer = new byte[MaxReadBytes];
                var totalRead = 0;
                while (totalRead < MaxReadBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = await stream.ReadAsync(buffer.AsMemory(totalRead, MaxReadBytes - totalRead), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    totalRead += read;
                }
                var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
                var firstNewline = text.IndexOf('\n');
                return firstNewline >= 0 ? text.Substring(firstNewline + 1) : string.Empty;
            }
            else
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: !ownsStream);
                return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownsStream)
            {
                stream.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
