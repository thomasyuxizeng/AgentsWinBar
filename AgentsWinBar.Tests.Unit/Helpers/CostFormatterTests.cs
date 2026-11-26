using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class CostFormatterTests
{
    [TestMethod]
    [DataRow(0L, "0")]
    [DataRow(-1L, "0")]
    [DataRow(999L, "999")]
    [DataRow(15000L, "15K")]
    [DataRow(15340L, "15.3K")]
    [DataRow(226356190L, "226.4M")]
    [DataRow(2300000000L, "2.3B")]
    public void Tokens_WhenLarge_UsesCompactInvariantUnits(long tokens, string expected) =>
        CostFormatter.Tokens(tokens).Should().Be(expected);

    [TestMethod]
    public void Money_WhenUsd_UsesDollarsAndTwoDecimals() => CostFormatter.Money(20.755m).Should().Be("$20.76");

    [TestMethod]
    public void Money_WhenOtherCurrency_KeepsItsCode() => CostFormatter.Money(4m, "EUR").Should().Be("EUR 4.00");

    [TestMethod]
    public void Summary_WhenDataPresent_ExplicitlyCallsItAnEstimate()
    {
        var text = CostFormatter.Summary(new CostData { TodayCostUSD = 1.25m, TodayTokens = 15000, Last30DaysCostUSD = 12m, Last30DaysTokens = 218000000 });
        text.Should().Be("Est. today $1.25, 15K tokens · 30 days $12.00, 218M tokens");
    }

    [TestMethod]
    public void CardSummary_WhenDataPresent_SeparatesPeriodsWithoutLosingTotals()
    {
        var text = CostFormatter.CardSummary(new CostData { TodayCostUSD = 21.67m, TodayTokens = 76400000, Last30DaysCostUSD = 71.85m, Last30DaysTokens = 226356190 });
        text.Should().Be("Today $21.67 · 76.4M tokens\n30 days $71.85 · 226.4M tokens");
    }

    [TestMethod]
    public void CardSummary_WhenOtherCurrency_KeepsCurrencyOnBothLines()
    {
        var text = CostFormatter.CardSummary(new CostData { CurrencyCode = "EUR", TodayTokens = 15000, Last30DaysCostUSD = 12m });
        text.Should().Be("Today EUR 0.00 · 15K tokens\n30 days EUR 12.00 · 0 tokens");
    }
}
