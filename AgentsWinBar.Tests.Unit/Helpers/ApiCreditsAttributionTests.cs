using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ApiCreditsAttributionTests
{
    private static readonly Dictionary<string, string> Creators = new() { ["key-a"] = "user-a" };
    private static readonly Dictionary<string, string> Names = new() { ["user-a"] = "Alex" };
    private const string CostJson = """
        {"data":[{"results":[
        {"currency":"USD","amount":"9517","cost_type":"tokens","model":"model-a","service_tier":"standard","context_window":"0-200k","token_type":"output_tokens","inference_geo":"global","description":"Output Tokens","workspace_id":null},
        {"currency":"USD","amount":"16","cost_type":"web_search","model":null,"token_type":null}]}],"has_more":false}
        """;
    private const string UsageJson = """
        {"data":[{"results":[{"api_key_id":"key-a","model":"model-a","service_tier":"standard","context_window":"0-200k","output_tokens":100,"uncached_input_tokens":null,"inference_geo":null,"workspace_id":null,"unknown":true,"cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":null}}]}],"has_more":false}
        """;
    private static ApiCostResult[] Costs() => JsonSerializer.Deserialize(CostJson,
        AppJsonSerializerContext.Default.ApiCostReport)!.Data!.SelectMany(b => b.Results!).ToArray();
    private static ApiUsageResult Row(long output, string? key = "key-a") => new()
    { ApiKeyId = key, Model = "model-a", ServiceTier = "standard", ContextWindow = "0-200k", OutputTokens = output };
    private static ApiUsageResult[] Usage() => JsonSerializer.Deserialize(UsageJson,
        AppJsonSerializerContext.Default.ApiUsageReport)!.Data!.SelectMany(b => b!.Results!).OfType<ApiUsageResult>().ToArray();
    private static ApiCreditsAttributionResult Calculate(IEnumerable<ApiUsageResult> rows,
        IReadOnlyDictionary<string, string>? creators = null, IEnumerable<ApiCostResult>? costs = null, decimal total = 95.33m) =>
        ApiCreditsAttribution.Calculate(total, costs ?? Costs(), rows, creators ?? Creators, Names, "user-a");

    [TestMethod]
    public void Calculate_RealisticJsonAndGeographyMismatch_AttributesTokensAndLeavesNonTokenCost()
    {
        var result = Calculate(Usage());
        result.Contributors.Should().ContainSingle().Which.Should().Be(new ApiCreditsContributor
        { Name = "Alex", UserId = "user-a", IsYou = true, SpentUsd = 95.17m });
        result.UnattributedUsd.Should().Be(0.16m);
        result.UnpricedTokenGroups.Should().Be(0);
        result.Reconciled.Should().BeTrue();
        result.ResidualUsd.Should().Be(0.16m);
    }

    [TestMethod]
    public void Calculate_NegativeNonTokenAdjustment_SetsReconciledFalseAndNegativeResidualWithZeroUnattributed()
    {
        var costs = new[]
        {
            new ApiCostResult
            {
                Currency = "USD", Amount = "10000", CostType = "tokens",
                Model = "model-a", ServiceTier = "standard", ContextWindow = "0-200k", TokenType = "output_tokens"
            },
            new ApiCostResult
            {
                Currency = "USD", Amount = "-2000", CostType = "credits_adjustment"
            }
        };
        var rows = new[] { Row(100) };
        var result = ApiCreditsAttribution.Calculate(80m, costs, rows, Creators, Names, "user-a");

        result.Reconciled.Should().BeFalse();
        result.ResidualUsd.Should().Be(-20m);
        result.UnattributedUsd.Should().Be(0m);
        result.Contributors.Should().ContainSingle().Which.SpentUsd.Should().Be(100m);
    }

    [TestMethod]
    public void Calculate_TwoMembersAndNullYouUserId_SetsIsYouFalseAndSortsBySpendDescending()
    {
        var creators = new Dictionary<string, string> { ["key-a"] = "user-a", ["key-b"] = "user-b" };
        var names = new Dictionary<string, string> { ["user-a"] = "Alex", ["user-b"] = "Blake" };
        var rows = new[] { Row(20, "key-a"), Row(80, "key-b") };

        var result = ApiCreditsAttribution.Calculate(95.33m, Costs(), rows, creators, names, youUserId: null);

        result.Contributors.Should().HaveCount(2);
        result.Contributors.Select(c => c.IsYou).Should().AllSatisfy(isYou => isYou.Should().BeFalse());
        result.Contributors.Select(c => c.UserId).Should().Equal("user-b", "user-a");
        result.Contributors[0].Name.Should().Be("Blake");
        result.Contributors[0].SpentUsd.Should().Be(76.136m);
        result.Contributors[1].Name.Should().Be("Alex");
        result.Contributors[1].SpentUsd.Should().Be(19.034m);
    }

    [TestMethod]
    public void Calculate_TwoMembersWithoutYouUserIdOverload_SetsIsYouFalseAndSortsBySpendDescending()
    {
        var creators = new Dictionary<string, string> { ["key-a"] = "user-a", ["key-b"] = "user-b" };
        var names = new Dictionary<string, string> { ["user-a"] = "Alex", ["user-b"] = "Blake" };
        var rows = new[] { Row(20, "key-a"), Row(80, "key-b") };

        var result = ApiCreditsAttribution.Calculate(95.33m, Costs(), rows, creators, names);

        result.Contributors.Should().HaveCount(2);
        result.Contributors.Select(c => c.IsYou).Should().AllSatisfy(isYou => isYou.Should().BeFalse());
        result.Contributors.Select(c => c.UserId).Should().Equal("user-b", "user-a");
    }

    [TestMethod]
    public void Calculate_TwoMembersAndYouUserIdSet_HighlightsExactlyOneAndSortsYouFirst()
    {
        var creators = new Dictionary<string, string> { ["key-a"] = "user-a", ["key-b"] = "user-b" };
        var names = new Dictionary<string, string> { ["user-a"] = "Alex", ["user-b"] = "Blake" };
        var rows = new[] { Row(20, "key-a"), Row(80, "key-b") };

        var result = ApiCreditsAttribution.Calculate(95.33m, Costs(), rows, creators, names, youUserId: "user-a");

        result.Contributors.Should().HaveCount(2);
        result.Contributors.Single(c => c.IsYou).UserId.Should().Be("user-a");
        result.Contributors.Select(c => c.UserId).Should().Equal("user-a", "user-b");
        result.Contributors[0].IsYou.Should().BeTrue();
        result.Contributors[1].IsYou.Should().BeFalse();
    }

    [TestMethod]
    public void Calculate_TwoMembersAndZeroSpendOwners_SortsYouFirstAndSumsAllKeys()
    {
        var creators = new Dictionary<string, string>
        { ["key-a"] = "user-a", ["key-a2"] = "user-a", ["key-b"] = "user-b", ["key-c"] = "user-c" };
        var result = Calculate([Row(10), Row(10, "key-a2"), Row(80, "key-b")], creators);
        result.Contributors.Select(c => c.UserId).Should().Equal("user-a", "user-b", "user-c");
        result.Contributors.Select(c => c.SpentUsd).Should().Equal(19.034m, 76.136m, 0m);
        result.Contributors[1].Name.Should().Be("Teammate");
        result.UnattributedUsd.Should().Be(0.16m);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("deleted-key")]
    public void Calculate_PlaygroundOrDeletedKey_LeavesSpendUnattributed(string? missingKey)
    {
        var result = Calculate([Row(50), Row(50, missingKey)]);
        result.Contributors[0].SpentUsd.Should().Be(47.585m);
        result.UnattributedUsd.Should().Be(47.745m);
    }

    [TestMethod]
    public void Calculate_ZeroTokenCostAndMissingPrice_CountsUnpricedGroups()
    {
        var result = Calculate([Row(0) with { UncachedInputTokens = 12 }]);
        result.Contributors[0].SpentUsd.Should().Be(0);
        result.UnattributedUsd.Should().Be(95.33m);
        result.UnpricedTokenGroups.Should().Be(2);
        result.UnpricedTokens.Should().Be(12);
    }

    [TestMethod]
    public void Calculate_AllTokenTypesAndMultipleDimensions_UsesCycleWidePrices()
    {
        var baseCost = Costs()[0] with { Amount = "100" };
        var costs = new[]
        {
            baseCost with { TokenType = "uncached_input_tokens" },
            baseCost with { TokenType = "output_tokens" },
            baseCost with { TokenType = "cache_read_input_tokens" },
            baseCost with { TokenType = "cache_creation.ephemeral_1h_input_tokens" },
            baseCost with { TokenType = "cache_creation.ephemeral_5m_input_tokens" },
            baseCost with { Model = "model-b", Amount = "200" },
            baseCost with { ServiceTier = "batch", Amount = "300" },
            baseCost with { ContextWindow = "200k-1M", Amount = "400" },
            baseCost with { Amount = "50" }
        };
        var rows = new[]
        {
            Row(10) with { UncachedInputTokens = 10, CacheReadInputTokens = 10,
                CacheCreation = new() { Ephemeral1hInputTokens = 10, Ephemeral5mInputTokens = 10 } },
            Row(10) with { Model = "model-b" }, Row(10) with { ServiceTier = "batch" },
            Row(10) with { ContextWindow = "200k-1M" }
        };
        var result = Calculate(rows, costs: costs, total: 14.5m);
        result.Contributors[0].SpentUsd.Should().Be(14.5m);
        result.UnattributedUsd.Should().Be(0);
        result.UnpricedTokenGroups.Should().Be(0);
    }

    [TestMethod]
    public void Calculate_FractionalCents_PreservesDecimalPrecision()
    {
        var result = Calculate([Row(1), Row(1, "missing")], costs: [Costs()[0] with { Amount = "0.123456789" }], total: 0.00123456789m);
        result.Contributors[0].SpentUsd.Should().Be(0.000617283945m);
        result.UnattributedUsd.Should().Be(0.000617283945m);
    }

    [TestMethod]
    public void Calculate_NonUsdLine_IgnoresForeignCurrency()
    {
        var result = Calculate([Row(100)], costs: [Costs()[0] with { Currency = "EUR" }]);
        result.Contributors[0].SpentUsd.Should().Be(0);
        result.UnpricedTokenGroups.Should().Be(1);
    }

    [TestMethod]
    [DataRow("prefix...suffix", "prefix-middle-suffix", "user-a")]
    [DataRow("different...suffix", "prefix-middle-suffix", null)]
    [DataRow("prefix...other", "prefix-middle-suffix", null)]
    [DataRow(null, "prefix-middle-suffix", null)]
    [DataRow("...suffix", "prefix-middle-suffix", null)]
    [DataRow("prefix...", "prefix-middle-suffix", null)]
    [DataRow("prefixsuffix", "prefix-middle-suffix", null)]
    public void ResolveYou_PartialHint_RequiresBothPrefixAndSuffix(string? hint, string key, string? expected)
    {
        ApiCreditsAttribution.ResolveYou([new() { PartialKeyHint = hint, CreatedBy = new() { Id = "user-a" } }], key)
            .Should().Be(expected);
    }

    [TestMethod]
    public void ResolveYou_SuffixCollision_UsesMatchingPrefix()
    {
        ApiCreditsAttribution.ResolveYou([
            new() { PartialKeyHint = "different...suffix", CreatedBy = new() { Id = "user-b" } },
            new() { PartialKeyHint = "prefix...suffix", CreatedBy = new() { Id = "user-a" } }
        ], "prefix-middle-suffix").Should().Be("user-a");
    }

    [TestMethod]
    public void ResolveYou_AmbiguousMatchingOwners_OmitsIdentity()
    {
        ApiCreditsAttribution.ResolveYou([
            new() { PartialKeyHint = "prefix...suffix", CreatedBy = new() { Id = "user-b" } },
            new() { PartialKeyHint = "prefix...suffix", CreatedBy = new() { Id = "user-a" } }
        ], "prefix-middle-suffix").Should().BeNull();
    }

    [TestMethod]
    public void ApiCreditsInfo_NewRecord_DefaultsToEmptyBreakdown()
    {
        var info = new ApiCreditsInfo();
        info.Contributors.Should().BeEmpty(); info.UnattributedUsd.Should().Be(0); info.HasBreakdown.Should().BeFalse();
        new ApiCreditsContributor().Name.Should().Be("Teammate");
    }
}
