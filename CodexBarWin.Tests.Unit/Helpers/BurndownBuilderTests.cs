using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class BurndownBuilderTests
{
    private static readonly DateTime Start = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
    private static UsageWindow Window => new() { WindowMinutes = 300, ResetAt = Start.AddHours(5) };
    private static UsageHistorySample Sample(int minutes, double percent) => new()
    { Timestamp = Start.AddMinutes(minutes), UsedPercent = percent, ResetsAt = Window.ResetAt, Provider = "claude", WindowKey = "session" };

    [TestMethod]
    public void Build_WhenCurrentWindowSamples_ReturnsElapsedFractionsGuideAndObservedProjection()
    {
        var series = BurndownBuilder.Build([Sample(60, 20), Sample(120, 50)], Window, Start.AddMinutes(130));
        series.Usage.Should().Equal(new BurndownPoint(0.2, 20), new BurndownPoint(0.4, 50));
        series.Guide.Should().Equal(new BurndownPoint(0, 0), new BurndownPoint(1, 100));
        series.ProjectedAtReset.Should().BeApproximately(140, 0.001);
        series.Projection[0].Should().Be(series.Usage[^1]);
        series.Projection[^1].ElapsedFraction.Should().Be(1);
        series.CapturedAt.Should().Be(Start.AddMinutes(120));
        series.IsReady.Should().BeTrue();
    }

    [TestMethod]
    public void Build_WhenZeroOrOneSample_IsCollectingWithoutProjection()
    {
        var empty = BurndownBuilder.Build([], Window, Start.AddMinutes(130));
        empty.IsReady.Should().BeFalse(); empty.Message.Should().Be("Collecting data");
        var one = BurndownBuilder.Build([Sample(60, 10)], Window, Start.AddMinutes(130));
        one.SampleCount.Should().Be(1); one.Projection.Should().BeEmpty(); one.ProjectedAtReset.Should().BeNull();
    }

    [TestMethod]
    public void Build_WhenResetMidSeries_OnlyIncludesCurrentResetAndTimeRange()
    {
        var samples = new[] { Sample(60, 99) with { ResetsAt = Start }, Sample(-1, 99), Sample(10, 2), Sample(20, 4), Sample(250, 99) };
        var series = BurndownBuilder.Build(samples, Window, Start.AddMinutes(30));
        series.SampleCount.Should().Be(2);
        series.Usage.Select(p => p.UsedPercent).Should().Equal(2, 4);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Build_WhenWindowLengthUnknown_ExplainsMissingTiming(int minutes) =>
        BurndownBuilder.Build([], Window with { WindowMinutes = minutes }, Start).Message.Should().Be("Window timing unavailable");

    [TestMethod]
    public void Build_WhenResetUnknownOrOverflowing_IsUnavailable()
    {
        BurndownBuilder.Build([], Window with { ResetAt = null }, Start).Message.Should().Be("Window timing unavailable");
        BurndownBuilder.Build([], Window with { ResetAt = DateTime.MinValue, WindowMinutes = int.MaxValue }, Start).IsReady.Should().BeFalse();
    }

    [TestMethod]
    public void Build_WhenClockBeforeStartOrResetExpired_ShowsWaitingState()
    {
        BurndownBuilder.Build([], Window, Start.AddSeconds(-1)).Message.Should().Be("Window has not started");
        BurndownBuilder.Build([], Window, Window.ResetAt!.Value).Message.Should().Be("Waiting for window reset");
    }

    [TestMethod]
    public void Build_WhenDuplicateTimestampsOrInvalidPercents_UsesLastValidPointWithoutZeroDurationRate()
    {
        var series = BurndownBuilder.Build([Sample(60, 10), Sample(60, 15), Sample(90, double.NaN), Sample(100, 101)], Window, Start.AddMinutes(120));
        series.SampleCount.Should().Be(1);
        series.Usage[0].UsedPercent.Should().Be(15);
        series.Projection.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(15d, 15d)]
    [DataRow(10d, 10d)]
    public void Build_WhenFlatOrCorrectedUsage_DoesNotProjectNegativeConsumption(double latest, double projected)
    {
        var series = BurndownBuilder.Build([Sample(60, 15), Sample(120, latest)], Window, Start.AddMinutes(130));
        series.ProjectedAtReset.Should().Be(projected);
    }

    [TestMethod]
    public void Build_WhenSamplesUnsorted_NormalizesToChronologicalOrder() =>
        BurndownBuilder.Build([Sample(120, 50), Sample(60, 20)], Window, Start.AddMinutes(130))
            .Usage.Select(p => p.ElapsedFraction).Should().Equal(0.2, 0.4);

    [TestMethod]
    public void Build_MixedTimestampKinds_SortsByInstantAndCapturesUtc()
    {
        var samples = new[] { Sample(120, 50) with { Timestamp = Sample(120, 50).Timestamp.ToLocalTime() },
            Sample(60, 20) with { Timestamp = DateTime.SpecifyKind(Sample(60, 20).Timestamp, DateTimeKind.Unspecified) } };
        var series = BurndownBuilder.Build(samples, Window, Start.AddMinutes(130));
        series.Usage.Select(p => p.UsedPercent).Should().Equal(20, 50);
        series.ProjectedAtReset.Should().BeApproximately(140, 0.001);
        series.CapturedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [TestMethod]
    public void Build_AdjacentTicksInLongWindow_ProducesFiniteProjection()
    {
        var now = new DateTime(8000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var window = new UsageWindow { WindowMinutes = int.MaxValue, ResetAt = now.AddDays(1) };
        var series = BurndownBuilder.Build([
            new() { Timestamp = now.AddTicks(-1), ResetsAt = window.ResetAt, UsedPercent = 10 },
            new() { Timestamp = now, ResetsAt = window.ResetAt, UsedPercent = 20 }], window, now);
        double.IsFinite(series.ProjectedAtReset!.Value).Should().BeTrue();
        series.ProjectedAtReset.Should().BeGreaterThan(100);
        series.Projection.Should().OnlyContain(p => double.IsFinite(p.UsedPercent) && double.IsFinite(p.ElapsedFraction));
    }

    [TestMethod]
    public void Build_ThousandsOfSamples_ReturnsOrderedFiniteSeries()
    {
        var samples = Enumerable.Range(0, 5000).Select(i => new UsageHistorySample {
            Timestamp = Start.AddSeconds(i), ResetsAt = Window.ResetAt, UsedPercent = i / 100d }).Reverse();
        var series = BurndownBuilder.Build(samples, Window, Start.AddMinutes(100));
        series.SampleCount.Should().Be(5000);
        series.Usage.Should().BeInAscendingOrder(p => p.ElapsedFraction);
        double.IsFinite(series.ProjectedAtReset!.Value).Should().BeTrue();
    }
}
