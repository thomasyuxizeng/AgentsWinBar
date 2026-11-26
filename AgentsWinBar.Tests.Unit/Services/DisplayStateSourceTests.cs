using AwesomeAssertions;
using AgentsWinBar.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class DisplayStateSourceTests
{
    private static DisplayStateSource Create() => new(new Mock<ILogger<DisplayStateSource>>().Object, registerNotifications: false);

    [TestMethod]
    [DataRow(0u, false)]
    [DataRow(1u, true)]
    [DataRow(2u, true)]
    [DataRow(7u, true)]
    public void ParseIsOn_WhenGivenAConsoleDisplayState_OnlyOffCountsAsOff(uint state, bool expected) =>
        DisplayStateSource.ParseIsOn(state).Should().Be(expected);

    [TestMethod]
    public void IsOn_WhenNothingHasBeenReported_IsTrue()
    {
        using var source = Create();
        source.IsOn.Should().BeTrue();
    }

    [TestMethod]
    public void Apply_WhenTheDisplayGoesOffAndBackOn_RaisesChangedEachTime()
    {
        using var source = Create();
        var changes = 0;
        source.Changed += (_, _) => changes++;

        source.Apply(0);
        source.IsOn.Should().BeFalse();
        source.Apply(1);
        source.IsOn.Should().BeTrue();

        changes.Should().Be(2);
    }

    [TestMethod]
    public void Apply_WhenTheStateDoesNotChange_RaisesNothing()
    {
        using var source = Create();
        var changes = 0;
        source.Changed += (_, _) => changes++;

        source.Apply(1);
        source.Apply(2);
        source.Apply(0);
        source.Apply(0);

        changes.Should().Be(1);
    }
}
