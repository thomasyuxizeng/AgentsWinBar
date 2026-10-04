namespace CodexBarWin.Models;

/// <summary>One observed limit window; contains no account or session-log contents.</summary>
public record UsageHistorySample
{
    public DateTime Timestamp { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string WindowKey { get; init; } = string.Empty;
    public double UsedPercent { get; init; }
    public DateTime? ResetsAt { get; init; }
}
