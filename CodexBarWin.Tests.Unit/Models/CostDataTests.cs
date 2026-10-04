using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Models;

[TestClass]
public class CostDataTests
{
    [TestMethod]
    public void ToCostData_WhenRealCliShape_PreservesTodayDailyModelsAndCoverage()
    {
        var json = """
            [{"provider":"codex","last30DaysCostUSD":71.85,"last30DaysTokens":226356190,
              "sessionCostUSD":20.75,"sessionTokens":73233194,"currencyCode":"USD","historyDays":30,
              "provenance":"listPriceEstimate","coverage":{"priced":8,"unpriced":1,"estimated":2,"unmetered":3},
              "daily":[{"date":"2026-10-04","totalCost":20.75,"totalTokens":73233194,"inputTokens":218,
              "outputTokens":93687,"cacheReadTokens":72939159,"cacheCreationTokens":200130,"reasoningTokens":15,
              "modelsUsed":["gpt-6.1-sol"],"modelBreakdowns":[{"modelName":"gpt-6.1-sol","totalTokens":73233194,"cost":20.75},
              {"modelName":"unpriced","totalTokens":109028}]}],"projects":[],"totals":{"totalCost":71.85}}]
            """;
        var dto = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListCostDataDto)!.Single();
        var now = DateTime.UtcNow;
        var data = dto.ToCostData("codex", now);
        data.TodayCostUSD.Should().Be(20.75m);
        data.TodayTokens.Should().Be(73233194);
        data.Last30DaysTokens.Should().Be(226356190);
        data.Last30DaysCostUSD.Should().Be(71.85m);
        data.FetchedAt.Should().Be(now);
        data.Provenance.Should().Be("listPriceEstimate");
        data.Coverage.Should().Be(new CostCoverage { Priced = 8, Unpriced = 1, Estimated = 2, Unmetered = 3 });
        data.Daily[0].Date.Should().Be(new DateOnly(2026, 10, 4));
        data.Daily[0].InputTokens.Should().Be(218);
        data.Daily[0].ReasoningTokens.Should().Be(15);
        data.Daily[0].ModelBreakdowns[1].Cost.Should().BeNull();
        data.HasData.Should().BeTrue();
        var saved = JsonSerializer.Serialize(data, AppJsonSerializerContext.Default.CostData);
        var loaded = JsonSerializer.Deserialize(saved, AppJsonSerializerContext.Default.CostData)!;
        loaded.Should().BeEquivalentTo(data);
    }

    [TestMethod]
    [DataRow("{\"daily\":[]}")]
    [DataRow("{\"daily\":null}")]
    [DataRow("{}")]
    public void ToCostData_WhenEmpty_HasSafeDefaults(string json)
    {
        var dto = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CostDataDto)!;
        var data = dto.ToCostData("gemini", DateTime.UtcNow);
        data.Daily.Should().BeEmpty();
        data.Coverage.Should().Be(new CostCoverage());
        data.CurrencyCode.Should().Be("USD");
        data.Provider.Should().Be("gemini");
        data.HasData.Should().BeFalse();
    }

    [TestMethod]
    public void HasData_WhenTokenOnly_IsVisible() =>
        new CostData { Last30DaysTokens = 100, Last30DaysCostUSD = 0 }.HasData.Should().BeTrue();
}
