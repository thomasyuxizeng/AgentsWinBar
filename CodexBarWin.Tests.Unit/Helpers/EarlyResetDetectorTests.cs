using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class EarlyResetDetectorTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static EarlyResetWindowReading Reading(
        string provider = "codex",
        string key = "session",
        string title = "Session",
        double usedPercent = 50,
        DateTime? resetAt = null,
        DateTime? readingAt = null,
        bool isCached = false,
        bool isStale = false,
        FetchOutcome outcome = FetchOutcome.Success) =>
        new(
            Provider: provider,
            WindowKey: key,
            WindowTitle: title,
            UsedPercent: usedPercent,
            ResetAt: resetAt,
            ReadingAtUtc: readingAt ?? Now,
            IsCached: isCached,
            IsStale: isStale,
            FetchOutcome: outcome);

    [TestMethod]
    public void Detect_FirstReadingEver_ReturnsNull()
    {
        var current = Reading(usedPercent: 20, resetAt: Now.AddHours(2));
        var result = EarlyResetDetector.Detect(null, current, Now);
        result.Should().BeNull();
    }

    [TestMethod]
    public void Detect_MismatchedProvider_ReturnsNull()
    {
        var prev = Reading(provider: "claude", usedPercent: 80, resetAt: Now.AddHours(2));
        var cur = Reading(provider: "codex", usedPercent: 20, resetAt: Now.AddHours(4));
        EarlyResetDetector.Detect(prev, cur, Now).Should().BeNull();
    }

    [TestMethod]
    public void Detect_MismatchedWindowKey_ReturnsNull()
    {
        var prev = Reading(key: "session", usedPercent: 80, resetAt: Now.AddHours(2));
        var cur = Reading(key: "weekly", usedPercent: 20, resetAt: Now.AddHours(4));
        EarlyResetDetector.Detect(prev, cur, Now).Should().BeNull();
    }

    [TestMethod]
    [DataRow(true, false, FetchOutcome.Success, false, false, FetchOutcome.Success)]
    [DataRow(false, true, FetchOutcome.Success, false, false, FetchOutcome.Success)]
    [DataRow(false, false, FetchOutcome.Failed, false, false, FetchOutcome.Success)]
    [DataRow(false, false, FetchOutcome.Success, true, false, FetchOutcome.Success)]
    [DataRow(false, false, FetchOutcome.Success, false, true, FetchOutcome.Success)]
    [DataRow(false, false, FetchOutcome.Success, false, false, FetchOutcome.Unavailable)]
    public void Detect_CachedStaleOrFailedReadings_ReturnsNull(
        bool prevCached, bool prevStale, FetchOutcome prevOutcome,
        bool curCached, bool curStale, FetchOutcome curOutcome)
    {
        var prev = Reading(usedPercent: 80, resetAt: Now.AddHours(2), isCached: prevCached, isStale: prevStale, outcome: prevOutcome);
        var cur = Reading(usedPercent: 20, resetAt: Now.AddHours(4), isCached: curCached, isStale: curStale, outcome: curOutcome);
        EarlyResetDetector.Detect(prev, cur, Now).Should().BeNull();
    }

    [TestMethod]
    public void Detect_NormalReset_ResetAtPassedOrWithin10Minutes_ReturnsNull()
    {
        // Reset time was 5 minutes in the past
        var prevPast = Reading(usedPercent: 90, resetAt: Now.AddMinutes(-5));
        var cur1 = Reading(usedPercent: 5, resetAt: Now.AddHours(4));
        EarlyResetDetector.Detect(prevPast, cur1, Now).Should().BeNull();

        // Reset time is exactly at current reading time
        var prevNow = Reading(usedPercent: 90, resetAt: Now);
        EarlyResetDetector.Detect(prevNow, cur1, Now).Should().BeNull();

        // Reset time is within 10-minute grace window (e.g. 5m ahead)
        var prevGrace = Reading(usedPercent: 90, resetAt: Now.AddMinutes(5));
        EarlyResetDetector.Detect(prevGrace, cur1, Now).Should().BeNull();
    }

    [TestMethod]
    public void Detect_GracePeriodBoundary_ExactlyAt10MinutesReturnsNull_Over10MinutesDetects()
    {
        // Exactly at 10 minutes (<= curReading + 10m): normal reset, not flagged
        var prev10m = Reading(usedPercent: 80, resetAt: Now.AddMinutes(10));
        var cur = Reading(usedPercent: 20, resetAt: Now.AddHours(4));
        EarlyResetDetector.Detect(prev10m, cur, Now).Should().BeNull();

        // Just over 10 minutes (10m + 1s): old window had NOT ended, flagged!
        var prev10m1s = Reading(usedPercent: 80, resetAt: Now.AddMinutes(10).AddSeconds(1));
        var detected = EarlyResetDetector.Detect(prev10m1s, cur, Now);
        detected.Should().NotBeNull();
        detected!.PreviousPercent.Should().Be(80);
        detected.CurrentPercent.Should().Be(20);
        detected.PreviousResetAtUtc.Should().Be(Now.AddMinutes(10).AddSeconds(1));
        detected.NewResetAtUtc.Should().Be(Now.AddHours(4));
    }

    [TestMethod]
    [DataRow(50.0, 40.1, false)] // 9.9 drop -> null
    [DataRow(50.0, 40.0, true)]  // 10.0 drop -> detected
    [DataRow(80.0, 70.1, false)] // 9.9 drop -> null
    [DataRow(80.0, 70.0, true)]  // 10.0 drop -> detected
    public void Detect_ThresholdBoundary_ExactlyAt9Point9And10Points(double prevPercent, double curPercent, bool shouldDetect)
    {
        var prev = Reading(usedPercent: prevPercent, resetAt: Now.AddHours(2));
        var cur = Reading(usedPercent: curPercent, resetAt: Now.AddHours(5));

        var result = EarlyResetDetector.Detect(prev, cur, Now);
        if (shouldDetect)
        {
            result.Should().NotBeNull();
            result!.PreviousPercent.Should().Be(prevPercent);
            result.CurrentPercent.Should().Be(curPercent);
        }
        else
        {
            result.Should().BeNull();
        }
    }

    [TestMethod]
    public void Detect_EarlyResetAtVariousMargins_ReturnsAccurateRecord()
    {
        // 79% down to 3% with future ResetAt (+2h) shifting to (+6h)
        var prev = Reading(provider: "codex", key: "session", title: "Session", usedPercent: 79, resetAt: Now.AddHours(2));
        var cur = Reading(provider: "codex", key: "session", title: "Session", usedPercent: 3, resetAt: Now.AddHours(6));

        var result = EarlyResetDetector.Detect(prev, cur, Now);
        result.Should().NotBeNull();
        result!.Provider.Should().Be("codex");
        result.WindowKey.Should().Be("session");
        result.WindowTitle.Should().Be("Session");
        result.PreviousPercent.Should().Be(79);
        result.CurrentPercent.Should().Be(3);
        result.PreviousResetAtUtc.Should().Be(Now.AddHours(2));
        result.NewResetAtUtc.Should().Be(Now.AddHours(6));
        result.DetectedAtUtc.Should().Be(Now);
    }

    [TestMethod]
    public void Detect_ResetAtForwardShiftOnly_RequiresNewResetAtLaterUnlessDropIsRolling()
    {
        var prevReset = Now.AddHours(3);

        // Drop is 15 points (50% -> 35%):
        // 1. New ResetAt later (+5h): detected!
        var prev = Reading(usedPercent: 50, resetAt: prevReset);
        var curLater = Reading(usedPercent: 35, resetAt: Now.AddHours(5));
        EarlyResetDetector.Detect(prev, curLater, Now).Should().NotBeNull();

        // 2. New ResetAt same (+3h) and drop 15 (< 25): null
        var curSame = Reading(usedPercent: 35, resetAt: prevReset);
        EarlyResetDetector.Detect(prev, curSame, Now).Should().BeNull();

        // 3. New ResetAt earlier (+2h) and drop 15 (< 25): null
        var curEarlier = Reading(usedPercent: 35, resetAt: Now.AddHours(2));
        EarlyResetDetector.Detect(prev, curEarlier, Now).Should().BeNull();

        // 4. New ResetAt same, but drop is >= 25 points (50% -> 25%): rolling reset detected!
        var curRolling = Reading(usedPercent: 25, resetAt: prevReset);
        var rollingResult = EarlyResetDetector.Detect(prev, curRolling, Now);
        rollingResult.Should().NotBeNull();
        rollingResult!.PreviousPercent.Should().Be(50);
        rollingResult.CurrentPercent.Should().Be(25);
    }

    [TestMethod]
    public void Detect_DropToNearZero_FromAtLeast10Points_TreatedAsEarlyResetEvenIfResetAtNotShifted()
    {
        var prevReset = Now.AddHours(2);

        // 15% dropping to 0% with unshifted ResetAt:
        var prev15 = Reading(usedPercent: 15, resetAt: prevReset);
        var cur0 = Reading(usedPercent: 0, resetAt: prevReset);
        var result = EarlyResetDetector.Detect(prev15, cur0, Now);
        result.Should().NotBeNull();
        result!.PreviousPercent.Should().Be(15);
        result.CurrentPercent.Should().Be(0);

        // 12% dropping to 0.5% (<= NearZeroPercent 1.0) and drop is 11.5 >= 10:
        var cur05 = Reading(usedPercent: 0.5, resetAt: prevReset);
        EarlyResetDetector.Detect(prev15, cur05, Now).Should().NotBeNull();

        // 8% dropping to 0% (drop is 8 < 10 points): null
        var prev8 = Reading(usedPercent: 8, resetAt: prevReset);
        EarlyResetDetector.Detect(prev8, cur0, Now).Should().BeNull();
    }

    [TestMethod]
    public void Detect_OverQuotaFluctuations_RequiresMinimum10PointDrop()
    {
        // 120% to 115%: drop is 5 points -> null
        var prev = Reading(usedPercent: 120, resetAt: Now.AddHours(2));
        var cur5 = Reading(usedPercent: 115, resetAt: Now.AddHours(4));
        EarlyResetDetector.Detect(prev, cur5, Now).Should().BeNull();

        // 120% to 105%: drop is 15 points (>= 10) with future reset shifting -> detected!
        var cur15 = Reading(usedPercent: 105, resetAt: Now.AddHours(4));
        var result = EarlyResetDetector.Detect(prev, cur15, Now);
        result.Should().NotBeNull();
        result!.PreviousPercent.Should().Be(120);
        result.CurrentPercent.Should().Be(105);
    }

    [TestMethod]
    public void Detect_UnknownResetAtOnBothSides_RequiresDropOfAtLeast25Points()
    {
        var prev = Reading(usedPercent: 60, resetAt: null);

        // Drop 24.9 points (< 25): null
        var cur249 = Reading(usedPercent: 35.1, resetAt: null);
        EarlyResetDetector.Detect(prev, cur249, Now).Should().BeNull();

        // Drop 25.0 points (>= 25): detected!
        var cur25 = Reading(usedPercent: 35.0, resetAt: null);
        var result = EarlyResetDetector.Detect(prev, cur25, Now);
        result.Should().NotBeNull();
        result!.PreviousPercent.Should().Be(60);
        result.CurrentPercent.Should().Be(35);
        result.PreviousResetAtUtc.Should().BeNull();
        result.NewResetAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Detect_PreviousReadingOlderThan24Hours_ReturnsNull()
    {
        var oldTime = Now.AddHours(-25);
        var prev = Reading(usedPercent: 80, resetAt: Now.AddHours(2), readingAt: oldTime);
        var cur = Reading(usedPercent: 10, resetAt: Now.AddHours(5), readingAt: Now);

        EarlyResetDetector.Detect(prev, cur, Now).Should().BeNull();
    }

    [TestMethod]
    public void DetectAll_MatchesSessionWeeklyTertiaryAndExtraWindows()
    {
        var prevData = new UsageData
        {
            Provider = "claude",
            Session = new() { ReportedPercent = 75, ResetAt = Now.AddHours(2) },
            Weekly = new() { ReportedPercent = 90, ResetAt = Now.AddDays(3) },
            Tertiary = new() { ReportedPercent = 40, ResetAt = Now.AddDays(1) },
            ExtraWindows = [new() { Key = "sonnet", Title = "Sonnet", Window = new() { ReportedPercent = 85, ResetAt = Now.AddHours(4) } }],
            FetchedAt = Now.AddMinutes(-5),
            UsageFetchOutcome = FetchOutcome.Success
        };

        var curData = new UsageData
        {
            Provider = "claude",
            Session = new() { ReportedPercent = 10, ResetAt = Now.AddHours(5) }, // Reset early! (75 -> 10, forward shift)
            Weekly = new() { ReportedPercent = 88, ResetAt = Now.AddDays(3) },  // Jitter (90 -> 88, drop < 10)
            Tertiary = new() { ReportedPercent = 10, ResetAt = Now.AddDays(1) }, // Reset early! (40 -> 10, drop 30 >= 25 rolling)
            ExtraWindows = [new() { Key = "sonnet", Title = "Sonnet", Window = new() { ReportedPercent = 5, ResetAt = Now.AddHours(8) } }], // Reset early! (85 -> 5)
            FetchedAt = Now,
            UsageFetchOutcome = FetchOutcome.Success
        };

        var results = EarlyResetDetector.DetectAll(prevData, curData, Now);
        results.Should().HaveCount(3);
        results.Select(r => r.WindowKey).Should().BeEquivalentTo(["session", "tertiary", "extra:sonnet"]);
    }
}
