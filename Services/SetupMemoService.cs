using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

/// <summary>Best-effort atomic persistence of a setup check beside application settings.</summary>
public sealed partial class SetupMemoService : ISetupMemoService
{
    private readonly ILogger<SetupMemoService> _logger;
    private readonly TimeProvider _clock;
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public string FilePath { get; }

    public SetupMemoService(ISettingsService settings, ILogger<SetupMemoService> logger,
        TimeProvider? timeProvider = null, string? filePath = null)
    {
        _logger = logger;
        _settings = settings;
        _clock = timeProvider ?? TimeProvider.System;
        FilePath = filePath ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(settings.SettingsFilePath))!, "setup-ok.json");
    }

    public async Task<bool> IsValidAsync(CancellationToken ct = default)
    {
        if (_settings.Settings?.DeveloperModeEnabled == true) return false;
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct);
            acquired = true;
            if (_settings.Settings?.DeveloperModeEnabled == true) return false;
            if (!File.Exists(FilePath)) return false;
            var json = await File.ReadAllTextAsync(FilePath, ct);
            var entry = JsonSerializer.Deserialize(json, SetupMemoJsonContext.Default.SetupMemoEntry);
            if (entry == null || string.IsNullOrWhiteSpace(entry.CliVersion)) return false;
            var age = _clock.GetUtcNow() - entry.CheckedAtUtc;
            if (age < TimeSpan.Zero || age >= TimeSpan.FromHours(24)) return false;
            var match = VersionRegex().Match(entry.CliVersion);
            return match.Success && Version.TryParse(match.Groups[1].Value, out var version)
                && version >= SetupChecker.MinCodexBarVersion;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Successful setup memo could not be loaded");
            return false;
        }
        finally { if (acquired) _ioGate.Release(); }
    }

    public async Task SaveAsync(string? version, CancellationToken ct = default)
    {
        if (_settings.Settings?.DeveloperModeEnabled == true) return;
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct);
            acquired = true;
            if (_settings.Settings?.DeveloperModeEnabled == true) return;
            var entry = new SetupMemoEntry { CliVersion = version, CheckedAtUtc = _clock.GetUtcNow() };
            var json = JsonSerializer.Serialize(entry, SetupMemoJsonContext.Default.SetupMemoEntry);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var temporaryPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json, ct);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Successful setup memo could not be saved"); }
        finally { if (acquired) _ioGate.Release(); }
    }

    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        if (_settings.Settings?.DeveloperModeEnabled == true) return;
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct);
            acquired = true;
            if (_settings.Settings?.DeveloperModeEnabled == true) return;
            File.Delete(FilePath);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Successful setup memo could not be invalidated"); }
        finally { if (acquired) _ioGate.Release(); }
    }

    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex VersionRegex();
}

internal sealed record SetupMemoEntry
{
    public string? CliVersion { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(SetupMemoEntry))]
internal partial class SetupMemoJsonContext : JsonSerializerContext
{
}
