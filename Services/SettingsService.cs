using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

/// <summary>
/// Service for managing application settings.
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private AppSettings _settings = AppSettings.GetDefaults();
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public AppSettings Settings => _settings;
    public string SettingsFilePath { get; }

    public event EventHandler? SettingsChanged;

    public SettingsService(ILogger<SettingsService> logger, string? settingsDirectory = null)
    {
        _logger = logger;

        var appFolder = settingsDirectory ?? AppPaths.DataDirectory;
        Directory.CreateDirectory(appFolder);
        SettingsFilePath = Path.Combine(appFolder, "settings.json");
    }

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                _logger.LogInformation("Settings file not found, using defaults");
                return;
            }

            var json = await File.ReadAllTextAsync(SettingsFilePath);
            var settings = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings);

            if (settings != null)
            {
                // Filter out any invalid providers for security
                var originalCount = settings.Providers?.Count ?? 0;
                settings.Normalize();

                if (settings.Providers!.Count < originalCount)
                {
                    _logger.LogWarning("Filtered {Count} invalid provider(s) from settings",
                        originalCount - settings.Providers.Count);
                }

                _settings = settings;
                _logger.LogInformation("Settings loaded from {Path}", SettingsFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
        }
    }

    public async Task SaveAsync()
    {
        await PersistAsync();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveQuietAsync()
    {
        try { await PersistAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to quietly save settings"); }
    }

    private async Task PersistAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            _settings.Normalize();
            var json = System.Text.Json.JsonSerializer.Serialize(_settings, AppJsonSerializerContext.Default.AppSettings);
            // Serialize saves and replace only after the complete file has reached disk.
            var temporaryPath = SettingsFilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
            _logger.LogInformation("Settings saved to {Path}", SettingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            throw;
        }
        finally { _saveGate.Release(); }
    }

    public void Reset()
    {
        _settings = AppSettings.GetDefaults();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Settings reset to defaults");
    }
}
