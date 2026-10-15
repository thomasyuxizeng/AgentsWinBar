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

/// <summary>Combines live and retained daily costs without counting overlapping dates twice.</summary>
public static class CostLedgerCostExtensions
{
    internal static bool HasSameTokenCoverage(this CostLedgerDayEntry entry, DailyCost day)
    {
        if ((day.ModelBreakdowns ?? []).Any(m => m == null || string.IsNullOrWhiteSpace(m.ModelName))) return false;
        if (!(entry.TotalTokens > 0 || entry.InputTokens > 0 || entry.OutputTokens > 0 ||
            entry.CacheReadTokens > 0 || entry.CacheWriteTokens > 0 || entry.ReasoningTokens > 0)) return false;
        return entry.TotalTokens == day.TotalTokens && entry.InputTokens == day.InputTokens &&
            entry.OutputTokens == day.OutputTokens && entry.CacheReadTokens == day.CacheReadTokens &&
            entry.CacheWriteTokens == day.CacheCreationTokens && entry.ReasoningTokens == day.ReasoningTokens &&
            entry.Models.Select(m => (m.ModelName.ToLowerInvariant(), m.TotalTokens)).OrderBy(m => m.Item1, StringComparer.Ordinal)
                .SequenceEqual((day.ModelBreakdowns ?? []).OfType<ModelCost>()
                    .Select(m => (m.ModelName.ToLowerInvariant(), m.TotalTokens)).OrderBy(m => m.Item1, StringComparer.Ordinal));
    }

    public static CostData WithLedgerLast30Days(this CostData cost, CostLedgerLast30DaysResult ledger30,
        DateOnly? asOfDay = null, DateOnly? readingDay = null)
    {
        if (!ledger30.Days.Any(d => d.HasLedgerRecord)) return cost;
        var end = asOfDay ?? ledger30.Days.Max(d => d.Date);
        var start = DateOnly.FromDayNumber(Math.Max(0, end.DayNumber - 29));
        var daily = (cost.Daily ?? []).OfType<DailyCost>().Where(d => d.Date >= start && d.Date <= end)
            .GroupBy(d => d.Date).ToDictionary(g => g.Key, g => new DailyCost
            {
                Date = g.Key, TotalCost = g.Sum(d => Math.Max(0, d.TotalCost)), TotalTokens = g.Sum(d => Math.Max(0, d.TotalTokens)),
                InputTokens = g.Sum(d => Math.Max(0, d.InputTokens)), OutputTokens = g.Sum(d => Math.Max(0, d.OutputTokens)),
                CacheReadTokens = g.Sum(d => Math.Max(0, d.CacheReadTokens)), CacheCreationTokens = g.Sum(d => Math.Max(0, d.CacheCreationTokens)),
                ReasoningTokens = g.Sum(d => Math.Max(0, d.ReasoningTokens)),
                ModelBreakdowns = g.SelectMany(d => d.ModelBreakdowns ?? []).OfType<ModelCost>().ToArray()
            });
        // An aggregate-only reading belongs to its captured calendar day, even across midnight.
        var capturedDay = readingDay ?? end;
        if (!cost.IsStale && capturedDay >= start && capturedDay <= end && !daily.ContainsKey(capturedDay) &&
            (cost.TodayCostUSD > 0 || cost.TodayTokens > 0))
            daily[capturedDay] = new() { Date = capturedDay, TotalCost = Math.Max(0, cost.TodayCostUSD), TotalTokens = Math.Max(0, cost.TodayTokens) };
        foreach (var d in ledger30.Days.Where(d => d.HasLedgerRecord && d.Date >= start && d.Date <= end))
        {
            var retained = new DailyCost
            {
                Date = d.Date, TotalCost = d.CostUSD, TotalTokens = d.TotalTokens,
                InputTokens = d.InputTokens, OutputTokens = d.OutputTokens, CacheReadTokens = d.CacheReadTokens,
                CacheCreationTokens = d.CacheWriteTokens, ReasoningTokens = d.ReasoningTokens,
                ModelBreakdowns = d.Models.Select(m => new ModelCost
                { ModelName = m.ModelName, Cost = m.CostUSD, TotalTokens = m.TotalTokens }).ToArray()
            };
            if (!cost.IsStale && daily.TryGetValue(d.Date, out var live))
            {
                var entry = new CostLedgerDayEntry
                {
                    TotalTokens = d.TotalTokens, InputTokens = d.InputTokens, OutputTokens = d.OutputTokens,
                    CacheReadTokens = d.CacheReadTokens, CacheWriteTokens = d.CacheWriteTokens,
                    ReasoningTokens = d.ReasoningTokens, Models = d.Models
                };
                if (d.Date != end && entry.HasSameTokenCoverage(live)) continue;
                retained = retained with
                {
                    TotalCost = Math.Max(retained.TotalCost, live.TotalCost), TotalTokens = Math.Max(retained.TotalTokens, live.TotalTokens),
                    InputTokens = Math.Max(retained.InputTokens, live.InputTokens), OutputTokens = Math.Max(retained.OutputTokens, live.OutputTokens),
                    CacheReadTokens = Math.Max(retained.CacheReadTokens, live.CacheReadTokens),
                    CacheCreationTokens = Math.Max(retained.CacheCreationTokens, live.CacheCreationTokens),
                    ReasoningTokens = Math.Max(retained.ReasoningTokens, live.ReasoningTokens),
                    ModelBreakdowns = retained.ModelBreakdowns.Concat(live.ModelBreakdowns).Where(m => !string.IsNullOrWhiteSpace(m.ModelName))
                        .GroupBy(m => m.ModelName, StringComparer.OrdinalIgnoreCase).Select(g => new ModelCost
                        { ModelName = g.Key, TotalTokens = g.Max(m => m.TotalTokens), Cost = g.Any(m => m.Cost.HasValue) ? g.Max(m => m.Cost ?? 0) : null }).ToArray()
                };
            }
            daily[d.Date] = retained;
        }
        var series = daily.Values.OrderBy(d => d.Date).ToArray();
        return cost with { Last30DaysCostUSD = series.Sum(d => d.TotalCost), Last30DaysTokens = series.Sum(d => d.TotalTokens), Daily = series };
    }
}
