using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ResetPlaybackGateTests
{
    private static readonly DateTime Detected = new(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Back = Detected.AddHours(6);
    private static readonly DateTime Released = Back.AddSeconds(1);

    [TestMethod]
    public void Resolve_WhenNoReset_ReturnsNull() =>
        new ResetPlaybackGate().Resolve("Claude|first", null, Detected).Should().BeNull();

    [TestMethod]
    public void Resolve_WhenResetFirstSeenOnScreen_PassesTheDetectionTimeThrough() =>
        new ResetPlaybackGate().Resolve("Claude|first", Detected, Detected).Should().Be(Detected);

    [TestMethod]
    public void Resolve_WhenResetFirstSeenOffScreen_HoldsItBack()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);

        gate.Resolve("Claude|first", Detected, Detected).Should().BeNull();
    }

    [TestMethod]
    public void SetOnScreen_WhenBackWithAHeldReset_ReleasesItOnceTheDelayHasPassed()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected, Detected);

        gate.SetOnScreen(true, Back).Should().BeTrue();

        gate.Resolve("Claude|first", Detected, Back).Should().BeNull();
        gate.Resolve("Claude|first", Detected, Back.AddMilliseconds(999)).Should().BeNull();
        gate.Resolve("Claude|first", Detected, Released).Should().Be(Released);
        gate.Resolve("Claude|first", Detected, Released.AddSeconds(8)).Should().Be(Released);
    }

    [TestMethod]
    public void Resolve_WhenAResetArrivesWhileTheWindowIsAlreadyOnScreen_IsNeverDelayed()
    {
        var gate = new ResetPlaybackGate();
        var live = Back.AddHours(1);

        // The detection time is returned at the very moment of detection, with no release delay
        gate.Resolve("Claude|first", live, live).Should().Be(live);
        gate.SetOnScreen(true, live).Should().BeFalse();
    }

    [TestMethod]
    public void Resolve_WhenALiveResetArrivesInsideTheDelayOfAnotherBarsRelease_IsNotDelayed()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected, Detected);
        gate.SetOnScreen(true, Back);
        var live = Back.AddMilliseconds(200);

        gate.Resolve("Claude|first", Detected, live).Should().BeNull();
        gate.Resolve("Codex|first", live, live).Should().Be(live);
        gate.Resolve("Claude|second", live, live).Should().Be(live);
    }

    [TestMethod]
    public void Resolve_WhenTheWindowWasAwayAndAResetIsFirstSeenAfterItCameBack_IsNotDelayed()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.SetOnScreen(true, Back).Should().BeFalse();
        var live = Back.AddMinutes(3);

        gate.Resolve("Claude|first", live, live).Should().Be(live);
    }

    [TestMethod]
    public void ReleaseDelay_WhenNotGiven_IsOneSecond() =>
        new ResetPlaybackGate().ReleaseDelay.Should().Be(TimeSpan.FromSeconds(1));

    [TestMethod]
    public void SetOnScreen_WhenAnotherDelayIsGiven_UsesIt()
    {
        var gate = new ResetPlaybackGate(TimeSpan.FromSeconds(3));
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected, Detected);
        gate.SetOnScreen(true, Back);

        gate.Resolve("Claude|first", Detected, Back.AddSeconds(2)).Should().BeNull();
        gate.Resolve("Claude|first", Detected, Back.AddSeconds(3)).Should().Be(Back.AddSeconds(3));
    }

    [TestMethod]
    public void SetOnScreen_WhenBackWithNothingHeld_ReleasesNothing()
    {
        var gate = new ResetPlaybackGate();
        gate.Resolve("Claude|first", Detected, Detected);
        gate.SetOnScreen(false, Detected.AddMinutes(1));

        gate.SetOnScreen(true, Back).Should().BeFalse();
        gate.Resolve("Claude|first", Detected, Back).Should().Be(Detected);
    }

    [TestMethod]
    public void SetOnScreen_WhenTheStateDoesNotChange_ReleasesNothing()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected, Detected);

        gate.SetOnScreen(false, Back).Should().BeFalse();
        gate.Resolve("Claude|first", Detected, Back.AddMinutes(1)).Should().BeNull();
    }

    [TestMethod]
    public void Resolve_WhenTheResetWasSeenOnScreenBeforeTheWindowWentAway_DoesNotReplayItOnReturn()
    {
        var gate = new ResetPlaybackGate();
        gate.Resolve("Claude|first", Detected, Detected).Should().Be(Detected);
        gate.SetOnScreen(false, Detected.AddSeconds(5));
        gate.Resolve("Claude|first", Detected, Detected.AddSeconds(6)).Should().Be(Detected);

        gate.SetOnScreen(true, Back);

        gate.Resolve("Claude|first", Detected, Released).Should().Be(Detected);
    }

    [TestMethod]
    public void SetOnScreen_WhenSeveralBarsHeldResets_ReleasesEveryOneAtTheSameTime()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected, Detected);
        gate.Resolve("Claude|second", Detected, Detected);
        gate.Resolve("Codex|first", Detected.AddMinutes(1), Detected);

        gate.SetOnScreen(true, Back).Should().BeTrue();

        gate.Resolve("Claude|first", Detected, Released).Should().Be(Released);
        gate.Resolve("Claude|second", Detected, Released).Should().Be(Released);
        gate.Resolve("Codex|first", Detected.AddMinutes(1), Released).Should().Be(Released);
    }

    [TestMethod]
    public void Resolve_WhenANewerResetFollowsAReleasedOne_HandlesItOnItsOwn()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected, Detected);
        gate.SetOnScreen(true, Back);
        var next = Back.AddHours(5);

        gate.Resolve("Claude|first", next, next).Should().Be(next);
        gate.SetOnScreen(false, next.AddSeconds(30));
        var later = next.AddMinutes(10);
        gate.Resolve("Claude|first", later, later).Should().BeNull();
    }

    [TestMethod]
    public void SetOnScreen_WhenTheWindowLeavesAgainInsideTheDelay_StillReleasesTheResetAtItsOriginalTime()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected, Detected);
        gate.SetOnScreen(true, Back);

        gate.SetOnScreen(false, Back.AddMilliseconds(400)).Should().BeFalse();
        gate.SetOnScreen(true, Back.AddSeconds(5)).Should().BeFalse();

        gate.Resolve("Claude|first", Detected, Back.AddSeconds(5)).Should().Be(Released);
    }

    [TestMethod]
    public void Resolve_WhenMoreThanTheCapIsTracked_ForgetsTheOldest()
    {
        var gate = new ResetPlaybackGate();
        for (var i = 0; i < 70; i++) gate.Resolve("bar" + i, Detected, Detected);
        gate.SetOnScreen(false, Back);

        // bar0 was evicted, so it is new to the gate while off screen; bar69 is still remembered as seen on screen
        gate.Resolve("bar0", Detected, Back).Should().BeNull();
        gate.Resolve("bar69", Detected, Back).Should().Be(Detected);
    }

    [TestMethod]
    public void OnScreen_StartsTrue() => new ResetPlaybackGate().OnScreen.Should().BeTrue();
}
