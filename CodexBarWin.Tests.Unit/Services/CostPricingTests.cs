using AwesomeAssertions;
using CodexBarWin.Services.Native.Cost;
using CodexBarWin.Tests.Unit.Services.Native.Cost;
using static CodexBarWin.Tests.Unit.Services.Native.Cost.CostTestSupport;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public sealed class CostPricingTests
{
    private const string Flat = "\"input\":4,\"output\":20,\"cache_read\":0.2,\"cache_write\":5";
    private const string LongLane = "\"context_over_200k\":{\"input\":8,\"output\":30,\"cache_read\":0.4,\"cache_write\":10}";
    private const string Tier272 = "\"tiers\":[{\"input\":8,\"output\":30,\"cache_read\":0.4,\"cache_write\":10,\"tier\":{\"type\":\"context\",\"size\":272000}}]";

    private static CostPricing Pricing(string anthropic = "", string openAi = "", string overlay = "{}", string custom = "{}") =>
        new(Catalog(CatalogJson(anthropic, openAi)), Overlay(overlay), Custom(custom));

    [TestMethod]
    [DataRow("claude-opus-5-5")]
    [DataRow("claude-sonnet-5-5")]
    [DataRow("claude-haiku-5-5")]
    public void GetRates_WhenCurrentClaudeModelUsesBundledSnapshot_IsPriced(string model)
    {
        var rate = new CostPricing().GetRates(CostProvider.Claude, model, false, false, false, 0);

        rate.Should().NotBeNull();
        rate!.Value.Input.Should().BePositive();
        rate.Value.Output.Should().BePositive();
    }

    [TestMethod]
    public void GetRates_WhenClaudeModelIsInCatalog_UsesItsRatesAndTwiceInputForOneHourWrites()
    {
        var rate = Pricing(Model("claude-test-1", Flat)).GetRates(CostProvider.Claude, "claude-test-1", false, false, false, 0);

        rate.Should().Be(new CostRates(4, 20, .2m, 5, 8));
    }

    [TestMethod]
    public void GetRates_WhenClaudeCatalogEntryOmitsCacheRates_ChargesCacheTokensAtInputRate()
    {
        var rate = Pricing(Model("claude-test-1", "\"input\":4,\"output\":20"))
            .GetRates(CostProvider.Claude, "claude-test-1", false, false, false, 0);

        rate.Should().Be(new CostRates(4, 20, 4, 4, 8));
    }

    [TestMethod]
    public void GetRates_WhenClaudeRequestUsesLongContextAndBothCacheTTLs_PricesEachBucket()
    {
        // Not in the catalog, so upstream's table applies: 200K tier, 5-minute and one-hour cache writes.
        var pricing = new CostPricing(Catalog(CatalogJson("", "")));

        var rate = pricing.GetRates(CostProvider.Claude, "claude-sonnet-4-5", true, false, false, 0);

        rate.Should().Be(new CostRates(6, 22.5m, .6m, 7.5m, 12));
        rate!.Value.Price(new(200_000, 1_000, 2_000, 1_000, 400)).Should().Be(1.233m);
    }

    [TestMethod]
    public void GetRates_WhenClaudeCatalogHasNoLongContextLane_IgnoresOtherTiers()
    {
        // A context tier without the long-context lane is not a price upstream applies.
        var cost = "\"input\":1,\"output\":5,\"tiers\":[{\"input\":5,\"output\":25,\"tier\":{\"type\":\"context\",\"size\":100000}}]";

        var rate = Pricing(Model("claude-test-1", cost)).GetRates(CostProvider.Claude, "claude-test-1", true, true, false, 0);

        rate.Should().Be(new CostRates(1, 5, 1, 1, 2));
    }

    [TestMethod]
    public void GetRates_WhenClaudeUsageIsBeforeThePriceChange_UsesHistoricalLongContextRates()
    {
        var pricing = new CostPricing();

        pricing.GetRates(CostProvider.Claude, "claude-opus-4-6", true, false, true, 0)
            .Should().Be(new CostRates(10, 37.5m, 1, 12.5m, 20));
        pricing.GetRates(CostProvider.Claude, "claude-opus-4-6", true, false, false, 0)
            .Should().Be(new CostRates(5, 25, .5m, 6.25m, 10));
    }

    [TestMethod]
    public void GetRates_WhenCatalogIdCarriesSnapshotDate_MatchesTheUndatedModel()
    {
        var pricing = Pricing(Model("claude-test-1-20260101", Flat) + "," + Model("claude-test-2@default", Flat));

        pricing.GetRates(CostProvider.Claude, "claude-test-1", false, false, false, 0).Should().NotBeNull();
        pricing.GetRates(CostProvider.Claude, "claude-test-2", false, false, false, 0).Should().NotBeNull();
        pricing.GetRates(CostProvider.Claude, "claude-test", false, false, false, 0).Should().BeNull();
    }

    [TestMethod]
    [DataRow("anthropic.claude-opus-5-5-20261001-v1:0", CostProvider.Claude, "claude-opus-5-5")]
    [DataRow("us.anthropic.claude-sonnet-5-5", CostProvider.Claude, "claude-sonnet-5-5")]
    [DataRow("deepseek-ai/DeepSeek-V4.1-Flash-1017", CostProvider.Claude, "deepseek-ai/DeepSeek-V4.1-Flash-1017")]
    [DataRow("deepseek-v4.1-flash:free", CostProvider.Claude, "deepseek-v4.1-flash:free")]
    [DataRow("claude-opus-5-5-v2:1", CostProvider.Claude, "claude-opus-5-5")]
    [DataRow("<synthetic>", CostProvider.Claude, "unknown")]
    [DataRow("gpt-5.6", CostProvider.Codex, "gpt-5.6-sol")]
    [DataRow("gpt-reserve", CostProvider.Codex, "gpt-5.6-luna")]
    [DataRow("gpt-daybreak-red-latest", CostProvider.Codex, "gpt-5.6-cyber")]
    [DataRow("openai/gpt-6.2-sol-2026-10-01", CostProvider.Codex, "gpt-6.2-sol")]
    [DataRow("", CostProvider.Codex, "unknown")]
    public void Normalize_WhenModelIdHasAliasOrDecoration_ReturnsThePricingKey(string raw, CostProvider provider, string expected)
    {
        CostPricing.Normalize(raw, provider).Should().Be(expected);
    }

    [TestMethod]
    public void GetRates_WhenCodexLaneHasContextTier_UsesTheTierSizeAsThreshold()
    {
        var pricing = Pricing(openAi: Model("gpt-test", $"{Flat},{LongLane},{Tier272}"));

        pricing.GetRates(CostProvider.Codex, "gpt-test", true, false, false, 0).Should().Be(new CostRates(4, 20, .2m, 5));
        pricing.GetRates(CostProvider.Codex, "gpt-test", true, true, false, 0).Should().Be(new CostRates(8, 30, .4m, 10));
    }

    [TestMethod]
    public void GetRates_WhenCodexLaneHasNoTierAndModelIsUnknownToUpstream_Uses200K()
    {
        var pricing = Pricing(openAi: Model("gpt-test", $"{Flat},{LongLane}"));

        pricing.GetRates(CostProvider.Codex, "gpt-test", true, false, false, 0).Should().Be(new CostRates(8, 30, .4m, 10));
    }

    [TestMethod]
    public void GetRates_WhenCodexLaneHasNoTierButUpstreamTableHasOne_UsesTheTableThreshold()
    {
        var overlay = "{\"codex\":{\"gpt-test\":{" + $"{Flat},{LongLane},{Tier272}" + "}}}";
        var pricing = Pricing(openAi: Model("gpt-test", $"{Flat},{LongLane}"), overlay: overlay);

        pricing.GetRates(CostProvider.Codex, "gpt-test", true, false, false, 0).Should().Be(new CostRates(4, 20, .2m, 5));
        pricing.GetRates(CostProvider.Codex, "gpt-test", true, true, false, 0).Should().Be(new CostRates(8, 30, .4m, 10));
    }

    [TestMethod]
    public void GetRates_WhenOverlaySetsModelThreshold_AppliesLongContextAboveIt()
    {
        var overlay = "{\"codexLongContextThresholds\":{\"gpt-test\":200000}}";
        var pricing = Pricing(openAi: Model("gpt-test", $"{Flat},{LongLane},{Tier272}") + "," +
            Model("gpt-other", $"{Flat},{LongLane},{Tier272}"), overlay: overlay);

        pricing.GetRates(CostProvider.Codex, "gpt-test", true, false, false, 0).Should().Be(new CostRates(8, 30, .4m, 10));
        pricing.GetRates(CostProvider.Codex, "gpt-other", true, false, false, 0).Should().Be(new CostRates(4, 20, .2m, 5));
    }

    [TestMethod]
    public void GetRates_WhenBundledDataIsUsed_KeepsTheThresholdsTheLedgerWasPricedWith()
    {
        var pricing = new CostPricing();

        // 200K for the models the installed CLI priced that way, the 272K tier for the rest.
        pricing.GetRates(CostProvider.Codex, "gpt-6.1-sol", true, false, false, 0)!.Value.Input.Should().Be(4);
        pricing.GetRates(CostProvider.Codex, "gpt-6.2-sol", true, false, true, 0)!.Value.Input.Should().Be(4);
        pricing.GetRates(CostProvider.Codex, "gpt-6-astra", true, false, false, 0)!.Value.Input.Should().Be(10);
        pricing.GetRates(CostProvider.Codex, "gpt-6-astra", true, true, false, 0)!.Value.Input.Should().Be(20);
    }

    [TestMethod]
    public void GetRates_WhenCodexCatalogHasNoLane_TakesLongContextFromUpstreamTable()
    {
        var overlay = "{\"codex\":{\"gpt-test\":{" + $"{Flat},{LongLane},{Tier272}" + "}}}";
        var pricing = Pricing(openAi: Model("gpt-test", "\"input\":3,\"output\":9"), overlay: overlay);

        // Catalog rates win; the cache rates and the whole long-context tuple come from the table.
        pricing.GetRates(CostProvider.Codex, "gpt-test", false, false, false, 0).Should().Be(new CostRates(3, 9, .2m, 5));
        pricing.GetRates(CostProvider.Codex, "gpt-test", true, true, false, 0).Should().Be(new CostRates(8, 30, .4m, 10));
    }

    [TestMethod]
    public void GetRates_WhenCodexModelHasNoCacheRates_ChargesCachedTokensAtInputRate()
    {
        var rate = Pricing(openAi: Model("gpt-test", "\"input\":15,\"output\":120"))
            .GetRates(CostProvider.Codex, "gpt-test", false, false, false, 0);

        rate.Should().Be(new CostRates(15, 120, 15, 15));
    }

    [TestMethod]
    public void GetRates_WhenCodexUsageIsBeforeThePriceChange_UsesHistoricalRates()
    {
        var pricing = new CostPricing();

        pricing.GetRates(CostProvider.Codex, "gpt-5.6-sol", false, false, true, 0).Should().Be(new CostRates(5, 30, .5m, 6.25m));
        pricing.GetRates(CostProvider.Codex, "gpt-5.6-sol", false, false, false, 0).Should().Be(new CostRates(4, 20, .4m, 5));
    }

    [TestMethod]
    public void GetRates_WhenCodexContextCrossesThreshold_DoublesInputAndIncreasesOutput()
    {
        var rate = new CostPricing().GetRates(CostProvider.Codex, "gpt-6.2-astra", true, true, false, 0);

        rate.Should().Be(new CostRates(16, 60, .8m, 20));
    }

    [TestMethod]
    [DataRow("gpt-5.5", 1, false, false, 2.5)]
    [DataRow("gpt-5.4", 1, false, false, 2)]
    [DataRow("gpt-5.4", 1, true, false, 1)]
    [DataRow("gpt-6.2-sol", 1, true, false, 2)]
    [DataRow("gpt-6.2-sol", 2, false, true, 3)]
    [DataRow("gpt-6.2-sol", 2, false, false, 6)]
    [DataRow("gpt-6-astra", 2, false, false, 6)]
    [DataRow("gpt-5.2", 1, false, false, 1)]
    [DataRow("gpt-6.2-sol", 0, false, false, 1)]
    public void ServiceTierMultiplier_WhenFastTierIsRecorded_FollowsUpstreamRates(string model, int tier, bool above272K,
        bool historical, double expected)
    {
        CostPricing.ServiceTierMultiplier(model, above272K, historical, tier).Should().Be((decimal)expected);
    }

    [TestMethod]
    public void GetRates_WhenCodexRequestUsedFastTier_MultipliesEveryRate()
    {
        var pricing = new CostPricing();
        var standard = pricing.GetRates(CostProvider.Codex, "gpt-5.5", false, false, false, 0)!.Value;

        var fast = pricing.GetRates(CostProvider.Codex, "gpt-5.5", false, false, false, 1);

        fast.Should().Be(new CostRates(standard.Input * 2.5m, standard.Output * 2.5m, standard.CacheRead * 2.5m,
            standard.CacheWrite * 2.5m));
    }

    [TestMethod]
    public void GetRates_WhenCustomOverlayHasTheModel_WinsOverCatalogAndIgnoresTiers()
    {
        var custom = "{\"GPT-Test\":{\"input\":1,\"output\":2,\"cacheRead\":0.5,\"cache_write\":3}}";
        var pricing = Pricing(openAi: Model("gpt-test", $"{Flat},{LongLane},{Tier272}"), custom: custom);

        pricing.GetRates(CostProvider.Codex, "gpt-test", true, true, false, 0).Should().Be(new CostRates(1, 2, .5m, 3));
    }

    [TestMethod]
    public void GetRates_WhenCustomOverlayKeyIsProviderQualified_MatchesThatProviderOnly()
    {
        var custom = "{\"openai/gpt-test\":{\"input\":1,\"output\":2,\"cacheRead\":0,\"cacheWrite\":0}}";
        var pricing = Pricing(custom: custom);

        pricing.GetRates(CostProvider.Codex, "gpt-test", false, false, false, 0).Should().Be(new CostRates(1, 2, 0, 0));
        pricing.GetRates(CostProvider.Claude, "gpt-test", false, false, false, 0).Should().BeNull();
    }

    [TestMethod]
    public void PriceTokens_WhenCustomOverlayLacksARate_LeavesOnlyRequestsThatNeedItUnpriced()
    {
        var pricing = Pricing(custom: "{\"deepseek-test:free\":{\"input\":0,\"output\":0}}");

        // Zero is a price (free); a missing cache rate is unknown, not zero.
        pricing.PriceTokens(CostProvider.Claude, "deepseek-test:free", false, false, false, 0, new(1_000, 500, 0))
            .Should().Be(0);
        pricing.PriceTokens(CostProvider.Claude, "deepseek-test:free", false, false, false, 0, new(1_000, 500, 10))
            .Should().BeNull();
        pricing.GetRates(CostProvider.Claude, "deepseek-test:free", false, false, false, 0).Should().BeNull();
    }

    [TestMethod]
    public void PriceTokens_WhenCustomOverlayPricesAFastCodexRequest_AppliesTheMultiplier()
    {
        var pricing = Pricing(custom: "{\"gpt-5.5\":{\"input\":10,\"output\":20,\"cacheRead\":1,\"cacheWrite\":10}}");

        pricing.PriceTokens(CostProvider.Codex, "gpt-5.5", false, false, false, 1, new(1_000_000, 0, 0)).Should().Be(25);
    }

    [TestMethod]
    public void GetRates_WhenModelHasNoRateAnywhere_LeavesItUnpriced()
    {
        var pricing = new CostPricing();

        pricing.GetRates(CostProvider.Claude, "claude-unknown", false, false, false, 0).Should().BeNull();
        pricing.GetRates(CostProvider.Codex, "unknown", false, false, false, 0).Should().BeNull();
        pricing.GetRates(CostProvider.Claude, "deepseek-ai/DeepSeek-V4.1-Flash-1017", false, false, false, 0).Should().BeNull();
        pricing.PriceTokens(CostProvider.Codex, "gpt-not-released", false, false, false, 0, new(10, 10, 0)).Should().BeNull();
    }

    [TestMethod]
    public void GetRates_WhenClaudeToolRecordsAnOpenAiModel_UsesTheOpenAiCatalog()
    {
        var rate = Pricing(openAi: Model("gpt-test", Flat)).GetRates(CostProvider.Claude, "gpt-test", false, false, false, 0);

        rate.Should().Be(new CostRates(4, 20, .2m, 5, 8));
    }
}
