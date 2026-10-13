using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class BarResetAnimationPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void ShouldPlay_NullValue_ReturnsFalse()
    {
        BarResetAnimationPolicy.ShouldPlay(null, Now.AddSeconds(-1), Now, true, true).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldPlay_RecentTimestamp_ReturnsTrue()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddSeconds(-3), null, Now, true, true).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldPlay_JustDetected_ReturnsTrue()
    {
        BarResetAnimationPolicy.ShouldPlay(Now, null, Now, true, true).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldPlay_TimestampJustInsideMaxAge_ReturnsTrue()
    {
        BarResetAnimationPolicy.ShouldPlay(Now - BarResetAnimationPolicy.MaxAge, null, Now, true, true).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldPlay_TimestampOlderThanMaxAge_ReturnsFalse()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddSeconds(-16), null, Now, true, true).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldPlay_AppStartsWithOldTimestamp_ReturnsFalse()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddHours(-5), null, Now, true, true).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldPlay_SmallFutureSkew_ReturnsTrue()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddSeconds(1), null, Now, true, true).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldPlay_LargeFutureSkew_ReturnsFalse()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddMinutes(5), null, Now, true, true).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldPlay_SameAsOldValue_ReturnsFalse()
    {
        var stamp = Now.AddSeconds(-2);
        BarResetAnimationPolicy.ShouldPlay(stamp, stamp, Now, true, true).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldPlay_NewerThanOldValue_ReturnsTrue()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddSeconds(-1), Now.AddSeconds(-10), Now, true, true).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldPlay_AnimationsDisabled_ReturnsFalse()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddSeconds(-1), null, Now, false, true).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldPlay_NotShown_ReturnsFalse()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddSeconds(-1), null, Now, true, false).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldPlay_LocalKindTimestamp_ComparesInUtc()
    {
        var local = Now.AddSeconds(-2).ToLocalTime();
        BarResetAnimationPolicy.ShouldPlay(local, null, Now, true, true).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldPlay_PlaybackDisabled_ReturnsFalse()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddSeconds(-1), null, Now, true, true, playbackEnabled: false).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldPlay_PlaybackEnabled_ReturnsTrue()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddSeconds(-1), null, Now, true, true, playbackEnabled: true).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldPlay_PlaybackDefaultsToEnabled()
    {
        BarResetAnimationPolicy.ShouldPlay(Now.AddSeconds(-1), null, Now, true, true).Should().BeTrue();
    }
}
