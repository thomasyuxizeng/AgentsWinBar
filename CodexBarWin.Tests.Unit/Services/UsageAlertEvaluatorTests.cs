using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class UsageAlertEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A session window of <paramref name="windowMinutes"/> with the given usage and time left.</summary>
    private static UsageData Provider(string id, double used, int minutesLeft, int windowMinutes = 300) => new()
    {
        Provider = id,
        Session = new UsageWindow
        {
            Used = (int)used,
            Limit = 100,
            WindowMinutes = windowMinutes,
            ResetAt = Now.AddMinutes(minutesLeft)
        }
    };

    [TestMethod]
    public void Evaluate_WhenUsageIsComfortable_ReturnsNothing()
    {
        // 20% used, 60% of the window gone: slow
        var alerts = new UsageAlertEvaluator().Evaluate([Provider("claude", 20, 120)], Now);

        alerts.Should().BeEmpty();
    }

    [TestMethod]
    public void Evaluate_WhenFarAheadOfPaceAndWillRunOut_WarnsOnce()
    {
        var evaluator = new UsageAlertEvaluator();
        var data = Provider("claude", 60, 210);   // 60% used, 30% of the time gone: out in 60 min, 150 min early

        var first = evaluator.Evaluate([data], Now);
        var second = evaluator.Evaluate([data], Now.AddMinutes(1));

        first.Should().ContainSingle();
        first[0].Title.Should().Be("Claude session: using it fast");
        first[0].Message.Should().Contain("hit the limit in 1h 0m");
        second.Should().BeEmpty("the same situation in the same window is only reported once");
    }

    [TestMethod]
    public void Evaluate_WhenOnlySlightlyAhead_DoesNotWarn()
    {
        // 55% used, 40% of the time gone: ahead, but by less than 25 points
        var alerts = new UsageAlertEvaluator().Evaluate([Provider("claude", 55, 180)], Now);

        alerts.Should().BeEmpty();
    }

    [TestMethod]
    public void Evaluate_WhenResetIsImminentAnyway_DoesNotWarnAboutPace()
    {
        // 85% used, 40% of the 300-minute window gone... but only 20 minutes left means the limit
        // is hit at most ~minutes before reset; use a tiny window to make the gap under 30 minutes
        var alerts = new UsageAlertEvaluator().Evaluate([Provider("claude", 75, 20, windowMinutes: 60)], Now);

        alerts.Should().BeEmpty();
    }

    [TestMethod]
    public void Evaluate_WhenNearLimit_AlertsEvenIfOnPace()
    {
        var alerts = new UsageAlertEvaluator().Evaluate([Provider("gemini", 92, 10)], Now);

        alerts.Should().ContainSingle();
        alerts[0].Title.Should().Be("Gemini 5-hour usage");   // Gemini labels its session window "5-hour"
        alerts[0].Message.Should().Be("92% used, resets in 10m");
    }

    [TestMethod]
    public void Evaluate_WhenLimitReached_AlertsAsLimitAndSkipsLowerLevels()
    {
        var evaluator = new UsageAlertEvaluator();

        var alerts = evaluator.Evaluate([Provider("claude", 100, 90)], Now);

        alerts.Should().ContainSingle();
        alerts[0].Title.Should().Be("Claude session limit reached");
        alerts[0].Message.Should().Contain("resets in 1h 30m");
    }

    [TestMethod]
    public void Evaluate_WhenSituationEscalates_AlertsAgainAtTheHigherLevel()
    {
        var evaluator = new UsageAlertEvaluator();

        var pace = evaluator.Evaluate([Provider("claude", 60, 210)], Now);
        var near = evaluator.Evaluate([Provider("claude", 91, 100)], Now.AddMinutes(30));

        pace.Should().ContainSingle();
        near.Should().ContainSingle();
        near[0].Message.Should().StartWith("91% used");
    }

    [TestMethod]
    public void Evaluate_WhenNewWindowStarts_AlertsAgain()
    {
        var evaluator = new UsageAlertEvaluator();

        evaluator.Evaluate([Provider("claude", 92, 10)], Now).Should().ContainSingle();
        // Five hours later the session window has reset and usage is high again
        var later = Now.AddHours(5);
        var again = new UsageData
        {
            Provider = "claude",
            Session = new UsageWindow { Used = 93, Limit = 100, WindowMinutes = 300, ResetAt = later.AddMinutes(10) }
        };

        evaluator.Evaluate([again], later).Should().ContainSingle();
    }

    [TestMethod]
    public void Evaluate_WhenResetTimeJittersWithinTheSameWindow_DoesNotRepeat()
    {
        var evaluator = new UsageAlertEvaluator();

        evaluator.Evaluate([Provider("claude", 92, 100)], Now).Should().ContainSingle();
        evaluator.Evaluate([Provider("claude", 93, 99)], Now.AddMinutes(1)).Should().BeEmpty();
    }

    [TestMethod]
    public void Evaluate_IgnoresProvidersWithErrorsOrStillLoading()
    {
        var failed = Provider("claude", 99, 10) with { Error = "boom" };
        var loading = Provider("gemini", 99, 10) with { IsLoading = true };

        new UsageAlertEvaluator().Evaluate([failed, loading], Now).Should().BeEmpty();
    }

    [TestMethod]
    public void Evaluate_CoversTheWeeklyWindowToo()
    {
        var data = new UsageData
        {
            Provider = "codex",
            Weekly = new UsageWindow { Used = 95, Limit = 100, WindowMinutes = 10080, ResetAt = Now.AddHours(30) }
        };

        var alerts = new UsageAlertEvaluator().Evaluate([data], Now);

        alerts.Should().ContainSingle();
        alerts[0].Title.Should().Be("Codex weekly usage");
        alerts[0].Message.Should().Be("95% used, resets in 1d 6h");
    }
}
