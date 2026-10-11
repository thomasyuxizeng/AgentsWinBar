using System.Globalization;
using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

/// <summary>
/// Backtest of the detection rule against real Codex usage-history transitions (Oct 4-11 2026, 6,580 samples):
/// every drop of 10+ points in that history. Twelve are normal session resets (the scheduled reset time had
/// passed, sometimes by hours because the app was asleep); two are real early weekly resets (OpenAI reset
/// everyone's limits: Oct 7, 53% to 0% with 74 h to go; Oct 11, 81% to 0% with 81 h to go).
/// </summary>
[TestClass]
public class EarlyResetRealHistoryTests
{
    private static DateTime T(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // key, prevPercent, curPercent, prevSampleAt, curAt, prevResetsAt, newResetsAt, expectedEarly
    [TestMethod]
    [DataRow("session", 100.0, 0.0, "2026-10-04T15:29:55Z", "2026-10-04T15:39:48Z", "2026-10-04T15:36:50Z", "2026-10-04T20:39:47Z", false)]
    [DataRow("session", 66.0, 0.0, "2026-10-04T20:34:48Z", "2026-10-04T20:44:48Z", "2026-10-04T20:44:02Z", "2026-10-05T01:44:47Z", false)]
    [DataRow("session", 100.0, 0.0, "2026-10-05T17:55:25Z", "2026-10-06T03:21:48Z", "2026-10-05T18:25:49Z", "2026-10-06T08:21:46Z", false)]
    [DataRow("session", 32.0, 0.0, "2026-10-06T06:49:19Z", "2026-10-06T09:21:47Z", "2026-10-06T08:33:44Z", "2026-10-06T14:21:45Z", false)]
    [DataRow("session", 55.0, 0.0, "2026-10-07T18:12:52Z", "2026-10-07T18:23:21Z", "2026-10-07T18:20:24Z", "2026-10-07T23:23:20Z", false)]
    [DataRow("session", 100.0, 0.0, "2026-10-08T07:33:34Z", "2026-10-08T07:39:04Z", "2026-10-08T07:35:31Z", "2026-10-08T12:39:03Z", false)]
    [DataRow("session", 100.0, 0.0, "2026-10-08T12:37:36Z", "2026-10-08T12:43:02Z", "2026-10-08T12:40:01Z", "2026-10-08T17:42:59Z", false)]
    [DataRow("session", 73.0, 0.0, "2026-10-08T17:32:29Z", "2026-10-08T17:43:59Z", "2026-10-08T17:43:02Z", "2026-10-08T22:43:55Z", false)]
    [DataRow("session", 16.0, 0.0, "2026-10-08T23:03:03Z", "2026-10-08T23:08:18Z", "2026-10-08T23:05:17Z", "2026-10-09T04:08:18Z", false)]
    [DataRow("session", 89.0, 0.0, "2026-10-10T06:36:27Z", "2026-10-10T09:29:29Z", "2026-10-10T08:48:25Z", "2026-10-10T14:29:28Z", false)]
    [DataRow("session", 68.0, 0.0, "2026-10-10T19:25:25Z", "2026-10-10T19:30:40Z", "2026-10-10T19:30:22Z", "2026-10-11T00:30:39Z", false)]
    [DataRow("session", 14.0, 0.0, "2026-10-11T01:11:36Z", "2026-10-11T01:22:09Z", "2026-10-11T01:18:33Z", "2026-10-11T06:22:08Z", false)]
    [DataRow("weekly", 53.0, 0.0, "2026-10-07T03:26:15Z", "2026-10-07T03:31:42Z", "2026-10-10T05:31:49Z", "2026-10-14T03:31:42Z", true)]
    [DataRow("weekly", 81.0, 0.0, "2026-10-11T03:23:17Z", "2026-10-11T03:58:59Z", "2026-10-14T13:20:24Z", "2026-10-18T03:58:58Z", true)]
    public void Detect_RealCodexHistoryTransitions_FlagsOnlyTheTwoEarlyWeeklyResets(
        string key, double prevPercent, double curPercent, string prevSampleAt, string curAt,
        string prevResetsAt, string newResetsAt, bool expectedEarly)
    {
        var title = key == "weekly" ? "Weekly" : "Session";
        var previous = new EarlyResetWindowReading("codex", key, title, prevPercent, T(prevResetsAt), T(prevSampleAt));
        var current = new EarlyResetWindowReading("codex", key, title, curPercent, T(newResetsAt), T(curAt));

        var result = EarlyResetDetector.Detect(previous, current, T(curAt));

        (result != null).Should().Be(expectedEarly);
        if (expectedEarly)
        {
            result!.PreviousPercent.Should().Be(prevPercent);
            result.CurrentPercent.Should().Be(curPercent);
            result.PreviousResetAtUtc.Should().Be(T(prevResetsAt));
        }
    }
}
