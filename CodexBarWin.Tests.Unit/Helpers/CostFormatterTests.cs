using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

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
}
