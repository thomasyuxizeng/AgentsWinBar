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
    /// <summary>Tokens in the reported days that had no known price. They are in the token totals, not in the cost.</summary>
    public long UnpricedTokens { get; init; }
    public IReadOnlyList<DailyCost> Daily { get; init; } = [];
    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
    /// <summary>Outcome of the scan that produced these values. IsStale marks a later failed or pending scan.</summary>
    public FetchOutcome FetchOutcome { get; init; }
    public bool IsStale { get; init; }
    public string? StaleReason { get; init; }
    public DateTime? LastSuccessfulFetch { get; init; }
    public bool HasData => FetchOutcome == FetchOutcome.Success || TodayTokens > 0 || TodayCostUSD > 0 || Last30DaysTokens > 0 || Last30DaysCostUSD > 0 ||
        (Daily ?? []).OfType<DailyCost>().Any(d => d.TotalTokens > 0 || d.TotalCost > 0 ||
            d.InputTokens > 0 || d.OutputTokens > 0 || d.CacheReadTokens > 0 || d.CacheCreationTokens > 0 ||
            d.ReasoningTokens > 0 || (d.ModelBreakdowns ?? []).OfType<ModelCost>().Any(m => m.TotalTokens > 0 || m.Cost > 0));
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
    /// <summary>Tokens of this day that had no known price (models whose cost is null in the breakdown).</summary>
    public long UnpricedTokens { get; init; }
    public IReadOnlyList<ModelCost> ModelBreakdowns { get; init; } = [];
}

public record ModelCost
{
    public string ModelName { get; init; } = string.Empty;
    public long TotalTokens { get; init; }
    public decimal? Cost { get; init; }
}

/// <summary>The last complete cost result per provider, kept on disk so a failed or slow scan never blanks it.</summary>
public record CostCacheFile
{
    public int Version { get; init; } = 1;
    public Dictionary<string, CostData> Items { get; init; } = [];
}

/// <summary>The CLI calls today's calendar-day totals "session".</summary>
public record CostDataDto
{
    public string Provider { get; init; } = string.Empty;
    public decimal? SessionCostUSD { get; init; }
    public long? SessionTokens { get; init; }
    public decimal? Last30DaysCostUSD { get; init; }
    public long? Last30DaysTokens { get; init; }
    public string? Error { get; init; }
    public string? Source { get; init; }
    public bool HasReading => string.IsNullOrWhiteSpace(Error) && !string.Equals(Source, "offline", StringComparison.OrdinalIgnoreCase) &&
        (SessionCostUSD.HasValue || SessionTokens.HasValue ||
        Last30DaysCostUSD.HasValue || Last30DaysTokens.HasValue || Daily?.Count > 0);
    public string? CurrencyCode { get; init; }
    public int HistoryDays { get; init; } = 30;
    public string? Provenance { get; init; }
    public CostCoverage? Coverage { get; init; }
    /// <summary>Set by the native scanner; the CLI's JSON has no such field and reads as zero.</summary>
    public long? UnpricedTokens { get; init; }
    public List<DailyCost>? Daily { get; init; }

    public CostData ToCostData(string provider, DateTime fetchedAt) => new()
    {
        Provider = ProviderConstants.ValidateAndNormalize(provider),
        TodayCostUSD = SessionCostUSD ?? 0,
        TodayTokens = SessionTokens ?? 0,
        Last30DaysCostUSD = Last30DaysCostUSD ?? 0,
        Last30DaysTokens = Last30DaysTokens ?? 0,
        CurrencyCode = string.IsNullOrWhiteSpace(CurrencyCode) ? "USD" : CurrencyCode,
        Provenance = Provenance,
        HistoryDays = HistoryDays,
        Coverage = Coverage ?? new(),
        UnpricedTokens = UnpricedTokens ?? 0,
        Daily = Daily ?? [],
        FetchedAt = fetchedAt,
        FetchOutcome = HasReading ? FetchOutcome.Success : FetchOutcome.Unavailable,
        LastSuccessfulFetch = HasReading ? fetchedAt : null
    };
}
