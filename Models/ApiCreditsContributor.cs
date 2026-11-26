namespace AgentsWinBar.Models;

public sealed record ApiCreditsContributor
{
    public string Name { get; init; } = "Teammate";
    public string? UserId { get; init; }
    public decimal SpentUsd { get; init; }
    public bool IsYou { get; init; }
}
