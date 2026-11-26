using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ResetAnimationGateTests
{
    [TestMethod]
    [DataRow(ResetAnimationMode.Always, false, true)]
    [DataRow(ResetAnimationMode.Always, true, true)]
    [DataRow(ResetAnimationMode.Never, false, false)]
    [DataRow(ResetAnimationMode.Never, true, false)]
    [DataRow(ResetAnimationMode.WhenNotBatterySaver, false, true)]
    [DataRow(ResetAnimationMode.WhenNotBatterySaver, true, false)]
    [DataRow((ResetAnimationMode)99, false, true)]
    [DataRow((ResetAnimationMode)99, true, false)]
    public void ShouldAnimate_ModeAndBatteryState_ReturnsExpected(ResetAnimationMode mode, bool active, bool expected) =>
        ResetAnimationGate.ShouldAnimate(mode, active).Should().Be(expected);
}
