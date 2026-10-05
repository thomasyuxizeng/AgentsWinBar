using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

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
    public void GetDelay_MaximumInterval_DoesNotOverflow()
    {
        AdaptiveRefreshPolicy.GetDelay(TimeSpan.MaxValue, 4, true).Should().Be(TimeSpan.MaxValue);
    }
}
