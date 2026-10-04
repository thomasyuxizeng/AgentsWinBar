using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class UsageChangeTrackerTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static UsageWindow Win(double used, DateTime? changedAt = null) =>
        new() { Used = (int)used, Limit = 100, WindowMinutes = 300, LastChangedAt = changedAt };

    private static UsageData Data(UsageWindow? session, UsageWindow? weekly = null, params ExtraUsageWindow[] extras) =>
        new() { Provider = "claude", Session = session, Weekly = weekly, ExtraWindows = extras };

    [TestMethod]
    public void Annotate_WhenNothingToCompareWith_ReturnsTheReadingUnchanged()
    {
        var incoming = Data(Win(10));

        UsageChangeTracker.Annotate(incoming, null, Now).Should().BeSameAs(incoming);
    }

    [TestMethod]
    public void Annotate_WhenUsageMoved_StampsTheWindowAsChangedNow()
    {
        var result = UsageChangeTracker.Annotate(Data(Win(12)), Data(Win(10)), Now);

        result.Session!.LastChangedAt.Should().Be(Now);
    }

    [TestMethod]
    public void Annotate_WhenUsageDidNotMove_CarriesTheEarlierChangeTime()
    {
        var earlier = Now.AddMinutes(-20);

        var result = UsageChangeTracker.Annotate(Data(Win(10)), Data(Win(10, earlier)), Now);

        result.Session!.LastChangedAt.Should().Be(earlier);
    }

    [TestMethod]
    public void Annotate_WhenTheWindowHasNeverChanged_StaysNull()
    {
        UsageChangeTracker.Annotate(Data(Win(10)), Data(Win(10)), Now).Session!.LastChangedAt.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_IgnoresSubHalfPointNoise()
    {
        var incoming = Data(new UsageWindow { Used = 10, Limit = 100 });   // 10%
        var previous = Data(new UsageWindow { Used = 10, Limit = 100 });

        UsageChangeTracker.Annotate(incoming, previous, Now).Session!.LastChangedAt.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_TracksEachWindowIndependently()
    {
        var result = UsageChangeTracker.Annotate(Data(Win(10), Win(21)), Data(Win(10), Win(20)), Now);

        result.Session!.LastChangedAt.Should().BeNull();
        result.Weekly!.LastChangedAt.Should().Be(Now);
    }

    [TestMethod]
    public void Annotate_MatchesExtraWindowsByTitle()
    {
        var previous = Data(Win(10), null,
            new ExtraUsageWindow { Title = "Fable only", Window = Win(5) },
            new ExtraUsageWindow { Title = "Other", Window = Win(50) });
        var incoming = Data(Win(10), null,
            new ExtraUsageWindow { Title = "Other", Window = Win(50) },          // order differs
            new ExtraUsageWindow { Title = "Fable only", Window = Win(7) });     // moved

        var result = UsageChangeTracker.Annotate(incoming, previous, Now);

        result.ExtraWindows.Single(x => x.Title == "Fable only").Window.LastChangedAt.Should().Be(Now);
        result.ExtraWindows.Single(x => x.Title == "Other").Window.LastChangedAt.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_WhenAWindowAppearsForTheFirstTime_LeavesItUnstamped()
    {
        var result = UsageChangeTracker.Annotate(
            Data(Win(10), null, new ExtraUsageWindow { Title = "New", Window = Win(1) }),
            Data(Win(10)),
            Now);

        result.ExtraWindows.Single().Window.LastChangedAt.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_WhenUsageResetsToZero_CountsAsAChange()
    {
        UsageChangeTracker.Annotate(Data(Win(0)), Data(Win(80)), Now).Session!.LastChangedAt.Should().Be(Now);
    }
}
