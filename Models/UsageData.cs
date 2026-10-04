namespace CodexBarWin.Models;

/// <summary>
/// Represents usage data for a provider.
/// </summary>
public record UsageData
{
    public string Provider { get; init; } = string.Empty;
    public string? Plan { get; init; }
    public UsageWindow? Session { get; init; }
    public UsageWindow? Weekly { get; init; }
    public UsageWindow? Tertiary { get; init; }

    /// <summary>
    /// Additional titled windows shown only on the detail page (e.g. Antigravity's Claude and GPT quota).
    /// </summary>
    public IReadOnlyList<ExtraUsageWindow> ExtraWindows { get; init; } = [];

    public CreditsInfo? Credits { get; init; }
    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
    public bool IsStale => DateTime.UtcNow - FetchedAt > TimeSpan.FromMinutes(5);
    public string? Status { get; init; }
    public ProviderStatus? ServiceStatus { get; init; }
    public CostData? Cost { get; init; }
    public string? Error { get; init; }
    public bool IsLoading { get; init; }

    public bool HasWeekly => Weekly != null;
    public bool HasTertiary => Tertiary != null;
    public bool HasExtraWindows => ExtraWindows.Count > 0;
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>
    /// Gets the display label for the session usage (e.g., "Session" for Claude/Codex, "5-hour" for Gemini).
    /// </summary>
    public string SessionLabel => Provider.ToLowerInvariant() switch
    {
        "gemini" => "5-hour",
        _ => "Session"
    };

    /// <summary>
    /// Gets the display label for the weekly usage ("Weekly" for all providers).
    /// </summary>
    public string WeeklyLabel => "Weekly";

    /// <summary>
    /// Gets the display label for the tertiary usage (e.g., "Sonnet Weekly" for Claude).
    /// </summary>
    public string TertiaryLabel => Provider.ToLowerInvariant() switch
    {
        "claude" => "Current week (Sonnet)",
        _ => "Additional"
    };
}

/// <summary>Health reported by the provider's status page, independent of CLI fetch errors.</summary>
public record ProviderStatus
{
    public ProviderStatusIndicator Indicator { get; init; }
    public string? Description { get; init; }
    public string? Url { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public enum ProviderStatusIndicator
{
    Unknown,
    None,
    Minor,
    Major,
    Critical,
    Maintenance
}

/// <summary>
/// A titled usage window beyond the main session and weekly ones.
/// </summary>
public record ExtraUsageWindow
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public UsageWindow Window { get; init; } = new();
}

/// <summary>
/// Represents a usage window (session or weekly).
/// </summary>
public record UsageWindow
{
    public int Used { get; init; }
    public int Limit { get; init; }
    public double? ReportedPercent { get; init; }
    public double Percent => ReportedPercent ?? (Limit > 0 ? (double)Used / Limit * 100 : 0);
    public string PercentText => $"{Percent:F0}%";
    public DateTime? ResetAt { get; init; }
    public string? ResetIn { get; init; }

    /// <summary>
    /// Length of the usage window in minutes (e.g. 300 for a 5-hour window), or 0 if unknown.
    /// </summary>
    public int WindowMinutes { get; init; }

    /// <summary>
    /// When this window's usage last changed between refreshes (null until a change has been seen).
    /// Used to show the bars that are actually moving.
    /// </summary>
    public DateTime? LastChangedAt { get; init; }

    /// <summary>
    /// How far through the window we are (0-100), or null if the window length or reset time is unknown.
    /// Compare with <see cref="Percent"/> to see whether usage is running ahead of or behind time.
    /// </summary>
    public double? ElapsedPercent =>
        Helpers.ResetTimeFormatter.ElapsedPercent(ResetAt, WindowMinutes, DateTime.UtcNow);

    /// <summary>
    /// What the usage rate so far means for the rest of the window.
    /// </summary>
    public Helpers.PaceProjection Pace =>
        Helpers.PaceCalculator.Project(Percent, WindowMinutes, ResetAt, DateTime.UtcNow);

    /// <summary>
    /// One-sentence pace summary for tooltips (empty when unknown).
    /// </summary>
    public string PaceSummary => Helpers.PaceCalculator.Summarize(Pace);
    public TimeSpan? TimeUntilReset => ResetAt.HasValue ? ResetAt.Value - DateTime.UtcNow : null;

    /// <summary>
    /// Consistent "Resets in ..." text computed from <see cref="ResetAt"/>.
    /// Falls back to the CLI's own description when no reset time is available.
    /// </summary>
    public string ResetText =>
        Helpers.ResetTimeFormatter.Format(ResetAt, DateTime.UtcNow)
        ?? (string.IsNullOrWhiteSpace(ResetIn) ? string.Empty : ResetIn.Trim());
}

/// <summary>
/// Represents credit/cost information.
/// </summary>
public record CreditsInfo
{
    public decimal Used { get; init; }
    public decimal Limit { get; init; }
    public decimal Remaining => Limit - Used;
}
