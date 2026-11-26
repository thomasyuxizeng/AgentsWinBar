using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ResetRefreshOutcomeTests
{
    private static readonly DateTime Reset = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static EarlyResetWindowReading Target(string provider = "codex", string key = "session") =>
        new(provider, key, key, 90, Reset, Reset.AddMinutes(-1));
    private static UsageData Result() => new()
    {
        Provider = "codex", FetchedAt = Reset.AddSeconds(12), UsageFetchOutcome = FetchOutcome.Success,
        Session = new() { ReportedPercent = 90, ResetAt = Reset },
        Weekly = new() { ReportedPercent = 90, ResetAt = Reset }
    };

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void Describe_AdvanceOrDrop_ReportsNewOtherwiseOld(bool advance, bool drop)
    {
        var data = Result();
        data = data with { Session = data.Session! with
            { ReportedPercent = drop ? 0 : 90, ResetAt = advance ? Reset.AddHours(5) : Reset } };
        ResetRefreshOutcome.Describe([Target()], data, Reset.AddSeconds(12))
            .Should().Be($"session {(advance || drop ? "NEW" : "OLD")} +12.0s");
    }

    [TestMethod]
    public void Describe_MultipleWindows_ReportsEachTargetIndependently()
    {
        var data = Result();
        data = data with { Session = data.Session! with { ResetAt = Reset.AddHours(5) } };
        ResetRefreshOutcome.Describe([Target(), Target(key: "weekly"), Target("claude")], data, Reset.AddSeconds(16))
            .Should().Be("session NEW +16.0s; weekly OLD +16.0s");
        ResetRefreshOutcome.Describe([Target("claude")], data, Reset.AddSeconds(16)).Should().BeNull();
    }

    [TestMethod]
    [DataRow("cached", "cached/stale")]
    [DataRow("stale", "cached/stale")]
    [DataRow("failed", "unavailable")]
    [DataRow("missing", "missing")]
    public void Describe_UnconfirmedResult_DoesNotReportNew(string kind, string qualifier)
    {
        var data = Result();
        data = data with
        {
            UsageIsCached = kind == "cached", UsageIsStale = kind == "stale",
            UsageFetchOutcome = kind == "failed" ? FetchOutcome.Failed : FetchOutcome.Success,
            Session = kind == "missing" ? null : data.Session! with { ResetAt = Reset.AddHours(5), ReportedPercent = 0 }
        };
        ResetRefreshOutcome.Describe([Target()], data, Reset.AddSeconds(12))
            .Should().Be($"session OLD ({qualifier}) +12.0s");
    }

    [TestMethod]
    public void Describe_ExtraAndTertiaryTargets_ReportsOwnKeys()
    {
        var data = Result() with
        {
            Tertiary = new() { ReportedPercent = 0, ResetAt = Reset.AddHours(1) },
            ExtraWindows = [new() { Key = "extra", Title = "Extra", Window = new() { ReportedPercent = 90, ResetAt = Reset } }]
        };
        ResetRefreshOutcome.Describe([Target(key: "tertiary"), Target(key: "extra:extra")], data, Reset.AddSeconds(12))
            .Should().Be("tertiary NEW +12.0s; extra:extra OLD +12.0s");
    }

    [TestMethod]
    public void Describe_ReplayedOrCachedBaseline_MarksResultUnconfirmed()
    {
        ResetRefreshOutcome.Describe([Target() with { IsCached = true }], Result(), Reset.AddSeconds(12))
            .Should().Be("session OLD (unconfirmed) +12.0s");
        ResetRefreshOutcome.Describe([Target() with { ReadingAtUtc = Reset.AddSeconds(12) }], Result(), Reset.AddSeconds(12))
            .Should().Be("session OLD (unconfirmed) +12.0s");
    }
}
