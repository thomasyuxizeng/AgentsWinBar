using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class PaceCalculatorTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Project_WhenWindowOrResetUnknown_ReturnsUnknown()
    {
        PaceCalculator.Project(50, 0, Now.AddHours(1), Now).Kind.Should().Be(PaceKind.Unknown);
        PaceCalculator.Project(50, 300, null, Now).Kind.Should().Be(PaceKind.Unknown);
    }

    [TestMethod]
    public void Project_WhenWindowAlreadyEnded_ReturnsUnknown()
    {
        PaceCalculator.Project(50, 300, Now.AddMinutes(-1), Now).Kind.Should().Be(PaceKind.Unknown);
    }

    [TestMethod]
    public void Project_WhenAtOrOverLimit_ReturnsLimitReached()
    {
        var result = PaceCalculator.Project(100, 300, Now.AddMinutes(90), Now);

        result.Kind.Should().Be(PaceKind.LimitReached);
        result.MinutesUntilReset.Should().BeApproximately(90, 0.001);
    }

    [TestMethod]
    public void Project_WhenNothingUsedOrWindowJustStarted_ReturnsNotStarted()
    {
        PaceCalculator.Project(0, 300, Now.AddMinutes(150), Now).Kind.Should().Be(PaceKind.NotStarted);
        // 2 minutes into a 5-hour window is too early to project
        PaceCalculator.Project(10, 300, Now.AddMinutes(298), Now).Kind.Should().Be(PaceKind.NotStarted);
    }

    [TestMethod]
    public void Project_WhenUsingFasterThanTime_PredictsRunOutBeforeReset()
    {
        // 60% used with 30% of a 300-minute window gone (90 min): rate = 2/3 %/min, 40% left = 60 minutes,
        // 210 minutes remain, so the limit is hit 150 minutes before the reset
        var result = PaceCalculator.Project(60, 300, Now.AddMinutes(210), Now);

        result.Kind.Should().Be(PaceKind.WillRunOut);
        result.MinutesToLimit.Should().BeApproximately(60, 0.001);
        result.MinutesBeforeReset.Should().BeApproximately(150, 0.001);
    }

    [TestMethod]
    public void Project_WhenUsingSlowerThanTime_IsOnTrackWithProjectedFinalUsage()
    {
        // 20% used with 60% of the window gone (180 of 300 min): rate = 1/9 %/min, 120 min left = +13.33%
        var result = PaceCalculator.Project(20, 300, Now.AddMinutes(120), Now);

        result.Kind.Should().Be(PaceKind.OnTrack);
        result.ProjectedUsedAtReset.Should().BeApproximately(33.33, 0.01);
    }

    [TestMethod]
    public void Project_WhenExactlyOnPace_IsOnTrackAtOneHundred()
    {
        var result = PaceCalculator.Project(50, 300, Now.AddMinutes(150), Now);

        result.Kind.Should().Be(PaceKind.OnTrack);
        result.ProjectedUsedAtReset.Should().BeApproximately(100, 0.001);
    }

    [TestMethod]
    [DataRow(0.4, "<1m")]
    [DataRow(12, "12m")]
    [DataRow(59.9, "59m")]
    [DataRow(60, "1h 0m")]
    [DataRow(299, "4h 59m")]
    [DataRow(24 * 60, "1d 0h")]
    [DataRow(2 * 24 * 60 + 3 * 60 + 40, "2d 3h")]
    public void FormatDuration_ReturnsCompactText(double minutes, string expected)
    {
        PaceCalculator.FormatDuration(minutes).Should().Be(expected);
    }

    [TestMethod]
    public void Summarize_DescribesEachOutcome()
    {
        PaceCalculator.Summarize(new PaceProjection(PaceKind.Unknown)).Should().BeEmpty();
        PaceCalculator.Summarize(new PaceProjection(PaceKind.NotStarted)).Should().Contain("Not enough usage");
        PaceCalculator.Summarize(new PaceProjection(PaceKind.LimitReached, MinutesUntilReset: 90))
            .Should().Be("Limit reached, resets in 1h 30m");
        PaceCalculator.Summarize(new PaceProjection(PaceKind.WillRunOut, MinutesToLimit: 80, MinutesBeforeReset: 190))
            .Should().Be("At this pace you'll hit the limit in 1h 20m, 3h 10m before the reset");
        PaceCalculator.Summarize(new PaceProjection(PaceKind.OnTrack, ProjectedUsedAtReset: 61.6))
            .Should().Be("At this pace you'll use about 62% by the reset");
    }

    [TestMethod]
    public void UsageWindow_PaceSummary_UsesItsOwnPercentWindowAndReset()
    {
        var window = new UsageWindow { Used = 60, Limit = 100, WindowMinutes = 300, ResetAt = DateTime.UtcNow.AddMinutes(210) };

        window.Pace.Kind.Should().Be(PaceKind.WillRunOut);
        window.PaceSummary.Should().StartWith("At this pace you'll hit the limit in");
    }

    [TestMethod]
    public void FormatDuration_ExtremeOrNonFiniteMinutes_DoesNotOverflow()
    {
        PaceCalculator.FormatDuration((DateTime.MaxValue - DateTime.MinValue).TotalMinutes).Should().Contain("d ");
        PaceCalculator.FormatDuration(double.NaN).Should().Be("<1m");
        PaceCalculator.FormatDuration(double.PositiveInfinity).Should().Be("<1m");
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Project_NonFiniteUsage_ReturnsUnknown(double percent)
    {
        var now = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        PaceCalculator.Project(percent, 300, now.AddMinutes(100), now).Kind.Should().Be(PaceKind.Unknown);
    }
}
