using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class ActivityLogTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void RecordRefresh_OutOfOrderEntries_KeepsFortyNewestAndDetachedSnapshots()
    {
        var log = new ActivityLog();
        for (var i = 59; i >= 0; i--) log.RecordRefresh(new() { ProviderId = "claude", TimestampUtc = Now.AddSeconds(i) });
        var snapshot = log.RecentRefreshes;
        snapshot.Should().HaveCount(40); snapshot.Should().BeInDescendingOrder(e => e.TimestampUtc);
        snapshot[0].TimestampUtc.Should().Be(Now.AddSeconds(59)); snapshot[^1].TimestampUtc.Should().Be(Now.AddSeconds(20));
        log.RecordRefresh(new() { TimestampUtc = Now.AddDays(1) });
        snapshot[0].TimestampUtc.Should().Be(Now.AddSeconds(59));
        ((DashboardRefreshEntry[])snapshot)[0] = new();
        log.RecentRefreshes[1].TimestampUtc.Should().Be(Now.AddSeconds(59));
    }

    [TestMethod]
    public void RecordAlert_OutOfOrderEntries_KeepsTwentyNewestAndDetachedSnapshots()
    {
        var log = new ActivityLog();
        for (var i = 49; i >= 0; i--) log.RecordAlert(new() { TimestampUtc = Now.AddSeconds(i), Title = "alert" });
        var snapshot = log.RecentAlerts;
        snapshot.Should().HaveCount(20); snapshot.Should().BeInDescendingOrder(e => e.TimestampUtc);
        snapshot[^1].TimestampUtc.Should().Be(Now.AddSeconds(30));
        log.RecordAlert(new() { TimestampUtc = Now.AddDays(1) });
        snapshot[0].TimestampUtc.Should().Be(Now.AddSeconds(49));
    }

    [TestMethod]
    public async Task RecordRefresh_ConcurrentWritesAndReads_PreservesCapsAndNewestEntries()
    {
        var log = new ActivityLog();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var timestamp = Now.AddSeconds(worker * 100 + i);
                log.RecordRefresh(new() { TimestampUtc = timestamp }); log.RecordAlert(new() { TimestampUtc = timestamp });
                log.RecentRefreshes.Should().HaveCountLessThanOrEqualTo(40);
                log.RecentAlerts.Should().HaveCountLessThanOrEqualTo(20);
            }
        })));
        log.RecentRefreshes.Select(e => e.TimestampUtc).Should().Equal(Enumerable.Range(760, 40).Reverse().Select(i => Now.AddSeconds(i)));
        log.RecentAlerts.Select(e => e.TimestampUtc).Should().Equal(Enumerable.Range(780, 20).Reverse().Select(i => Now.AddSeconds(i)));
    }

    [TestMethod]
    public void RecordAlert_NullEntries_IgnoresWithoutThrowing()
    {
        var log = new ActivityLog(); log.RecordAlert(null!); log.RecordRefresh(null!);
        log.RecentAlerts.Should().BeEmpty(); log.RecentRefreshes.Should().BeEmpty();
    }

    [TestMethod]
    public void RecordRefresh_MonotonicAndEqualTimestamps_KeepsNewestFirstOrder()
    {
        var log = new ActivityLog();
        log.RecordRefresh(new() { ProviderId = "claude", TimestampUtc = Now });
        log.RecordRefresh(new() { ProviderId = "codex", TimestampUtc = Now });
        log.RecordRefresh(new() { ProviderId = "gemini", TimestampUtc = Now.AddSeconds(1) });
        log.RecordRefresh(new() { ProviderId = "antigravity", TimestampUtc = Now.AddSeconds(-1) });

        log.RecentRefreshes.Select(e => e.ProviderId)
            .Should().Equal("gemini", "codex", "claude", "antigravity");
    }
}
