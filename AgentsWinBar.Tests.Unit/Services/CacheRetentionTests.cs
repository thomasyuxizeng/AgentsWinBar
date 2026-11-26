using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class CacheRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(0)]
    [DataRow(60)]
    [DataRow(8 * 24 * 60)]
    [DataRow(365 * 24 * 60)]
    public void IsCostRetained_PastTimestampOfAnyAge_ReturnsTrue(int minutesAgo)
    {
        CacheRetention.IsCostRetained(Now.AddMinutes(-minutesAgo), Now).Should().BeTrue();
    }

    [TestMethod]
    public void IsCostRetained_FutureWithinSkewTolerance_ReturnsTrue()
    {
        CacheRetention.IsCostRetained(Now + CacheRetention.ClockSkewTolerance, Now).Should().BeTrue();
    }

    [TestMethod]
    public void IsCostRetained_FutureBeyondSkewTolerance_ReturnsFalse()
    {
        CacheRetention.IsCostRetained(Now + CacheRetention.ClockSkewTolerance + TimeSpan.FromSeconds(1), Now).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(-8 * 24 * 60, true)]
    [DataRow(4, true)]
    [DataRow(6, false)]
    public void RetainedCost_LastSuccessfulFetchAge_AgreesWithIsCostRetained(int minutesFromNow, bool retained)
    {
        var cost = new CostData { Provider = "claude", FetchedAt = Now, LastSuccessfulFetch = Now.AddMinutes(minutesFromNow) };

        var result = CacheRetention.RetainedCost(cost, Now);

        (result != null).Should().Be(retained);
        CacheRetention.IsCostRetained(cost.LastSuccessfulFetch!.Value, Now).Should().Be(retained);
    }

    [TestMethod]
    public void RetainedCost_NoLastSuccessfulFetch_JudgesFetchedAt()
    {
        var future = new CostData { Provider = "claude", FetchedAt = Now.AddMinutes(10) };
        var old = new CostData { Provider = "claude", FetchedAt = Now.AddDays(-30) };

        CacheRetention.RetainedCost(future, Now).Should().BeNull();
        CacheRetention.RetainedCost(old, Now).Should().BeSameAs(old);
    }
}
