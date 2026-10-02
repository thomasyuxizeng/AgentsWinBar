using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>
/// Interface for settings management.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Gets the current settings.
    /// </summary>
    AppSettings Settings { get; }

    /// <summary>
    /// Gets the settings file path.
    /// </summary>
    string SettingsFilePath { get; }

    /// <summary>
    /// Loads settings from file.
    /// </summary>
    Task LoadAsync();

    /// <summary>
    /// Saves settings to file.
    /// </summary>
    Task SaveAsync();

    /// <summary>
    /// Saves settings to file without raising <see cref="SettingsChanged"/>
    /// (for UI-only state such as overlay position, which must not trigger a data refresh).
    /// </summary>
    Task SaveQuietAsync();

    /// <summary>
    /// Resets settings to defaults.
    /// </summary>
    void Reset();

    /// <summary>
    /// Event raised when settings change.
    /// </summary>
    event EventHandler? SettingsChanged;
}
