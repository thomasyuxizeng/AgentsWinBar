using System.Text.Json;
using AgentsWinBar.Helpers;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services.Native;

/// <summary>Keeps Claude's "blocked until" time after a 429 so a restart cannot call the endpoint during a cooldown.</summary>
public interface IClaudeCooldownStore
{
    /// <summary>The saved "blocked until" time, or null when none is saved or the file cannot be read.</summary>
    DateTimeOffset? Load();

    /// <summary>Saves the "blocked until" time. Never throws for IO failures.</summary>
    Task SaveAsync(DateTimeOffset blockedUntil, CancellationToken ct = default);
}

/// <summary>
/// Stores the cooldown as {"blockedUntil": "..."} in claude-cooldown.json in the app's local data folder, beside
/// cache.json. Holds no sign-in data.
/// </summary>
public sealed class ClaudeCooldownFile : IClaudeCooldownStore
{
    public const string FileName = "claude-cooldown.json";
    private readonly ILogger<ClaudeCooldownFile> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClaudeCooldownFile(ILogger<ClaudeCooldownFile> logger, string? filePath = null)
    {
        _logger = logger;
        FilePath = filePath ?? AppPaths.DataFile(FileName);
    }

    public string FilePath { get; }

    /// <summary>The file holds one short timestamp; anything larger is damaged and is not read.</summary>
    internal const int MaxBytes = 64 * 1024;

    public DateTimeOffset? Load()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists) return null;
            if (info.Length > MaxBytes)
            {
                _logger.LogDebug("Claude cooldown file is larger than expected; ignoring it");
                return null;
            }
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[MaxBytes + 1];
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > MaxBytes) return null;
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("blockedUntil", out var value) &&
                value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var blockedUntil))
                return blockedUntil;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug("Claude cooldown file could not be read ({Type})", ex.GetType().Name);
            return null;
        }
    }

    public async Task SaveAsync(DateTimeOffset blockedUntil, CancellationToken ct = default)
    {
        var acquired = false;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("blockedUntil", blockedUntil.ToUniversalTime());
                writer.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var temporaryPath = FilePath + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, buffer.ToArray(), ct).ConfigureAwait(false);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogWarning("Claude cooldown could not be saved ({Type})", ex.GetType().Name);
        }
        finally { if (acquired) _gate.Release(); }
    }
}
