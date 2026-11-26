using AwesomeAssertions;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public class CostLedgerCostExtensionsTests
{
    [TestMethod]
    [DataRow(200, 10)]
    [DataRow(1000, 2)]
    public void WithLedgerLast30Days_PartialRotationOrRepricing_UsesSameCoverageRuleAsLedger(int tokens, int expectedCost)
    {
        var today = new DateOnly(2026, 10, 11);
        var day = today.AddDays(-1);
        var cost = new CostData
        {
            Provider = "claude", TodayCostUSD = 5m, Provenance = "live logs",
            Daily = [new() { Date = day, TotalCost = 2m, TotalTokens = tokens },
                new() { Date = today.AddDays(-30), TotalCost = 999m, TotalTokens = 999 }]
        };
        var ledger = new CostLedgerLast30DaysResult
        {
            Days = [new() { Date = day, HasLedgerRecord = true, CostUSD = 10m, TotalTokens = 1000 }]
        };
        var merged = cost.WithLedgerLast30Days(ledger, today);
        merged.Daily.Single(d => d.Date == day).TotalCost.Should().Be(expectedCost);
        merged.Last30DaysCostUSD.Should().Be(expectedCost + 5m);
        merged.TodayCostUSD.Should().Be(5m);
        merged.Provenance.Should().Be("live logs");
        merged.Daily.Should().NotContain(d => d.Date < today.AddDays(-29));
    }

    [TestMethod]
    public void WithLedgerLast30Days_NullModelNames_DoesNotTreatUnknownCoverageAsRepricing()
    {
        var today = new DateOnly(2026, 10, 11);
        var day = today.AddDays(-1);
        var cost = new CostData
        {
            Provider = "claude", Daily = [new() { Date = day, TotalCost = 2m, TotalTokens = 1000,
                ModelBreakdowns = [null!, new() { ModelName = null!, TotalTokens = 1000 }] }]
        };
        var ledger = new CostLedgerLast30DaysResult
        {
            Days = [new() { Date = day, HasLedgerRecord = true, CostUSD = 10m, TotalTokens = 1000 }]
        };
        cost.WithLedgerLast30Days(ledger, today).Last30DaysCostUSD.Should().Be(10m);
    }
}
