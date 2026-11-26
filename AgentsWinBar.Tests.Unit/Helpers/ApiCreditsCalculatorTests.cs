using System.Globalization;
using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ApiCreditsCalculatorTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void BuildOk_MidCycle_ReturnsUtcWindowAndPace()
    {
        var info = ApiCreditsCalculator.BuildOk(200, 95.33m, 1, Now, Now);
        info.State.Should().Be(ApiCreditsState.Ok);
        info.CycleStart.Should().Be(new DateOnly(2026, 10, 1));
        info.CycleEnd.Should().Be(new DateOnly(2026, 11, 1));
        info.DaysLeft.Should().Be(17);
        info.RemainingUsd.Should().Be(104.67m);
        info.PaceUsdPerDay.Should().Be(104.67m / 17);
        info.PercentUsed.Should().BeApproximately(47.665, 0.000001);
        info.FetchedAtUtc.Should().Be(Now);
        info.IsStale.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(2026, 2, 28, 31, 2026, 1, 31, 2026, 2, 28)]
    [DataRow(2026, 3, 15, 31, 2026, 2, 28, 2026, 3, 31)]
    [DataRow(2028, 3, 1, 31, 2028, 2, 29, 2028, 3, 31)]
    [DataRow(2026, 1, 3, 10, 2025, 12, 10, 2026, 1, 10)]
    public void GetCycleWindow_BeforeStartDay_ClampsEachMonthIndependently(int y, int m, int d, int day,
        int sy, int sm, int sd, int ey, int em, int ed)
    {
        var result = ApiCreditsCalculator.GetCycleWindow(day, new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1));
        result.StartUtc.Should().Be(new DateTime(sy, sm, sd, 0, 0, 0, DateTimeKind.Utc));
        result.EndUtc.Should().Be(new DateTime(ey, em, ed, 0, 0, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    public void GetCycleWindow_ExactlyMidnight_StartsNewCycle()
    {
        var now = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        ApiCreditsCalculator.GetCycleWindow(12, now).StartUtc.Should().Be(now);
        ApiCreditsCalculator.GetCycleWindow(12, now.AddTicks(-1)).StartUtc.Should().Be(now.AddMonths(-1));
        ApiCreditsCalculator.GetCycleWindow(12, now.ToLocalTime()).StartUtc.Should().Be(now);
    }

    [TestMethod]
    [DataRow(200, 0, 100, false)]
    [DataRow(250, -50, 100, true)]
    [DataRow(-10, 210, 0, false)]
    public void BuildOk_BudgetBoundaries_ClampsPercentAndNonpositivePace(int spent, int remaining, int percent, bool over)
    {
        var info = ApiCreditsCalculator.BuildOk(200, spent, 1, Now, Now);
        info.RemainingUsd.Should().Be(remaining); info.PercentUsed.Should().Be(percent);
        info.IsOverBudget.Should().Be(over);
        info.PaceUsdPerDay.Should().Be(Math.Max(remaining, 0) / (decimal)info.DaysLeft);
    }

    [TestMethod]
    public void BuildOk_JustBeforeReset_DaysLeftIsOne()
    {
        var info = ApiCreditsCalculator.BuildOk(200, 0, 1, new DateTime(2026, 10, 31, 23, 59, 59, DateTimeKind.Utc), Now);
        info.DaysLeft.Should().Be(1);
    }

    [TestMethod]
    public void BuildOk_FifteenMinuteThreshold_MarksOnlyOlderFetchStale()
    {
        ApiCreditsCalculator.BuildOk(200, 1, 1, Now, Now.AddMinutes(-15)).IsStale.Should().BeFalse();
        ApiCreditsCalculator.BuildOk(200, 1, 1, Now, Now.AddMinutes(-15).AddTicks(-1)).IsStale.Should().BeTrue();
    }

    [TestMethod]
    public void TryParseCostReport_DecimalCentsUnderOtherCulture_PreservesPrecision()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            ApiCreditsCalculator.TryParseCostReport("""{"data":[{"results":[{"amount":"9533.0","currency":"USD"}]}],"has_more":false}""",
                out var cents, out var page, out var more).Should().BeTrue();
            cents.Should().Be(9533m); (cents / 100).Should().Be(95.33m);
            page.Should().BeNull(); more.Should().BeFalse();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    public void TryParseCostReport_MultipleBucketsAndCurrencies_SumsOnlyUsdAndReturnsCursor()
    {
        const string json = """{"data":[{"results":[]},{"results":[{"amount":"123.78912","currency":"USD"},{"amount":"junk","currency":"EUR"}]},{"results":[{"amount":"4.5","currency":"USD"}]}],"has_more":true,"next_page":"page two"}""";
        ApiCreditsCalculator.TryParseCostReport(json, out var total, out var page, out var more).Should().BeTrue();
        total.Should().Be(128.28912m); page.Should().Be("page two"); more.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("{\"data\":[],\"has_more\":false}")]
    [DataRow("{\"data\":[{\"results\":[]}],\"has_more\":false}")]
    public void TryParseCostReport_EmptyResults_ReturnsZero(string json)
    {
        ApiCreditsCalculator.TryParseCostReport(json, out var cents, out _, out _).Should().BeTrue();
        cents.Should().Be(0);
    }

    [TestMethod]
    [DataRow("bad json")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"data\":[null],\"has_more\":false}")]
    [DataRow("{\"data\":[{\"results\":[null]}],\"has_more\":false}")]
    [DataRow("{\"data\":[{\"results\":[{\"amount\":\"1,23\",\"currency\":\"USD\"}]}],\"has_more\":false}")]
    [DataRow("{\"data\":[{\"results\":[{\"amount\":9533,\"currency\":\"USD\"}]}],\"has_more\":false}")]
    [DataRow("{\"data\":[{\"results\":[{\"amount\":\"79228162514264337593543950335\",\"currency\":\"USD\"},{\"amount\":\"1\",\"currency\":\"USD\"}]}],\"has_more\":false}")]
    public void TryParseCostReport_MalformedReport_ReturnsFalseWithoutThrowing(string json)
    {
        ApiCreditsCalculator.TryParseCostReport(json, out var total, out _, out _).Should().BeFalse();
        total.Should().Be(0);
    }

    [TestMethod]
    public void BuildState_NonOkStates_HasSafeMessagesAndNoFetchTimestamp()
    {
        var missing = ApiCreditsCalculator.BuildNoKey(200, 1, Now);
        missing.State.Should().Be(ApiCreditsState.NoKey); missing.FetchedAtUtc.Should().BeNull();
        missing.Message.Should().Contain("ANTHROPIC_ADMIN_KEY");
        var rejected = ApiCreditsCalculator.BuildRejected(200, 1, Now, 403);
        rejected.State.Should().Be(ApiCreditsState.Rejected); rejected.Message.Should().Contain("403");
        var unavailable = ApiCreditsCalculator.BuildUnavailable(200, 1, Now);
        unavailable.State.Should().Be(ApiCreditsState.Unavailable);
        unavailable.Message.Should().Be("Anthropic API spend is unavailable right now");
    }

    [TestMethod]
    public void BuildUnavailable_WhenCalled_ReturnsUnavailableStateAndSpendUnavailableMessage()
    {
        var info = ApiCreditsCalculator.BuildUnavailable(200, 1, Now);
        info.State.Should().Be(ApiCreditsState.Unavailable);
        info.Message.Should().Be("Anthropic API spend is unavailable right now");
        info.FetchedAtUtc.Should().BeNull();
    }
}
