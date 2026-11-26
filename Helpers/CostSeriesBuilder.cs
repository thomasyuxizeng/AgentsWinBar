using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

public static class CostSeriesBuilder
{
    /// <summary>Includes quiet days so the time axis is evenly spaced.</summary>
    public static IReadOnlyList<DailyCost> Daily(CostData data, DateOnly endDate, int days = 30)
    {
        days = Math.Clamp(days, 7, 30);
        var byDate = (data.Daily ?? []).OfType<DailyCost>().GroupBy(d => d.Date).ToDictionary(g => g.Key, g => new DailyCost
        {
            Date = g.Key,
            TotalCost = g.Sum(d => Math.Max(0, d.TotalCost)),
            TotalTokens = g.Sum(d => Math.Max(0, d.TotalTokens)),
            InputTokens = g.Sum(d => Math.Max(0, d.InputTokens)),
            OutputTokens = g.Sum(d => Math.Max(0, d.OutputTokens)),
            CacheReadTokens = g.Sum(d => Math.Max(0, d.CacheReadTokens)),
            CacheCreationTokens = g.Sum(d => Math.Max(0, d.CacheCreationTokens)),
            ReasoningTokens = g.Sum(d => Math.Max(0, d.ReasoningTokens)),
            ModelBreakdowns = g.SelectMany(d => d.ModelBreakdowns ?? []).OfType<ModelCost>().ToArray()
        });
        return Enumerable.Range(0, days).Select(i => endDate.AddDays(i - days + 1))
            .Select(date => byDate.GetValueOrDefault(date) ?? new DailyCost { Date = date }).ToList();
    }

    public static IReadOnlyList<ModelCost> TopModels(CostData data, DateOnly endDate, int count = 5) =>
        (data.Daily ?? []).OfType<DailyCost>().Where(d => d.Date >= endDate.AddDays(-29) && d.Date <= endDate)
            .SelectMany(d => d.ModelBreakdowns ?? []).OfType<ModelCost>().Where(m => !string.IsNullOrWhiteSpace(m.ModelName))
            .GroupBy(m => m.ModelName).Select(g => new ModelCost
            {
                ModelName = g.Key,
                TotalTokens = g.Sum(m => Math.Max(0, m.TotalTokens)),
                Cost = g.Any(m => m.Cost.HasValue) ? g.Sum(m => Math.Max(0, m.Cost ?? 0)) : null
            }).OrderByDescending(m => m.Cost).ThenBy(m => m.ModelName, StringComparer.Ordinal)
            .Take(Math.Max(0, count)).ToList();
}
