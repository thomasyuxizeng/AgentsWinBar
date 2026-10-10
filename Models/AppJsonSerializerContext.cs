using System.Text.Json.Serialization;

namespace CodexBarWin.Models;

/// <summary>
/// JSON serializer context for AOT-compatible serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CacheData))]
[JsonSerializable(typeof(UsageDataDto))]
[JsonSerializable(typeof(List<UsageDataDto>))]
[JsonSerializable(typeof(ProviderStatusDto))]
[JsonSerializable(typeof(ProviderStatus))]
[JsonSerializable(typeof(CostDataDto))]
[JsonSerializable(typeof(List<CostDataDto>))]
[JsonSerializable(typeof(CostData))]
[JsonSerializable(typeof(CostCoverage))]
[JsonSerializable(typeof(DailyCost))]
[JsonSerializable(typeof(ModelCost))]
[JsonSerializable(typeof(CostCacheFile))]
[JsonSerializable(typeof(UsageHistorySample))]
[JsonSerializable(typeof(List<UsageHistorySample>))]
[JsonSerializable(typeof(IReadOnlyList<UsageHistorySample>))]
public partial class AppJsonSerializerContext : JsonSerializerContext
{
}

/// <summary>
/// Cache data for disk persistence.
/// </summary>
public record CacheData
{
    public DateTime Timestamp { get; init; }
    public Dictionary<string, UsageData> Items { get; init; } = [];
}

/// <summary>
/// DTO for deserializing codexbar JSON output.
/// </summary>
public record UsageDataDto
{
    public string Provider { get; init; } = string.Empty;
    public string? Source { get; init; }
    public bool Cached { get; init; }
    public DateTime? CacheUpdatedAt { get; init; }
    public ProviderStatusDto? Status { get; init; }
    public UsageDto? Usage { get; init; }
    public string? Error { get; init; }

    public UsageData ToUsageData() => new()
    {
        Provider = Provider,
        Plan = Usage?.LoginMethod,
        Session = Usage?.Primary?.ToUsageWindow(),
        Weekly = Usage?.Secondary?.ToUsageWindow(),
        Tertiary = Usage?.Tertiary?.ToUsageWindow(),
        ExtraWindows = (Usage?.ExtraRateWindows ?? [])
            .Where(x => x.Window?.HasReading == true && !string.IsNullOrWhiteSpace(x.Title))
            .Select(x => new ExtraUsageWindow { Key = x.Id ?? string.Empty, Title = Helpers.WindowTitles.Short(x.Title), Window = x.Window!.ToUsageWindow()! })
            .ToList(),
        Status = Source,
        UsageIsCached = Cached || string.Equals(Source, "cache", StringComparison.OrdinalIgnoreCase),
        CacheUpdatedAt = CacheUpdatedAt,
        ServiceStatus = Status?.ToProviderStatus(),
        Error = Error,
        UsageFetchOutcome = !string.IsNullOrWhiteSpace(Error) || string.Equals(Source, "offline", StringComparison.OrdinalIgnoreCase)
            ? FetchOutcome.Failed
            : Usage?.Primary?.HasReading == true || Usage?.Secondary?.HasReading == true || Usage?.Tertiary?.HasReading == true ||
                Usage?.ExtraRateWindows?.Any(x => x.Window?.HasReading == true && !string.IsNullOrWhiteSpace(x.Title)) == true
                ? FetchOutcome.Success : FetchOutcome.Unavailable,
        StatusFetchOutcome = Status == null || string.IsNullOrWhiteSpace(Status.Indicator) ? FetchOutcome.Unavailable : FetchOutcome.Success,
        FetchedAt = DateTime.UtcNow,
        DataUpdatedAt = (Usage?.UpdatedAt ?? CacheUpdatedAt) is { } updated ? Helpers.DataAge.ToUtc(updated) : null
    };
}

/// <summary>Raw status strings tolerate new or missing CLI indicators.</summary>
public record ProviderStatusDto
{
    public string? Indicator { get; init; }
    public string? Description { get; init; }
    public string? Url { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? IncidentTitle { get; init; }
    public string? LatestUpdateText { get; init; }
    public DateTime? LatestUpdateAt { get; init; }

    public ProviderStatus ToProviderStatus() => new()
    {
        Indicator = Helpers.ProviderStatusPresentation.ParseIndicator(Indicator),
        Description = Description,
        Url = Url,
        UpdatedAt = UpdatedAt,
        IncidentTitle = IncidentTitle,
        LatestUpdateText = LatestUpdateText,
        LatestUpdateAt = LatestUpdateAt
    };
}

/// <summary>
/// DTO for usage information.
/// </summary>
public record UsageDto
{
    public string? LoginMethod { get; init; }
    public UsageWindowDto? Primary { get; init; }
    public UsageWindowDto? Secondary { get; init; }
    public UsageWindowDto? Tertiary { get; init; }
    public List<ExtraRateWindowDto>? ExtraRateWindows { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>
/// DTO for an additional, separately titled usage window (e.g. Antigravity's Claude and GPT quota).
/// </summary>
public record ExtraRateWindowDto
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public UsageWindowDto? Window { get; init; }
}

/// <summary>
/// DTO for usage window information.
/// </summary>
public record UsageWindowDto
{
    public double? UsedPercent { get; init; }
    public bool HasReading => UsedPercent is { } percent && double.IsFinite(percent) && percent >= 0;
    public int WindowMinutes { get; init; }
    public DateTime? ResetsAt { get; init; }
    public string? ResetDescription { get; init; }

    public UsageWindow? ToUsageWindow() => !HasReading ? null : new()
    {
        Used = (int)UsedPercent!.Value,
        ReportedPercent = UsedPercent,
        Limit = 100,
        WindowMinutes = WindowMinutes,
        ResetAt = ResetsAt,
        ResetIn = ResetDescription
    };
}
