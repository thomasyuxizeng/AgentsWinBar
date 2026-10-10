using System.Text.Json.Serialization;

namespace CodexBarWin.Models;

/// <summary>
/// Root persistent document for permanent cost tracking across log rotation.
/// </summary>
public record CostLedgerFile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("providers")]
    public Dictionary<string, Dictionary<string, CostLedgerDayEntry>> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Permanent record for one calendar day and provider.
/// </summary>
public record CostLedgerDayEntry
{
    [JsonPropertyName("date")]
    public DateOnly Date { get; init; }

    [JsonPropertyName("costUSD")]
    public decimal CostUSD { get; init; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cacheReadTokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("cacheWriteTokens")]
    public long CacheWriteTokens { get; init; }

    [JsonPropertyName("reasoningTokens")]
    public long ReasoningTokens { get; init; }

    [JsonPropertyName("models")]
    public IReadOnlyList<CostLedgerModelEntry> Models { get; init; } = [];

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTime LastUpdatedUtc { get; init; }

    [JsonIgnore]
    public bool HasData => CostUSD > 0 || TotalTokens > 0 || InputTokens > 0 || OutputTokens > 0 ||
        CacheReadTokens > 0 || CacheWriteTokens > 0 || ReasoningTokens > 0 ||
        Models.Any(m => (m.CostUSD ?? 0) > 0 || m.TotalTokens > 0);
}

/// <summary>
/// Per-model breakdown within a ledger day entry.
/// </summary>
public record CostLedgerModelEntry
{
    [JsonPropertyName("modelName")]
    public string ModelName { get; init; } = string.Empty;

    [JsonPropertyName("costUSD")]
    public decimal? CostUSD { get; init; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }
}

/// <summary>
/// Lifetime totals returned by ICostLedger.GetLifetime.
/// </summary>
public record CostLedgerLifetimeResult
{
    public decimal TotalCostUSD { get; init; }
    public long TotalTokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public DateOnly? FirstDayWithData { get; init; }
    public DateOnly? LastDayWithData { get; init; }
    public int DaysWithDataCount { get; init; }
    public IReadOnlyDictionary<string, CostLedgerProviderTotal> ByProvider { get; init; } =
        new Dictionary<string, CostLedgerProviderTotal>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Provider-specific lifetime slice in CostLedgerLifetimeResult.
/// </summary>
public record CostLedgerProviderTotal
{
    public string Provider { get; init; } = string.Empty;
    public decimal TotalCostUSD { get; init; }
    public long TotalTokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public DateOnly? FirstDayWithData { get; init; }
    public DateOnly? LastDayWithData { get; init; }
    public int DaysWithDataCount { get; init; }
}

/// <summary>
/// Status of a calendar day in a range or 30-day query.
/// HasLedgerRecord is false for 'no data' (distinct from a recorded zero).
/// </summary>
public record CostLedgerDayStatus
{
    public DateOnly Date { get; init; }
    public bool HasLedgerRecord { get; init; }
    public decimal CostUSD { get; init; }
    public long TotalTokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public IReadOnlyList<CostLedgerModelEntry> Models { get; init; } = [];

    public bool HasData => HasLedgerRecord && (CostUSD > 0 || TotalTokens > 0 || InputTokens > 0 || OutputTokens > 0 ||
        CacheReadTokens > 0 || CacheWriteTokens > 0 || ReasoningTokens > 0 ||
        Models.Any(m => (m.CostUSD ?? 0) > 0 || m.TotalTokens > 0));
}

/// <summary>
/// Daily series returned by ICostLedger.GetRange.
/// </summary>
public record CostLedgerRangeResult
{
    public decimal TotalCostUSD { get; init; }
    public long TotalTokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public int DaysWithDataCount { get; init; }
    public IReadOnlyList<CostLedgerDayStatus> Days { get; init; } = [];
}

/// <summary>
/// Exactly 30 calendar days ending asOfDay inclusive, returned by ICostLedger.GetLast30Days.
/// </summary>
public record CostLedgerLast30DaysResult
{
    public decimal TotalCostUSD { get; init; }
    public long TotalTokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public int DaysWithDataCount { get; init; }
    public IReadOnlyList<CostLedgerDayStatus> Days { get; init; } = [];
}
