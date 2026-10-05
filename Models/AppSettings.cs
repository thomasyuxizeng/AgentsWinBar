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
    private const string DefaultGuideUrl = "https://github.com/Wave-OCE/CodexBarWin#quick-start";
    public int Version { get; set; } = 1;
    public List<ProviderConfig> Providers { get; set; } = [];
    public int RefreshIntervalSeconds { get; set; } = 120;
    public bool AdaptiveRefreshEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public int CacheExpiryMinutes { get; set; } = 5;
    public TimeoutSettings Timeouts { get; set; } = new();
    public AnimationSettings Animation { get; set; } = new();
    public string CodexBarGuideUrl { get; set; } = DefaultGuideUrl;
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

    public bool ShowCostTotals { get; set; } = true;

    /// <summary>
    /// Saved overlay position in screen pixels (null = default corner).
    /// </summary>
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }

    /// <summary>
    /// Repairs malformed persisted values before they reach timers, animations or shell commands.
    /// An explicitly empty provider list remains empty.
    /// </summary>
    public void Normalize()
    {
        Providers ??= ProviderConfig.GetDefaults().ToList();
        Providers = Providers
            .Where(p => p != null && ProviderConstants.IsValidProvider(p.Id))
            .Select(p => p with { Id = ProviderConstants.ValidateAndNormalize(p.Id) })
            .DistinctBy(p => p.Id)
            .ToList();
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        RefreshIntervalSeconds = ValidRange(RefreshIntervalSeconds, 86400, 120);
        CacheExpiryMinutes = ValidRange(CacheExpiryMinutes, 1440, 5);
        Timeouts ??= new();
        Timeouts.WslCommandTimeoutSeconds = ValidRange(Timeouts.WslCommandTimeoutSeconds, 3600, 30);
        Timeouts.WslStatusCheckTimeoutSeconds = ValidRange(Timeouts.WslStatusCheckTimeoutSeconds, 3600, 10);
        Timeouts.CliProviderFirstFetchTimeoutSeconds = ValidRange(Timeouts.CliProviderFirstFetchTimeoutSeconds, 3600, 60);
        Timeouts.CliProviderTimeoutSeconds = ValidRange(Timeouts.CliProviderTimeoutSeconds, 3600, 45);
        Timeouts.StandardProviderFirstFetchTimeoutSeconds = ValidRange(Timeouts.StandardProviderFirstFetchTimeoutSeconds, 3600, 20);
        Timeouts.StandardProviderTimeoutSeconds = ValidRange(Timeouts.StandardProviderTimeoutSeconds, 3600, 10);
        Animation ??= new();
        Animation.ShowDurationMs = ValidRange(Animation.ShowDurationMs, 10000, 150);
        Animation.HideDurationMs = ValidRange(Animation.HideDurationMs, 10000, 100);
        Animation.Steps = ValidRange(Animation.Steps, 120, 15);
        if (CodexBarGuideUrl == "https://github.com/steipete/CodexBar#installation" ||
            !Uri.TryCreate(CodexBarGuideUrl, UriKind.Absolute, out var guide) ||
            (guide.Scheme != Uri.UriSchemeHttps && guide.Scheme != Uri.UriSchemeHttp))
            CodexBarGuideUrl = DefaultGuideUrl;
    }

    private static int ValidRange(int value, int maximum, int fallback) =>
        value > 0 && value <= maximum ? value : fallback;

    /// <summary>
    /// Gets the default settings.
    /// </summary>
    public static AppSettings GetDefaults() => new()
    {
        Providers = ProviderConfig.GetDefaults().ToList(),
        RefreshIntervalSeconds = 120,
        AdaptiveRefreshEnabled = true,
        StartWithWindows = false,
        StartMinimized = true,
        Theme = AppTheme.System,
        CacheExpiryMinutes = 5,
        Timeouts = new TimeoutSettings(),
        Animation = new AnimationSettings(),
        CodexBarGuideUrl = DefaultGuideUrl,
        DeveloperModeEnabled = false,
        AlwaysOnOverlayEnabled = false,
        TaskbarDisplayEnabled = false,
        AlertsEnabled = true,
        ShowCostTotals = true
    };
}
