using AwesomeAssertions;
using AgentsWinBar.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class BatterySaverMonitorTests
{
    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    public void Constructor_PowerStatus_OnlyOnIsActive(int status, bool active)
    {
        var source = new Mock<IBatterySaverStatusSource>();
        source.SetupGet(s => s.EnergySaverStatus).Returns(status);
        using var monitor = new BatterySaverMonitor(source.Object, NullLogger<BatterySaverMonitor>.Instance);
        monitor.BatterySaverActive.Should().Be(active);
    }

    [TestMethod]
    public void StatusChanged_ChangesAndDuplicateEvents_NotifiesOnlyOnChangeAndDetaches()
    {
        var source = new Mock<IBatterySaverStatusSource>();
        var status = 0;
        source.SetupGet(s => s.EnergySaverStatus).Returns(() => status);
        var monitor = new BatterySaverMonitor(source.Object, NullLogger<BatterySaverMonitor>.Instance);
        var count = 0;
        monitor.BatterySaverChanged += (_, _) => count++;
        status = 2;
        source.Raise(s => s.EnergySaverStatusChanged += null, source.Object, new object());
        source.Raise(s => s.EnergySaverStatusChanged += null, source.Object, new object());
        monitor.BatterySaverActive.Should().BeTrue();
        count.Should().Be(1);
        status = 1;
        source.Raise(s => s.EnergySaverStatusChanged += null, source.Object, new object());
        monitor.BatterySaverActive.Should().BeFalse();
        count.Should().Be(2);
        monitor.Dispose();
        monitor.Dispose();
        status = 2;
        source.Raise(s => s.EnergySaverStatusChanged += null, source.Object, new object());
        count.Should().Be(2);
        source.VerifyRemove(s => s.EnergySaverStatusChanged -= It.IsAny<EventHandler<object>>(), Times.Once);
    }

    [TestMethod]
    public void Constructor_ApiReadAndSubscriptionThrow_FallsBackSafely()
    {
        var source = new Mock<IBatterySaverStatusSource>();
        source.SetupGet(s => s.EnergySaverStatus).Throws(new InvalidOperationException());
        source.SetupAdd(s => s.EnergySaverStatusChanged += It.IsAny<EventHandler<object>>()).Throws(new InvalidOperationException());
        source.SetupRemove(s => s.EnergySaverStatusChanged -= It.IsAny<EventHandler<object>>()).Throws(new InvalidOperationException());
        using var monitor = new BatterySaverMonitor(source.Object, NullLogger<BatterySaverMonitor>.Instance);
        monitor.BatterySaverActive.Should().BeFalse();
    }

    [TestMethod]
    public void StatusChanged_ApiThrowsAfterActive_FallsBackAndNotifies()
    {
        var source = new Mock<IBatterySaverStatusSource>();
        source.SetupGet(s => s.EnergySaverStatus).Returns(2);
        using var monitor = new BatterySaverMonitor(source.Object, NullLogger<BatterySaverMonitor>.Instance);
        var count = 0;
        monitor.BatterySaverChanged += (_, _) => count++;
        source.SetupGet(s => s.EnergySaverStatus).Throws(new InvalidOperationException());
        source.Raise(s => s.EnergySaverStatusChanged += null, source.Object, new object());
        monitor.BatterySaverActive.Should().BeFalse();
        count.Should().Be(1);
    }
}
