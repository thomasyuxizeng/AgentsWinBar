using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class CostSeriesBuilderTests
{
    private static readonly DateOnly Today = new(2026, 10, 4);

    [TestMethod]
    public void Daily_DuplicateDays_PreservesTokenBreakdownsAndModels()
    {
        var data = new CostData { Daily = [
            new() { Date = Today, InputTokens = 1, OutputTokens = 2, CacheReadTokens = 3, CacheCreationTokens = 4,
                ReasoningTokens = 5, ModelBreakdowns = [new() { ModelName = "a" }] },
            new() { Date = Today, InputTokens = 10, OutputTokens = 20, CacheReadTokens = 30, CacheCreationTokens = 40,
                ReasoningTokens = 50, ModelBreakdowns = [new() { ModelName = "b" }] }] };
        var day = CostSeriesBuilder.Daily(data, Today)[^1];
        day.InputTokens.Should().Be(11); day.OutputTokens.Should().Be(22); day.CacheReadTokens.Should().Be(33);
        day.CacheCreationTokens.Should().Be(44); day.ReasoningTokens.Should().Be(55);
        day.ModelBreakdowns.Select(m => m.ModelName).Should().Equal("a", "b");
    }

    [TestMethod]
    public void Daily_WhenGapsDuplicatesAndFutureDays_BuildsEvenCalendarSeries()
    {
        var data = new CostData { Daily = [
            new() { Date = Today, TotalCost = 2, TotalTokens = 20 },
            new() { Date = Today, TotalCost = 1, TotalTokens = 10 },
            new() { Date = Today.AddDays(-6), TotalCost = 5 },
            new() { Date = Today.AddDays(1), TotalCost = 99 },
            new() { Date = Today.AddDays(-10), TotalCost = 99 }] };
        var series = CostSeriesBuilder.Daily(data, Today, 7);
        series.Should().HaveCount(7);
        series[0].Date.Should().Be(Today.AddDays(-6));
        series[^1].TotalCost.Should().Be(3);
        series[^1].TotalTokens.Should().Be(30);
        series[1].TotalCost.Should().Be(0);
        series.Sum(d => d.TotalCost).Should().Be(8);
    }

    [TestMethod]
    [DataRow(0, 7)]
    [DataRow(14, 14)]
    [DataRow(999, 30)]
    public void Daily_WhenEmptyOrRangeOutsideBounds_UsesSevenToThirtyDays(int requested, int expected) =>
        CostSeriesBuilder.Daily(new CostData(), Today, requested).Should().HaveCount(expected);

    [TestMethod]
    public void TopModels_WhenMultipleDays_AggregatesOrdersAndPreservesUnpricedModels()
    {
        var data = new CostData { Daily = [
            new() { Date = Today, ModelBreakdowns = [new() { ModelName = "a", Cost = 2, TotalTokens = 10 }, new() { ModelName = "b", Cost = 6 }, new() { ModelName = "u", TotalTokens = 15 }] },
            new() { Date = Today.AddDays(-1), ModelBreakdowns = [new() { ModelName = "a", Cost = 5, TotalTokens = 20 }] },
            new() { Date = Today.AddDays(-30), ModelBreakdowns = [new() { ModelName = "old", Cost = 999 }] }] };
        var models = CostSeriesBuilder.TopModels(data, Today);
        models.Select(m => m.ModelName).Should().Equal("a", "b", "u");
        models[0].Cost.Should().Be(7);
        models[0].TotalTokens.Should().Be(30);
        models[2].Cost.Should().BeNull();
        CostSeriesBuilder.TopModels(data, Today, 1).Should().ContainSingle();
    }
}
