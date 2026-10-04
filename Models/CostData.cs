namespace CodexBarWin.Models;

/// <summary>Local log totals at list prices, not the provider's bill.</summary>
public record CostData
{
    public string Provider { get; init; } = string.Empty;
    public decimal TodayCostUSD { get; init; }
    public long TodayTokens { get; init; }
    public decimal Last30DaysCostUSD { get; init; }
    public long Last30DaysTokens { get; init; }
    public string CurrencyCode { get; init; } = "USD";
    public string? Provenance { get; init; }
    public int HistoryDays { get; init; } = 30;
    public CostCoverage Coverage { get; init; } = new();
    public IReadOnlyList<DailyCost> Daily { get; init; } = [];
    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
    public bool HasData => Last30DaysTokens > 0 || Last30DaysCostUSD > 0 ||
        Daily.Any(d => d.TotalTokens > 0 || d.TotalCost > 0);
}

public record CostCoverage
{
    public int Priced { get; init; }
    public int Unpriced { get; init; }
    public int Estimated { get; init; }
    public int Unmetered { get; init; }
}

public record DailyCost
{
    public DateOnly Date { get; init; }
    public decimal TotalCost { get; init; }
    public long TotalTokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public IReadOnlyList<ModelCost> ModelBreakdowns { get; init; } = [];
}

public record ModelCost
{
    public string ModelName { get; init; } = string.Empty;
    public long TotalTokens { get; init; }
    public decimal? Cost { get; init; }
}

/// <summary>The CLI calls today's calendar-day totals "session".</summary>
public record CostDataDto
{
    public string Provider { get; init; } = string.Empty;
    public decimal SessionCostUSD { get; init; }
    public long SessionTokens { get; init; }
    public decimal Last30DaysCostUSD { get; init; }
    public long Last30DaysTokens { get; init; }
    public string? CurrencyCode { get; init; }
    public int HistoryDays { get; init; } = 30;
    public string? Provenance { get; init; }
    public CostCoverage? Coverage { get; init; }
    public List<DailyCost>? Daily { get; init; }

    public CostData ToCostData(string provider, DateTime fetchedAt) => new()
    {
        Provider = ProviderConstants.ValidateAndNormalize(provider),
        TodayCostUSD = SessionCostUSD,
        TodayTokens = SessionTokens,
        Last30DaysCostUSD = Last30DaysCostUSD,
        Last30DaysTokens = Last30DaysTokens,
        CurrencyCode = string.IsNullOrWhiteSpace(CurrencyCode) ? "USD" : CurrencyCode,
        Provenance = Provenance,
        HistoryDays = HistoryDays,
        Coverage = Coverage ?? new(),
        Daily = Daily ?? [],
        FetchedAt = fetchedAt
    };
}
