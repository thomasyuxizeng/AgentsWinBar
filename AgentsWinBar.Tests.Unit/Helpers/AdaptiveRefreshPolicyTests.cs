using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class AdaptiveRefreshPolicyTests
{
    [TestMethod]
    [DataRow(0, 300)]
    [DataRow(1, 300)]
    [DataRow(2, 600)]
    [DataRow(3, 600)]
    [DataRow(4, 900)]
    [DataRow(100, 900)]
    public void GetDelay_UnchangedStreak_UsesExpectedMultiplier(int streak, int seconds)
    {
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.FromSeconds(300), streak, true)
            .Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [TestMethod]
    public void GetDelay_MultipliedIntervalExceedsFifteenMinutes_CapsAtFifteenMinutes()
    {
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.FromMinutes(10), 2, true)
            .Should().Be(TimeSpan.FromMinutes(15));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(100)]
    public void GetDelay_Disabled_ReturnsBaseInterval(int streak)
    {
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.FromSeconds(300), streak, false)
            .Should().Be(TimeSpan.FromSeconds(300));
    }

    [TestMethod]
    public void GetDelay_BaseLargerThanFifteenMinutes_ReturnsBaseInterval()
    {
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.FromMinutes(20), 4, true)
            .Should().Be(TimeSpan.FromMinutes(20));
    }

    [TestMethod]
    [DataRow(0, true, 600)]
    [DataRow(2, true, 1200)]
    [DataRow(4, true, 1800)]
    [DataRow(0, false, 600)]
    [DataRow(4, false, 600)]
    public void GetDelay_BatterySaver_DoublesTheDelayUpToThirtyMinutes(int streak, bool adaptive, int seconds)
    {
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.FromSeconds(300), streak, adaptive, batterySaver: true)
            .Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [TestMethod]
    public void GetDelay_BatterySaverOff_IsUnchanged()
    {
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.FromSeconds(300), 4, true, batterySaver: false)
            .Should().Be(TimeSpan.FromSeconds(900));
    }

    [TestMethod]
    public void GetDelay_BatterySaverWithLargeBase_NeverShortensOrOverflows()
    {
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.FromMinutes(45), 4, true, batterySaver: true)
            .Should().Be(TimeSpan.FromMinutes(45));
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.MaxValue, 4, true, batterySaver: true).Should().Be(TimeSpan.MaxValue);
    }

    [TestMethod]
    public void GetDelay_MaximumInterval_DoesNotOverflow()
    {
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.MaxValue, 4, true).Should().Be(TimeSpan.MaxValue);
    }
}
