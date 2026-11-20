using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class GeminiSpendCalculatorTests
{
    private static readonly TimeZoneInfo UtcZone = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo BrisbaneZone = TimeZoneInfo.CreateCustomTimeZone("BrisbaneZone", TimeSpan.FromHours(10), "Brisbane", "Brisbane");
    private static readonly TimeZoneInfo PacificStandardZone = TimeZoneInfo.CreateCustomTimeZone("PstZone", TimeSpan.FromHours(-8), "PST", "PST");

    [TestMethod]
    public void Build_EmptyInput_ReturnsOkStateWithZeroAmounts()
    {
        var nowUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var result = GeminiSpendCalculator.Build(string.Empty, nowUtc, UtcZone, nowUtc);

        result.State.Should().Be(GeminiApiSpendState.Ok);
        result.TotalUsd.Should().Be(0m);
        result.VertexUsd.Should().Be(0m);
        result.AiStudioUsd.Should().Be(0m);
        result.UnrecordedRouteUsd.Should().Be(0m);
        result.Calls.Should().Be(0);
        result.Models.Should().BeEmpty();
        result.LastCallUtc.Should().BeNull();
        result.MonthStart.Should().Be(new DateOnly(2026, 11, 1));
        result.MonthEnd.Should().Be(new DateOnly(2026, 12, 1));
        result.DaysLeft.Should().Be(11);
        result.FetchedAtUtc.Should().Be(nowUtc);
    }

    [TestMethod]
    public void ParseEntries_RowHasCacheAwareAmount_PrefersItOverUpperBound()
    {
        var text = """
            {"time": "2026-11-05T10:00:00", "model": "gemini-4-neon", "usd": 10.7313, "usd_cache_aware": 3.4305}
            {"time": "2026-11-05T11:00:00", "model": "gemini-4-neon", "usd": 0.5}
            {"time": "2026-11-05T12:00:00", "model": "gemini-4-neon", "usd": null, "usd_cache_aware": 0.25}
            """;

        var entries = GeminiSpendCalculator.ParseEntries(text);

        entries.Select(e => e.Usd).Should().Equal(3.4305m, 0.5m, 0.25m);
    }

    [TestMethod]
    public void Build_MonthBoundariesInUtc_CountsOnlyRowsWithinCurrentMonth()
    {
        var nowUtc = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
        var log = """
            {"time": "2026-10-31T23:59:59", "model": "gemini-4-neon", "usd": 1.0}
            {"time": "2026-11-01T00:00:00", "model": "gemini-4-neon", "usd": 2.0}
            {"time": "2026-11-30T23:59:59", "model": "gemini-4-neon", "usd": 4.0}
            {"time": "2026-12-01T00:00:00", "model": "gemini-4-neon", "usd": 8.0}
            """;

        var result = GeminiSpendCalculator.Build(log, nowUtc, UtcZone, nowUtc);

        result.State.Should().Be(GeminiApiSpendState.Ok);
        result.Calls.Should().Be(2);
        result.TotalUsd.Should().Be(6.0m);
        result.LastCallUtc.Should().Be(new DateTime(2026, 11, 30, 23, 59, 59, DateTimeKind.Utc));
    }

    [TestMethod]
    public void Build_MonthBoundariesInDifferentTimeZones_InterpretsNaiveTimeInSpecifiedZone()
    {
        // 2026-11-01 01:00:00 UTC:
        // In Brisbane (+10): 2026-11-01 11:00:00 -> Month is November 2026
        // In PST (-8): 2026-10-31 17:00:00 -> Month is October 2026
        var nowUtc = new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc);
        var log = """
            {"time": "2026-10-31T20:00:00", "model": "gemini-4-neon", "usd": 1.5}
            {"time": "2026-11-01T08:00:00", "model": "gemini-4-carbon", "usd": 2.5}
            """;

        var pstResult = GeminiSpendCalculator.Build(log, nowUtc, PacificStandardZone, nowUtc);
        pstResult.MonthStart.Should().Be(new DateOnly(2026, 10, 1));
        pstResult.MonthEnd.Should().Be(new DateOnly(2026, 11, 1));
        pstResult.Calls.Should().Be(1);
        pstResult.TotalUsd.Should().Be(1.5m);
        pstResult.Models.Single().Model.Should().Be("gemini-4-neon");
        pstResult.LastCallUtc.Should().Be(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc));

        var brisbaneResult = GeminiSpendCalculator.Build(log, nowUtc, BrisbaneZone, nowUtc);
        brisbaneResult.MonthStart.Should().Be(new DateOnly(2026, 11, 1));
        brisbaneResult.MonthEnd.Should().Be(new DateOnly(2026, 12, 1));
        brisbaneResult.Calls.Should().Be(1);
        brisbaneResult.TotalUsd.Should().Be(2.5m);
        brisbaneResult.Models.Single().Model.Should().Be("gemini-4-carbon");
        brisbaneResult.LastCallUtc.Should().Be(new DateTime(2026, 10, 31, 22, 0, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    public void Build_InvalidCorruptNullOrNegativeLines_SkipsThemAndPreservesValidRows()
    {
        var nowUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var log = """

            not valid json at all
            {"time": "invalid-time", "model": "gemini-4-neon", "usd": 1.0}
            {"model": "gemini-4-neon", "usd": 1.0}
            {"time": "2026-11-19T10:00:00", "usd": 1.0}
            {"time": "2026-11-19T10:00:00", "model": "", "usd": 1.0}
            {"time": "2026-11-19T10:00:00", "model": "gemini-4-neon", "usd": -5.0}
            {"time": "2026-11-19T10:00:00", "model": "gemini-4-neon", "usd": null}
            {"time": "2026-11-19T12:00:00", "model": "gemini-4-neon", "backend": "vertex", "usd": 0.005}
            {"time": "2026-11-19T13:00:00", "model": "gemini-4-neon", "usd": 0.0}
            """;

        var result = GeminiSpendCalculator.Build(log, nowUtc, UtcZone, nowUtc);

        result.State.Should().Be(GeminiApiSpendState.Ok);
        result.Calls.Should().Be(2);
        result.TotalUsd.Should().Be(0.005m);
        result.Models.Single().Calls.Should().Be(2);
    }

    [TestMethod]
    public void Build_RouteBuckets_CategorizesVertexAiStudioAndUnrecorded()
    {
        var nowUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var log = """
            {"time": "2026-11-10T10:00:00", "model": "gemini-4-neon", "backend": "vertex", "usd": 10.0}
            {"time": "2026-11-11T10:00:00", "model": "gemini-4-neon", "backend": "aistudio", "usd": 5.0}
            {"time": "2026-11-12T10:00:00", "model": "gemini-4-carbon", "backend": "unknown", "usd": 2.0}
            {"time": "2026-11-13T10:00:00", "model": "gemini-4-carbon", "usd": 1.0}
            """;

        var result = GeminiSpendCalculator.Build(log, nowUtc, UtcZone, nowUtc);

        result.VertexUsd.Should().Be(10.0m);
        result.AiStudioUsd.Should().Be(5.0m);
        result.UnrecordedRouteUsd.Should().Be(3.0m);
        result.TotalUsd.Should().Be(18.0m);
    }

    [TestMethod]
    public void Build_ModelOrdering_SortsDescendingByUsdThenByName()
    {
        var nowUtc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var log = """
            {"time": "2026-11-10T10:00:00", "model": "alpha-model", "usd": 5.0}
            {"time": "2026-11-11T10:00:00", "model": "beta-model", "usd": 10.0}
            {"time": "2026-11-12T10:00:00", "model": "gamma-model", "usd": 5.0}
            {"time": "2026-11-13T10:00:00", "model": "alpha-model", "usd": 5.0}
            """;

        var result = GeminiSpendCalculator.Build(log, nowUtc, UtcZone, nowUtc);

        result.Models.Should().HaveCount(3);
        result.Models[0].Model.Should().Be("alpha-model");
        result.Models[0].Usd.Should().Be(10.0m);
        result.Models[0].Calls.Should().Be(2);

        result.Models[1].Model.Should().Be("beta-model");
        result.Models[1].Usd.Should().Be(10.0m);
        result.Models[1].Calls.Should().Be(1);

        result.Models[2].Model.Should().Be("gamma-model");
        result.Models[2].Usd.Should().Be(5.0m);
        result.Models[2].Calls.Should().Be(1);
    }

    [TestMethod]
    public void Build_DaysLeftOnLastDayOfMonth_ReturnsAtLeastOne()
    {
        var nowUtc = new DateTime(2026, 11, 30, 23, 59, 59, DateTimeKind.Utc);
        var result = GeminiSpendCalculator.Build(string.Empty, nowUtc, UtcZone, nowUtc);

        result.DaysLeft.Should().Be(1);
    }

    [TestMethod]
    public void ConvertNaiveToUtc_DstGap_TreatsAsNextValidInstantWithoutThrowing()
    {
        // America/New_York DST spring forward: 2026-03-08 02:00 -> 03:00
        var nyZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var invalidLocal = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);

        var utc = GeminiSpendCalculator.ConvertNaiveToUtc(invalidLocal, nyZone);

        // Next valid instant is 03:00 local (which is 07:00 UTC under EDT)
        utc.Kind.Should().Be(DateTimeKind.Utc);
        utc.Should().Be(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc));
    }
}
