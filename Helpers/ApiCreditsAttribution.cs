using System.Globalization;
using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

public sealed record ApiCreditsAttributionResult(
    IReadOnlyList<ApiCreditsContributor> Contributors, decimal UnattributedUsd, int UnpricedTokenGroups, decimal UnpricedTokens,
    bool Reconciled = true, decimal ResidualUsd = 0m);

/// <summary>Joins cycle-wide token costs to usage without rounding or using inference geography.</summary>
public static class ApiCreditsAttribution
{
    private readonly record struct PriceKey(string? Model, string? Tier, string? Context, string? Token);

    public static ApiCreditsAttributionResult Calculate(decimal totalSpentUsd,
        IEnumerable<ApiCostResult> costs, IEnumerable<ApiUsageResult> usage,
        IReadOnlyDictionary<string, string> keyCreators, IReadOnlyDictionary<string, string> userNames) =>
        Calculate(totalSpentUsd, costs, usage, keyCreators, userNames, youUserId: null);

    public static ApiCreditsAttributionResult Calculate(decimal totalSpentUsd,
        IEnumerable<ApiCostResult> costs, IEnumerable<ApiUsageResult> usage,
        IReadOnlyDictionary<string, string> keyCreators, IReadOnlyDictionary<string, string> userNames,
        string? youUserId = null)
    {
        var rows = usage.ToArray();
        var cents = new Dictionary<PriceKey, decimal>();
        foreach (var cost in costs)
        {
            if (cost.Currency != "USD" || cost.CostType != "tokens") continue;
            var key = new PriceKey(cost.Model, cost.ServiceTier, cost.ContextWindow, cost.TokenType);
            var amount = decimal.Parse(cost.Amount!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture);
            cents[key] = cents.GetValueOrDefault(key) + amount;
        }
        var totals = new Dictionary<PriceKey, decimal>();
        foreach (var row in rows)
            foreach (var (token, count) in Tokens(row))
            {
                var key = new PriceKey(row.Model, row.ServiceTier, row.ContextWindow, token);
                totals[key] = totals.GetValueOrDefault(key) + count;
            }
        var unpriced = new HashSet<PriceKey>();
        decimal unpricedTokens = 0;
        foreach (var key in cents.Keys)
            if (totals.GetValueOrDefault(key) == 0) unpriced.Add(key);

        var memberSpend = keyCreators.Values.Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, _ => 0m, StringComparer.Ordinal);
        foreach (var row in rows)
            foreach (var (token, count) in Tokens(row))
            {
                if (count <= 0) continue;
                var key = new PriceKey(row.Model, row.ServiceTier, row.ContextWindow, token);
                if (!cents.TryGetValue(key, out var cost) || totals.GetValueOrDefault(key) <= 0)
                { unpriced.Add(key); unpricedTokens += count; continue; }
                if (row.ApiKeyId != null && keyCreators.TryGetValue(row.ApiKeyId, out var creator))
                    memberSpend[creator] += count * (cost / totals[key]) / 100m;
            }
        var contributors = memberSpend.Select(member => new ApiCreditsContributor
        {
            UserId = member.Key, IsYou = youUserId != null && member.Key == youUserId, SpentUsd = member.Value,
            Name = userNames.TryGetValue(member.Key, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name : "Teammate"
        }).OrderByDescending(member => member.IsYou).ThenByDescending(member => member.SpentUsd)
            .ThenBy(member => member.UserId, StringComparer.Ordinal).ToArray();
        var contributorSum = contributors.Sum(member => member.SpentUsd);
        var residualUsd = totalSpentUsd - contributorSum;
        var reconciled = contributorSum <= totalSpentUsd;
        return new(contributors, Math.Max(0, residualUsd), unpriced.Count, unpricedTokens, reconciled, residualUsd);
    }

    public static string? ResolveYou(IEnumerable<ApiOrganizationKey> keys, string adminKey)
    {
        string? creator = null;
        foreach (var key in keys)
        {
            var hint = key.PartialKeyHint;
            if (hint == null) continue;
            var separator = hint.IndexOf("...", StringComparison.Ordinal);
            if (separator <= 0 || separator + 3 >= hint.Length) continue;
            var prefix = hint[..separator]; var suffix = hint[(separator + 3)..];
            if (adminKey.Length < prefix.Length + suffix.Length ||
                !adminKey.StartsWith(prefix, StringComparison.Ordinal) ||
                !adminKey.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(key.CreatedBy?.Id)) return null;
            if (creator != null && creator != key.CreatedBy.Id) return null;
            creator = key.CreatedBy.Id;
        }
        return creator;
    }

    private static IEnumerable<(string Token, decimal Count)> Tokens(ApiUsageResult row)
    {
        yield return ("uncached_input_tokens", row.UncachedInputTokens ?? 0);
        yield return ("output_tokens", row.OutputTokens ?? 0);
        yield return ("cache_read_input_tokens", row.CacheReadInputTokens ?? 0);
        yield return ("cache_creation.ephemeral_1h_input_tokens", row.CacheCreation?.Ephemeral1hInputTokens ?? 0);
        yield return ("cache_creation.ephemeral_5m_input_tokens", row.CacheCreation?.Ephemeral5mInputTokens ?? 0);
    }
}
