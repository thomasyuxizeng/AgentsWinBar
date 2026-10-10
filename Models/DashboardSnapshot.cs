using CodexBarWin.Helpers;

namespace CodexBarWin.Models;

/// <summary>
/// Everything the full-window dashboard shows, computed in one pass from the app's live state.
/// This is the contract between the data layer (<c>DashboardSnapshotBuilder</c> / <c>IDashboardService</c>)
/// and the dashboard window: the window only formats and draws these values, it never recomputes them.
/// All timestamps are UTC; the window converts them to local time for display.
/// </summary>
public sealed record DashboardSnapshot
{
    public DateTime GeneratedAtUtc { get; init; }
    public DashboardSummary Summary { get; init; } = new();
    public IReadOnlyList<DashboardProvider> Providers { get; init; } = [];
    public DashboardDiagnostics Diagnostics { get; init; } = new();
}

/// <summary>Cross-provider headline numbers shown in the dashboard's top strip.</summary>
public sealed record DashboardSummary
{
    public DateTime? LastRefreshUtc { get; init; }
    public DateTime? NextRefreshUtc { get; init; }
    public int RefreshIntervalSeconds { get; init; }
    public int EffectiveRefreshIntervalSeconds { get; init; }

    public int ProviderCount { get; init; }

    /// <summary>Providers with data, no error and not stale.</summary>
    public int HealthyProviderCount { get; init; }
    public int ErrorProviderCount { get; init; }
    public int StaleProviderCount { get; init; }

    /// <summary>Providers whose service status is an incident (minor, major, critical or maintenance).</summary>
    public int IncidentCount { get; init; }

    public int WindowCount { get; init; }
    public int WindowsAtLimit { get; init; }          // used >= UsageThresholds.Critical (90)
    public int WindowsNearLimit { get; init; }        // 75 <= used < 90
    public int WindowsAheadOfPace { get; init; }      // pace says the limit is hit before the reset

    /// <summary>The window closest to its limit (highest used percent), if any.</summary>
    public DashboardWindowRef? MostUsedWindow { get; init; }

    /// <summary>The window that resets soonest, if any.</summary>
    public DashboardWindowRef? NextResetWindow { get; init; }

    public bool CostAvailable { get; init; }
    public string CostCurrency { get; init; } = "USD";
    public decimal TotalCostToday { get; init; }
    public decimal TotalCost30Days { get; init; }
    public long TotalTokensToday { get; init; }
    public long TotalTokens30Days { get; init; }
}

/// <summary>A pointer to one window, for headline callouts.</summary>
public sealed record DashboardWindowRef
{
    public string ProviderId { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public double UsedPercent { get; init; }
    public DateTime? ResetsAtUtc { get; init; }
}

/// <summary>One provider's complete card: identity, health, every window, cost and history.</summary>
public sealed record DashboardProvider
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Plan { get; init; }

    /// <summary>Where the numbers came from (CLI source string, e.g. "oauth", "codex-cli", "cli").</summary>
    public string? Source { get; init; }

    public ProviderStatus? ServiceStatus { get; init; }
    public bool IsLoading { get; init; }
    public bool IsStale { get; init; }
    public bool UsageIsStale { get; init; }
    /// <summary>Whether the retained service status came from an earlier fetch, independent of usage.</summary>
    public bool StatusIsStale { get; init; }
    /// <summary>The oldest last-success timestamp among stale kinds, or the usage timestamp when fresh.</summary>
    public DateTime? LastSuccessfulFetchUtc { get; init; }
    public DateTime? LastSuccessfulUsageFetchUtc { get; init; }
    /// <summary>The original status fetch time; null when status is missing or beyond seven-day retention.</summary>
    public DateTime? LastSuccessfulStatusFetchUtc { get; init; }
    public TimeSpan? StaleAge { get; init; }
    public string? Error { get; init; }
    public DateTime FetchedAtUtc { get; init; }
    /// <summary>The reading's own time when it is much older than the fetch (a cached result); otherwise null.</summary>
    public DateTime? OldReadingUtc { get; init; }
    public CreditsInfo? Credits { get; init; }

    /// <summary>Session, weekly, tertiary, then every extra window, in that order.</summary>
    public IReadOnlyList<DashboardWindow> Windows { get; init; } = [];

    public DashboardCost? Cost { get; init; }

    public int HistorySampleCount { get; init; }
    public DateTime? HistoryOldestUtc { get; init; }
}

public enum DashboardWindowKind
{
    Session,
    Weekly,
    Tertiary,
    Extra
}

/// <summary>One usage window with its pace analysis and burndown history already worked out.</summary>
public sealed record DashboardWindow
{
    /// <summary>History key: "session", "weekly", "tertiary" or "extra:&lt;id&gt;".</summary>
    public string Key { get; init; } = string.Empty;
    public DashboardWindowKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public WindowAccent Accent { get; init; }

    public double UsedPercent { get; init; }

    /// <summary>How far through the window we are (0-100), or null when unknown.</summary>
    public double? ElapsedPercent { get; init; }
    public int WindowMinutes { get; init; }
    public DateTime? ResetsAtUtc { get; init; }

    /// <summary>"Resets in 4h 59m" style text, computed at snapshot time.</summary>
    public string ResetText { get; init; } = string.Empty;
    public DateTime? LastChangedAtUtc { get; init; }

    public PaceProjection Pace { get; init; } = new(PaceKind.Unknown);
    public string PaceSummary { get; init; } = string.Empty;

    /// <summary>Burndown for the current window (usage line, even-use guide, projection).</summary>
    public BurndownSeries History { get; init; } = new();
}

/// <summary>Local-log cost and token totals for one provider, plus derived figures.</summary>
public sealed record DashboardCost
{
    public bool IsStale { get; init; }
    public DateTime? LastSuccessfulFetchUtc { get; init; }
    public TimeSpan? StaleAge { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal TodayCost { get; init; }
    public long TodayTokens { get; init; }
    public decimal Last30DaysCost { get; init; }
    public long Last30DaysTokens { get; init; }

    /// <summary>"listPriceEstimate" and similar: how the costs were derived.</summary>
    public string? Provenance { get; init; }
    public DateTime CapturedAtUtc { get; init; }
    public CostCoverage Coverage { get; init; } = new();

    // Token breakdown summed over the 30-day history
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long ReasoningTokens { get; init; }

    /// <summary>One entry per calendar day for the last 30 days, zero-filled, oldest first.</summary>
    public IReadOnlyList<DailyCost> Daily { get; init; } = [];

    /// <summary>Models by cost over the 30 days (unpriced models last), up to eight.</summary>
    public IReadOnlyList<ModelCost> TopModels { get; init; } = [];

    /// <summary>Average cost per day over the days that had any usage (null when none).</summary>
    public decimal? AverageDailyCost { get; init; }
    public DailyCost? PeakDay { get; init; }
}

/// <summary>App-level facts for the dashboard's diagnostics panel.</summary>
public sealed record DashboardDiagnostics
{
    public string? AppVersion { get; init; }
    public string? CliVersion { get; init; }
    public bool? CliAvailable { get; init; }
    public string? WslDistro { get; init; }
    public string SettingsPath { get; init; } = string.Empty;

    public string Theme { get; init; } = string.Empty;
    public int RefreshIntervalSeconds { get; init; }
    public int EffectiveRefreshIntervalSeconds { get; init; }
    public int CacheExpiryMinutes { get; init; }
    public int WslCommandTimeoutSeconds { get; init; }
    public int CliProviderTimeoutSeconds { get; init; }
    public int StandardProviderTimeoutSeconds { get; init; }

    public bool OverlayEnabled { get; init; }
    public bool TaskbarStripEnabled { get; init; }
    public bool AlertsEnabled { get; init; }
    public bool CostTotalsEnabled { get; init; }
    public bool DeveloperModeEnabled { get; init; }
    public bool StartWithWindows { get; init; }

    public int HistorySampleCount { get; init; }
    public DateTime? HistoryOldestUtc { get; init; }
    public DateTime? HistoryNewestUtc { get; init; }
    public long HistoryFileBytes { get; init; }

    /// <summary>Most recent first, capped (about 40).</summary>
    public IReadOnlyList<DashboardRefreshEntry> RecentRefreshes { get; init; } = [];

    /// <summary>Alerts raised this session, most recent first, capped (about 20).</summary>
    public IReadOnlyList<DashboardAlertEntry> RecentAlerts { get; init; } = [];

    public IReadOnlyList<DashboardCostCacheEntry> CostCache { get; init; } = [];
}

/// <summary>The outcome of fetching one provider during a refresh.</summary>
public sealed record DashboardRefreshEntry
{
    public DateTime TimestampUtc { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public int DurationMs { get; init; }

    /// <summary>The error text when the fetch failed; null on success.</summary>
    public string? Message { get; init; }
}

public sealed record DashboardAlertEntry
{
    public DateTime TimestampUtc { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed record DashboardCostCacheEntry
{
    public string ProviderId { get; init; } = string.Empty;
    public DateTime? LastAttemptUtc { get; init; }
    public bool HasData { get; init; }
}
