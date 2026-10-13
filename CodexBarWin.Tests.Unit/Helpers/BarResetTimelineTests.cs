using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class BarResetTimelineTests
{
    [TestMethod]
    public void Phases_Default_MatchTheAgreedTimeline()
    {
        (BarResetTimeline.RingStartMs, BarResetTimeline.RingFullMs).Should().Be((0, 900));
        (BarResetTimeline.FillStartMs, BarResetTimeline.FillEndMs).Should().Be((900, 1400));
        (BarResetTimeline.RingFadeStartMs, BarResetTimeline.RingFadeEndMs).Should().Be((900, 1400));
        (BarResetTimeline.CollapseStartMs, BarResetTimeline.CollapseEndMs).Should().Be((1400, 2300));
        (BarResetTimeline.SweepStartMs, BarResetTimeline.SweepEndMs).Should().Be((2250, 3500));
        BarResetTimeline.TotalMs.Should().Be(3500);
    }

    [TestMethod]
    public void Phases_Default_RunInTheAgreedOrder()
    {
        BarResetTimeline.RingStartMs.Should().BeLessThan(BarResetTimeline.RingFullMs);
        BarResetTimeline.RingFullMs.Should().BeLessThanOrEqualTo(BarResetTimeline.FillStartMs);
        BarResetTimeline.FillStartMs.Should().BeLessThan(BarResetTimeline.FillEndMs);
        BarResetTimeline.CollapseStartMs.Should().BeGreaterThanOrEqualTo(BarResetTimeline.FillEndMs);
        BarResetTimeline.TotalMs.Should().Be(Math.Max(BarResetTimeline.CollapseEndMs, BarResetTimeline.SweepEndMs));
    }

    [TestMethod]
    public void RingFade_Default_RunsTogetherWithTheFill()
    {
        BarResetTimeline.RingFadeStartMs.Should().Be(BarResetTimeline.FillStartMs);
        BarResetTimeline.RingFadeMs.Should().Be(BarResetTimeline.FillMs);
    }

    [TestMethod]
    public void CollapseAndSweep_Default_AreConsecutiveWithOnlyATinyOverlap()
    {
        BarResetTimeline.CollapseStartMs.Should().Be(BarResetTimeline.FillEndMs, "there is no hold between the fill and the collapse");
        BarResetTimeline.SweepStartMs.Should().BeInRange(BarResetTimeline.CollapseEndMs - 100, BarResetTimeline.CollapseEndMs);
        BarResetTimeline.SweepEndMs.Should().BeGreaterThan(BarResetTimeline.CollapseEndMs);
    }

    [TestMethod]
    public void Handoff_Default_GlowFadesInWhileTheGreenSliverFadesOut()
    {
        // The green's last sliver fades (CollapseFadeStart..CollapseEnd) while the glow fades in (SweepStart..+GlowReveal)
        BarResetTimeline.SweepStartMs.Should().BeGreaterThanOrEqualTo(BarResetTimeline.CollapseFadeStartMs);
        BarResetTimeline.SweepStartMs.Should().BeLessThanOrEqualTo(BarResetTimeline.CollapseEndMs);
        (BarResetTimeline.SweepStartMs + BarResetTimeline.GlowRevealMs).Should().BeGreaterThan(BarResetTimeline.CollapseEndMs);
        BarResetTimeline.GlowRevealMs.Should().BeLessThan((int)(BarResetTimeline.SweepMs * (1 - BarResetTimeline.SweepFadeShare)));
    }

    [TestMethod]
    public void Phases_Default_HaveExpectedDurations()
    {
        BarResetTimeline.RingMs.Should().Be(900);
        BarResetTimeline.FillMs.Should().Be(500);
        BarResetTimeline.CollapseMs.Should().Be(900);
        BarResetTimeline.SweepMs.Should().Be(1250);
        BarResetTimeline.TotalMs.Should().BeInRange(3400, 3600);
    }

    [TestMethod]
    public void RingRevealMs_Default_IsWithinTheFirstFramesOfTheDraw()
    {
        BarResetTimeline.RingRevealMs.Should().BeInRange(1, 60);
        BarResetTimeline.RingRevealMs.Should().BeLessThan(BarResetTimeline.RingFullMs);
    }

    [TestMethod]
    public void CollapseFadeStartMs_Default_IsTheLastTenthOfTheCollapse()
    {
        BarResetTimeline.CollapseFadeStartMs.Should().Be(BarResetTimeline.CollapseEndMs - 90);
        BarResetTimeline.CollapseFadeStartMs.Should().BeGreaterThan(BarResetTimeline.CollapseStartMs);
    }

    [TestMethod]
    public void SweepFadeShare_Default_FadesOnlyAtTheEnd()
    {
        BarResetTimeline.SweepFadeShare.Should().BeInRange(0.15f, 0.35f);
    }

    [TestMethod]
    public void GentleEasing_Default_IsMildEaseInOutWithoutOvershoot()
    {
        (BarResetTimeline.GentleX1, BarResetTimeline.GentleY1, BarResetTimeline.GentleX2, BarResetTimeline.GentleY2)
            .Should().Be((0.35f, 0f, 0.65f, 1f));
    }

    [TestMethod]
    public void SweepEasing_Default_StartsAtZeroSpeedLikeTheCollapseEndsWithoutOvershoot()
    {
        (BarResetTimeline.SweepX1, BarResetTimeline.SweepY1, BarResetTimeline.SweepX2, BarResetTimeline.SweepY2)
            .Should().Be((0.4f, 0f, 0.2f, 1f));
        // Zero speed at the handoff: the collapse curve ends flat (last control point on y=1), the sweep starts flat (y=0)
        BarResetTimeline.GentleY2.Should().Be(1f);
        BarResetTimeline.SweepY1.Should().Be(0f);
        // Control points inside the unit square: monotonic, no overshoot
        new[] { BarResetTimeline.SweepX1, BarResetTimeline.SweepY1, BarResetTimeline.SweepX2, BarResetTimeline.SweepY2 }
            .Should().AllSatisfy(v => v.Should().BeInRange(0f, 1f));
    }

    [TestMethod]
    [DataRow(0, 0f)]
    [DataRow(-50, 0f)]
    [DataRow(BarResetTimeline.TotalMs, 1f)]
    [DataRow(BarResetTimeline.TotalMs + 500, 1f)]
    public void Fraction_OutOfOrBoundaryValues_IsClampedToUnitRange(int ms, float expected)
    {
        BarResetTimeline.Fraction(ms).Should().Be(expected);
    }

    [TestMethod]
    public void Fraction_HalfOfTotal_IsHalf()
    {
        BarResetTimeline.Fraction(BarResetTimeline.TotalMs / 2).Should().BeApproximately(0.5f, 0.001f);
    }
}
