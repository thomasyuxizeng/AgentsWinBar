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

public enum ResetAnimationMode
{
    WhenNotBatterySaver = 0,
    Always = 1,
    Never = 2
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
    public int LockedRefreshIntervalMinutes { get; set; } = 30;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public ResetAnimationMode ResetAnimationMode { get; set; } = ResetAnimationMode.WhenNotBatterySaver;
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
    public decimal ApiCreditsMonthlyUsd { get; set; } = 200;
    public int ApiCreditsCycleStartDay { get; set; } = 1;

    /// <summary>
    /// Saved overlay position in screen pixels (null = default corner).
    /// </summary>
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }

    /// <summary>
    /// Usage source per provider id: "native" (default) or "wsl". Set in settings.json; files written before this
    /// setting existed load as "native" for every provider.
    /// </summary>
    public Dictionary<string, string> UsageSources { get; set; } = UsageSource.Defaults();

    /// <summary>The normalized usage source for a provider ("native" unless it is set to "wsl").</summary>
    public string GetUsageSource(string providerId)
    {
        var provider = ProviderConstants.ValidateAndNormalize(providerId);
        return UsageSources != null && UsageSources.TryGetValue(provider, out var value)
            ? UsageSource.Normalize(value)
            : UsageSource.Native;
    }

    /// <summary>True when the provider's usage is read natively.</summary>
    public bool UsesNativeUsage(string providerId) => GetUsageSource(providerId) == UsageSource.Native;

    /// <summary>
    /// True when every enabled provider reads usage natively, so usage needs neither WSL nor the CodexBar CLI.
    /// Also true when no provider is enabled.
    /// </summary>
    public bool AllEnabledUsageNative() => (Providers ?? [])
        .Where(p => p != null && p.IsEnabled && ProviderConstants.IsValidProvider(p.Id))
        .All(p => UsesNativeUsage(p.Id));

    /// <summary>
    /// Cost source for every provider: "native" (default) scans the Windows log folders in-process with an on-disk
    /// index; "wsl" runs the CodexBar CLI's cost scan inside WSL (kept as a fallback). Set in settings.json; files
    /// written before this setting existed load as "native".
    /// </summary>
    public string CostSource { get; set; } = UsageSource.Native;

    /// <summary>True when costs come from the native log scanner, so cost scans never start WSL.</summary>
    public bool UsesNativeCost() => UsageSource.Normalize(CostSource) == UsageSource.Native;

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
        UsageSources = NormalizeUsageSources(UsageSources);
        CostSource = UsageSource.Normalize(CostSource);
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        if (!Enum.IsDefined(ResetAnimationMode)) ResetAnimationMode = ResetAnimationMode.WhenNotBatterySaver;
        RefreshIntervalSeconds = ValidRange(RefreshIntervalSeconds, 86400, 120);
        if (LockedRefreshIntervalMinutes < 0 || LockedRefreshIntervalMinutes > 1440)
            LockedRefreshIntervalMinutes = 30;
        CacheExpiryMinutes = ValidRange(CacheExpiryMinutes, 1440, 5);
        if (ApiCreditsMonthlyUsd <= 0 || ApiCreditsMonthlyUsd > 100000) ApiCreditsMonthlyUsd = 200;
        if (ApiCreditsCycleStartDay < 1 || ApiCreditsCycleStartDay > 28) ApiCreditsCycleStartDay = 1;
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

    /// <summary>
    /// Keeps one entry per allowed provider: invalid provider keys are dropped, missing providers and unknown values
    /// become "native", and "WSL" in any case becomes "wsl".
    /// </summary>
    private static Dictionary<string, string> NormalizeUsageSources(Dictionary<string, string>? stored)
    {
        var result = UsageSource.Defaults();
        foreach (var (key, value) in stored ?? [])
        {
            if (!ProviderConstants.IsValidProvider(key)) continue;
            var provider = ProviderConstants.ValidateAndNormalize(key);
            // A "wsl" entry wins over a duplicate key that differs only in case.
            if (result[provider] != UsageSource.Wsl) result[provider] = UsageSource.Normalize(value);
        }
        return result;
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
        LockedRefreshIntervalMinutes = 30,
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
        ShowCostTotals = true,
        ApiCreditsMonthlyUsd = 200,
        ApiCreditsCycleStartDay = 1
    };
}
