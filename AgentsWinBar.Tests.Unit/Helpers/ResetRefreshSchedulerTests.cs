using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ResetRefreshSchedulerTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Normal = TimeSpan.FromMinutes(5);

    [TestMethod]
    public void GetSchedule_MultipleWindows_TargetsNearestFutureResetPlusTwoSeconds()
    {
        var reset = Now.AddSeconds(20);
        var result = ResetRefreshScheduler.GetSchedule([null, Now.AddSeconds(-1), Now, Now.AddMinutes(2), reset, reset], Now, Normal);
        result.Should().Be(new ResetRefreshSchedule(TimeSpan.FromSeconds(22), reset));
    }

    [TestMethod]
    public void GetSchedule_NoFutureWindows_ReturnsNormalDelay()
    {
        ResetRefreshScheduler.GetSchedule([null, Now.AddSeconds(-1), Now], Now, Normal)
            .Should().Be(new ResetRefreshSchedule(Normal));
    }

    [TestMethod]
    [DataRow(298)]
    [DataRow(299)]
    [DataRow(600)]
    public void GetSchedule_ResetAtOrAfterNormalRefresh_IgnoresReset(int seconds)
    {
        ResetRefreshScheduler.GetSchedule([Now.AddSeconds(seconds)], Now, Normal)
            .Should().Be(new ResetRefreshSchedule(Normal));
    }

    [TestMethod]
    public void GetSchedule_NormalDelayBelowMinimum_ClampsToTwoSeconds()
    {
        ResetRefreshScheduler.GetSchedule([Now.AddMilliseconds(10)], Now, TimeSpan.Zero)
            .Should().Be(new ResetRefreshSchedule(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void GetSchedule_FirstAttemptStillReportsOldReset_SchedulesOneRetryFifteenSecondsAfterStart()
    {
        var reset = Now.AddSeconds(-5);
        var attempts = new Dictionary<DateTime, ResetRefreshAttempt> { [reset] = new(Now) };
        ResetRefreshScheduler.GetSchedule([reset], Now.AddSeconds(8), Normal, attempts)
            .Should().Be(new ResetRefreshSchedule(TimeSpan.FromSeconds(7), reset, true));
    }

    [TestMethod]
    [DataRow(14)]
    [DataRow(16)]
    [DataRow(40)]
    public void GetSchedule_OverdueRetry_ClampsToTwoSeconds(int elapsedSeconds)
    {
        var reset = Now.AddMinutes(-1);
        var attempts = new Dictionary<DateTime, ResetRefreshAttempt> { [reset] = new(Now.AddSeconds(-elapsedSeconds)) };
        ResetRefreshScheduler.GetSchedule([reset], Now, Normal, attempts)
            .Should().Be(new ResetRefreshSchedule(TimeSpan.FromSeconds(2), reset, true));
    }

    [TestMethod]
    public void GetSchedule_RetryAlreadyTriggered_FallsBackToNormalEvenWithRepeatedOldReset()
    {
        var reset = Now.AddMinutes(-1);
        var attempts = new Dictionary<DateTime, ResetRefreshAttempt> { [reset] = new(Now.AddSeconds(-40), true) };
        for (var i = 0; i < 100; i++)
            ResetRefreshScheduler.GetSchedule([reset], Now.AddSeconds(i), Normal, attempts)
                .Should().Be(new ResetRefreshSchedule(Normal));
    }

    [TestMethod]
    public void GetSchedule_ProviderRolledForward_DoesNotRetryOldReset()
    {
        var oldReset = Now.AddSeconds(-5);
        var attempts = new Dictionary<DateTime, ResetRefreshAttempt> { [oldReset] = new(Now) };
        var next = Now.AddSeconds(50);
        ResetRefreshScheduler.GetSchedule([next], Now, Normal, attempts)
            .Should().Be(new ResetRefreshSchedule(TimeSpan.FromSeconds(52), next));
    }

    [TestMethod]
    public void GetSchedule_NewResetBeforePendingRetry_TargetsNewReset()
    {
        var oldReset = Now.AddSeconds(-5);
        var next = Now.AddSeconds(8);
        var attempts = new Dictionary<DateTime, ResetRefreshAttempt> { [oldReset] = new(Now) };
        ResetRefreshScheduler.GetSchedule([oldReset, next], Now, Normal, attempts)
            .Should().Be(new ResetRefreshSchedule(TimeSpan.FromSeconds(10), next));
    }

    [TestMethod]
    public void GetSchedule_NormalRefreshSoonerThanRetry_UsesNormalInterval()
    {
        var reset = Now.AddSeconds(-5);
        var attempts = new Dictionary<DateTime, ResetRefreshAttempt> { [reset] = new(Now) };
        ResetRefreshScheduler.GetSchedule([reset], Now, TimeSpan.FromSeconds(10), attempts)
            .Should().Be(new ResetRefreshSchedule(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public void GetSchedule_LocalResetTime_NormalizesTargetToUtc()
    {
        var reset = Now.AddSeconds(20);
        ResetRefreshScheduler.GetSchedule([reset.ToLocalTime()], Now, Normal).TargetResetAtUtc.Should().Be(reset);
    }

    [TestMethod]
    public void PruneExpiredAttempts_MixedEntries_RemovesOnlyFullyExpiredEntries()
    {
        var fullyExpired = Now.AddMinutes(-61);
        var oldResetRecentTrigger = Now.AddMinutes(-90);
        var recentReset = Now.AddMinutes(-30);
        var attempts = new Dictionary<DateTime, ResetRefreshAttempt>
        {
            [fullyExpired] = new(Now.AddMinutes(-61)),
            [oldResetRecentTrigger] = new(Now.AddMinutes(-30)),
            [recentReset] = new(Now.AddMinutes(-61))
        };

        var removed = ResetRefreshScheduler.PruneExpiredAttempts(attempts, Now);

        removed.Should().Be(1);
        attempts.Should().ContainKeys(oldResetRecentTrigger, recentReset);
        attempts.Should().NotContainKey(fullyExpired);
    }

    [TestMethod]
    public void PruneExpiredAttempts_EmptyDictionary_ReturnsZero()
    {
        var attempts = new Dictionary<DateTime, ResetRefreshAttempt>();
        ResetRefreshScheduler.PruneExpiredAttempts(attempts, Now).Should().Be(0);
    }
}
