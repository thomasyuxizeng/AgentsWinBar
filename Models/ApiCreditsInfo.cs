namespace AgentsWinBar.Models;

public enum ApiCreditsState { Ok, NoKey, Rejected, Unavailable }

/// <summary>In-memory organization API spend, independent of CLI provider credits.</summary>
public sealed record ApiCreditsInfo
{
    public const string BillingUrl = "https://platform.claude.com/settings/billing";
    public const string AdminKeysUrl = "https://platform.claude.com/settings/admin-keys";

    public ApiCreditsState State { get; init; }
    public decimal MonthlyUsd { get; init; }
    public decimal SpentUsd { get; init; }
    public IReadOnlyList<ApiCreditsContributor> Contributors { get; init; } = Array.Empty<ApiCreditsContributor>();
    public decimal UnattributedUsd { get; init; }
    public bool HasBreakdown { get; init; }
    public decimal RemainingUsd { get; init; }
    public double PercentUsed { get; init; }
    public bool IsOverBudget { get; init; }
    public DateOnly CycleStart { get; init; }
    public DateOnly CycleEnd { get; init; }
    public int DaysLeft { get; init; }
    public decimal PaceUsdPerDay { get; init; }
    public DateTime? FetchedAtUtc { get; init; }
    public bool IsStale { get; init; }
    public string? Message { get; init; }
}
