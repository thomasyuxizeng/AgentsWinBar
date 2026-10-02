namespace CodexBarWin.Models;

/// <summary>
/// Theme mode setting.
/// </summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// Timeout settings for various operations.
/// </summary>
public class TimeoutSettings
{
    public int WslCommandTimeoutSeconds { get; set; } = 30;
    public int WslStatusCheckTimeoutSeconds { get; set; } = 10;
    public int CliProviderFirstFetchTimeoutSeconds { get; set; } = 60;
    public int CliProviderTimeoutSeconds { get; set; } = 45;
    public int StandardProviderFirstFetchTimeoutSeconds { get; set; } = 20;
    public int StandardProviderTimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Animation settings for window transitions.
/// </summary>
public class AnimationSettings
{
    public int ShowDurationMs { get; set; } = 150;
    public int HideDurationMs { get; set; } = 100;
    public int Steps { get; set; } = 15;
}

/// <summary>
/// Application settings.
/// </summary>
public class AppSettings
{
    public int Version { get; set; } = 1;
    public List<ProviderConfig> Providers { get; set; } = [];
    public int RefreshIntervalSeconds { get; set; } = 120;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public int CacheExpiryMinutes { get; set; } = 5;
    public TimeoutSettings Timeouts { get; set; } = new();
    public AnimationSettings Animation { get; set; } = new();
    public string CodexBarGuideUrl { get; set; } = "https://github.com/steipete/CodexBar#installation";
    public bool DeveloperModeEnabled { get; set; } = false;

    /// <summary>
    /// Shows a minimal, always-visible usage overlay in addition to the tray flyout.
    /// </summary>
    public bool AlwaysOnOverlayEnabled { get; set; } = false;

    /// <summary>
    /// Shows a compact usage strip on the taskbar (bottom-left corner), independent of the floating overlay.
    /// </summary>
    public bool TaskbarDisplayEnabled { get; set; } = false;

    /// <summary>
    /// Shows a Windows notification when usage is far ahead of pace, nearly out, or at the limit.
    /// </summary>
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>
    /// Saved overlay position in screen pixels (null = default corner).
    /// </summary>
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }

    /// <summary>
    /// Gets the default settings.
    /// </summary>
    public static AppSettings GetDefaults() => new()
    {
        Providers = ProviderConfig.GetDefaults().ToList(),
        RefreshIntervalSeconds = 120,
        StartWithWindows = false,
        StartMinimized = true,
        Theme = AppTheme.System,
        CacheExpiryMinutes = 5,
        Timeouts = new TimeoutSettings(),
        Animation = new AnimationSettings(),
        CodexBarGuideUrl = "https://github.com/steipete/CodexBar#installation",
        DeveloperModeEnabled = false,
        AlwaysOnOverlayEnabled = false,
        TaskbarDisplayEnabled = false,
        AlertsEnabled = true
    };
}
