using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class ResetPlaybackGateTests
{
    private static readonly DateTime Detected = new(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Back = Detected.AddHours(6);

    [TestMethod]
    public void Resolve_WhenNoReset_ReturnsNull() =>
        new ResetPlaybackGate().Resolve("Claude|first", null).Should().BeNull();

    [TestMethod]
    public void Resolve_WhenResetFirstSeenOnScreen_PassesTheDetectionTimeThrough() =>
        new ResetPlaybackGate().Resolve("Claude|first", Detected).Should().Be(Detected);

    [TestMethod]
    public void Resolve_WhenResetFirstSeenOffScreen_HoldsItBack()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);

        gate.Resolve("Claude|first", Detected).Should().BeNull();
    }

    [TestMethod]
    public void SetOnScreen_WhenBackWithAHeldReset_ReleasesItOnceWithTheTimeOfTheReturn()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected);

        gate.SetOnScreen(true, Back).Should().BeTrue();

        gate.Resolve("Claude|first", Detected).Should().Be(Back);
        gate.Resolve("Claude|first", Detected).Should().Be(Back);
    }

    [TestMethod]
    public void SetOnScreen_WhenBackWithNothingHeld_ReleasesNothing()
    {
        var gate = new ResetPlaybackGate();
        gate.Resolve("Claude|first", Detected);
        gate.SetOnScreen(false, Detected.AddMinutes(1));

        gate.SetOnScreen(true, Back).Should().BeFalse();
        gate.Resolve("Claude|first", Detected).Should().Be(Detected);
    }

    [TestMethod]
    public void SetOnScreen_WhenTheStateDoesNotChange_ReleasesNothing()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected);

        gate.SetOnScreen(false, Back).Should().BeFalse();
        gate.Resolve("Claude|first", Detected).Should().BeNull();
    }

    [TestMethod]
    public void Resolve_WhenTheResetWasSeenOnScreenBeforeTheWindowWentAway_DoesNotReplayItOnReturn()
    {
        var gate = new ResetPlaybackGate();
        gate.Resolve("Claude|first", Detected).Should().Be(Detected);
        gate.SetOnScreen(false, Detected.AddSeconds(5));
        gate.Resolve("Claude|first", Detected).Should().Be(Detected);

        gate.SetOnScreen(true, Back);

        gate.Resolve("Claude|first", Detected).Should().Be(Detected);
    }

    [TestMethod]
    public void SetOnScreen_WhenSeveralBarsHeldResets_ReleasesEveryOneWithTheSameTime()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected);
        gate.Resolve("Claude|second", Detected);
        gate.Resolve("Codex|first", Detected.AddMinutes(1));

        gate.SetOnScreen(true, Back).Should().BeTrue();

        gate.Resolve("Claude|first", Detected).Should().Be(Back);
        gate.Resolve("Claude|second", Detected).Should().Be(Back);
        gate.Resolve("Codex|first", Detected.AddMinutes(1)).Should().Be(Back);
    }

    [TestMethod]
    public void Resolve_WhenANewerResetFollowsAReleasedOne_HandlesItOnItsOwn()
    {
        var gate = new ResetPlaybackGate();
        gate.SetOnScreen(false, Detected);
        gate.Resolve("Claude|first", Detected);
        gate.SetOnScreen(true, Back);
        var next = Back.AddHours(5);

        gate.Resolve("Claude|first", next).Should().Be(next);
        gate.SetOnScreen(false, next.AddSeconds(30));
        var later = next.AddMinutes(10);
        gate.Resolve("Claude|first", later).Should().BeNull();
    }

    [TestMethod]
    public void Resolve_WhenMoreThanTheCapIsTracked_ForgetsTheOldest()
    {
        var gate = new ResetPlaybackGate();
        for (var i = 0; i < 70; i++) gate.Resolve("bar" + i, Detected);
        gate.SetOnScreen(false, Back);

        // bar0 was evicted, so it is new to the gate while off screen; bar69 is still remembered as seen on screen
        gate.Resolve("bar0", Detected).Should().BeNull();
        gate.Resolve("bar69", Detected).Should().Be(Detected);
    }

    [TestMethod]
    public void OnScreen_StartsTrue() => new ResetPlaybackGate().OnScreen.Should().BeTrue();
}
