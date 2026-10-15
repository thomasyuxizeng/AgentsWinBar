using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class ResetHostStateTests
{
    private static readonly DateTime Stamp = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void TryMarkPlayed_WhenFirstTimeForKeyAndStamp_ReturnsTrue() =>
        new ResetHostState().TryMarkPlayed("Claude|first", Stamp).Should().BeTrue();

    [TestMethod]
    public void TryMarkPlayed_WhenRepeatedForTheSameBar_ReturnsFalse()
    {
        var state = new ResetHostState();
        state.TryMarkPlayed("Claude|first", Stamp);
        state.TryMarkPlayed("Claude|first", Stamp).Should().BeFalse();
    }

    [TestMethod]
    public void TryMarkPlayed_WhenAnotherBarOrAnotherReset_ReturnsTrue()
    {
        var state = new ResetHostState();
        state.TryMarkPlayed("Claude|first", Stamp);
        state.TryMarkPlayed("Claude|second", Stamp).Should().BeTrue();
        state.TryMarkPlayed("Codex|first", Stamp).Should().BeTrue();
        state.TryMarkPlayed("Claude|first", Stamp.AddHours(5)).Should().BeTrue();
    }

    [TestMethod]
    public void TryMarkPlayed_WhenMoreThanTheCapIsRecorded_ForgetsTheOldest()
    {
        var state = new ResetHostState();
        for (var i = 0; i < 70; i++) state.TryMarkPlayed("bar" + i, Stamp);

        state.TryMarkPlayed("bar0", Stamp).Should().BeTrue();
        state.TryMarkPlayed("bar69", Stamp).Should().BeFalse();
    }

    [TestMethod]
    public void RemainingBusy_WhenNothingStarted_IsZero() =>
        new ResetHostState().RemainingBusy(1_000).Should().Be(TimeSpan.Zero);

    [TestMethod]
    public void RemainingBusy_WhileAnAnimationRuns_CountsDown()
    {
        var state = new ResetHostState();
        state.MarkBusy(1_000, 3_700);

        state.RemainingBusy(1_000).Should().Be(TimeSpan.FromMilliseconds(3_700));
        state.RemainingBusy(3_000).Should().Be(TimeSpan.FromMilliseconds(1_700));
        state.RemainingBusy(4_700).Should().Be(TimeSpan.Zero);
    }

    [TestMethod]
    public void MarkBusy_WhenASecondBarStartsLater_ExtendsTheBusyTimeButNeverShortensIt()
    {
        var state = new ResetHostState();
        state.MarkBusy(1_000, 3_700);
        state.MarkBusy(1_500, 3_700);
        state.RemainingBusy(1_500).Should().Be(TimeSpan.FromMilliseconds(3_700));

        state.MarkBusy(1_600, 100);
        state.RemainingBusy(1_600).Should().Be(TimeSpan.FromMilliseconds(3_600));
    }
}
