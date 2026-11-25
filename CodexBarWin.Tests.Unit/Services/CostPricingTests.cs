using AwesomeAssertions;
using CodexBarWin.Services.Native.Cost;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public sealed class CostPricingTests
{
    [TestMethod]
    public void GetRates_WhenClaudeRequestUsesLongContextAndBothCacheTTLs_PricesEachBucket()
    {
        var pricing = new CostPricing();
        var rate = pricing.GetRates(CostProvider.Claude, "claude-sonnet-4-5", true, false, false, 0);
        rate!.Value.Price(new(200_000, 1_000, 2_000, 1_000, 400)).Should().Be(1.233m);
    }

    [TestMethod]
    public void GetRates_WhenCodexContextCrossesThreshold_DoublesInputAndIncreasesOutput()
    {
        var pricing = new CostPricing();
        var rate = pricing.GetRates(CostProvider.Codex, "gpt-6.2-astra", true, true, false, 0);
        rate.Should().Be(new CostRates(16, 60, .8m, 20));
    }

    [TestMethod]
    public void GetRates_WhenModelHasNoBundledRate_LeavesItUnpriced()
    {
        new CostPricing().GetRates(CostProvider.Claude, "claude-unknown", false, false, false, 0)
            .Should().BeNull();
    }
}
