using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class DashboardClockUpdaterTests
{
    private static readonly DateTime BaseUtc = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static DashboardSnapshot SampleSnapshot()
    {
        var window = new DashboardWindow
        {
            Key = "session",
            Title = "Session",
            UsedPercent = 40,
            WindowMinutes = 300,
            ResetsAtUtc = BaseUtc.AddHours(3),
            ElapsedPercent = 40,
            ResetText = "Resets in 3h 0m",
            Pace = PaceCalculator.Project(40, 300, BaseUtc.AddHours(3), BaseUtc),
            PaceSummary = PaceCalculator.Summarize(PaceCalculator.Project(40, 300, BaseUtc.AddHours(3), BaseUtc)),
            History = new BurndownSeries { Usage = [new(0.1, 10), new(0.4, 40)] }
        };
        var provider = new DashboardProvider
        {
            Id = "claude",
            DisplayName = "Claude",
            FetchedAtUtc = BaseUtc,
            Windows = [window],
            Cost = new DashboardCost { TodayCost = 5m, LastSuccessfulFetchUtc = BaseUtc }
        };
        return new DashboardSnapshot
        {
            GeneratedAtUtc = BaseUtc,
            Providers = [provider],
            Diagnostics = new DashboardDiagnostics { AppVersion = "1.6.2" }
        };
    }

    [TestMethod]
    public void TryRefreshClock_NormalTick_UpdatesWindowTimesAndPreservesHeavyObjects()
    {
        var snap = SampleSnapshot();
        var nextUtc = BaseUtc.AddMinutes(2);

        var ok = DashboardClockUpdater.TryRefreshClock(snap, nextUtc, TimeZoneInfo.Utc, out var updated);

        ok.Should().BeTrue();
        updated.GeneratedAtUtc.Should().Be(nextUtc);
        updated.Diagnostics.Should().BeSameAs(snap.Diagnostics);
        updated.Providers[0].Cost.Should().BeSameAs(snap.Providers[0].Cost);
        updated.Providers[0].Windows[0].History.Should().BeSameAs(snap.Providers[0].Windows[0].History);
        updated.Providers[0].Windows[0].ResetText.Should().Be("Resets in 2h 58m");
        updated.Providers[0].Windows[0].ElapsedPercent.Should().BeGreaterThan(snap.Providers[0].Windows[0].ElapsedPercent!.Value);
    }

    [TestMethod]
    public void TryRefreshClock_LocalMidnightRollover_ReturnsFalse()
    {
        var beforeMidnight = new DateTime(2026, 10, 5, 23, 59, 45, DateTimeKind.Utc);
        var afterMidnight = new DateTime(2026, 10, 6, 0, 0, 15, DateTimeKind.Utc);
        var snap = SampleSnapshot() with { GeneratedAtUtc = beforeMidnight };

        DashboardClockUpdater.TryRefreshClock(snap, afterMidnight, TimeZoneInfo.Utc, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryRefreshClock_WindowCrossesResetTime_ReturnsFalse()
    {
        var snap = SampleSnapshot();
        var atReset = snap.Providers[0].Windows[0].ResetsAtUtc!.Value;

        DashboardClockUpdater.TryRefreshClock(snap, atReset, TimeZoneInfo.Utc, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryRefreshClock_ApiCreditsCrossesStaleThresholdOrUtcMidnight_ReturnsFalse()
    {
        var fetched = BaseUtc.AddMinutes(-14).AddSeconds(-45);
        var snap = SampleSnapshot();
        snap = snap with
        {
            Providers =
            [
                snap.Providers[0] with
                {
                    ApiCredits = ApiCreditsCalculator.BuildOk(200m, 50m, 1, BaseUtc, fetched)
                }
            ]
        };

        // Still under 15m -> succeeds
        DashboardClockUpdater.TryRefreshClock(snap, BaseUtc.AddSeconds(10), TimeZoneInfo.Utc, out _).Should().BeTrue();
        // Past 15m -> falls back
        DashboardClockUpdater.TryRefreshClock(snap, BaseUtc.AddSeconds(20), TimeZoneInfo.Utc, out _).Should().BeFalse();
        // UTC midnight rollover with non-UTC local zone -> falls back
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
        var utc2359 = new DateTime(2026, 10, 5, 23, 59, 45, DateTimeKind.Utc); // Oct 6 08:59 JST
        var utc0000 = new DateTime(2026, 10, 6, 0, 0, 15, DateTimeKind.Utc);  // Oct 6 09:00 JST (same local day)
        var utcSnap = snap with { GeneratedAtUtc = utc2359 };
        DashboardClockUpdater.TryRefreshClock(utcSnap, utc0000, tokyo, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryRefreshClock_EarlyResetExpires_ReturnsFalse()
    {
        var snap = SampleSnapshot();
        snap = snap with
        {
            Providers =
            [
                snap.Providers[0] with
                {
                    EarlyResetAtUtc = BaseUtc.AddHours(-23).AddMinutes(-59).AddSeconds(-45)
                }
            ]
        };

        DashboardClockUpdater.TryRefreshClock(snap, BaseUtc.AddSeconds(30), TimeZoneInfo.Utc, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryRefreshClock_CostOlderThanSevenDays_ReturnsTrue()
    {
        // Cost data has no maximum age, so an old (stale) reading must not force a full snapshot on every tick
        var snap = SampleSnapshot();
        var oldCost = snap.Providers[0].Cost! with { IsStale = true, LastSuccessfulFetchUtc = BaseUtc.AddDays(-8) };
        snap = snap with { Providers = [snap.Providers[0] with { Cost = oldCost }] };

        var ok = DashboardClockUpdater.TryRefreshClock(snap, BaseUtc.AddSeconds(30), TimeZoneInfo.Utc, out var updated);

        ok.Should().BeTrue();
        updated.Providers[0].Cost.Should().BeSameAs(oldCost);
    }

    [TestMethod]
    public void TryRefreshClock_CostBeyondFutureSkewTolerance_ReturnsFalse()
    {
        var snap = SampleSnapshot();
        var futureCost = snap.Providers[0].Cost! with { LastSuccessfulFetchUtc = BaseUtc.AddMinutes(10) };
        snap = snap with { Providers = [snap.Providers[0] with { Cost = futureCost }] };

        DashboardClockUpdater.TryRefreshClock(snap, BaseUtc.AddSeconds(30), TimeZoneInfo.Utc, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryRefreshClock_StatusRetentionExpires_ReturnsFalse()
    {
        var snap = SampleSnapshot();
        snap = snap with
        {
            Providers =
            [
                snap.Providers[0] with
                {
                    ServiceStatus = new ProviderStatus { Indicator = ProviderStatusIndicator.Minor },
                    LastSuccessfulStatusFetchUtc = BaseUtc.AddDays(-7).AddSeconds(10)
                }
            ]
        };

        DashboardClockUpdater.TryRefreshClock(snap, BaseUtc.AddSeconds(30), TimeZoneInfo.Utc, out _).Should().BeFalse();
    }
}
