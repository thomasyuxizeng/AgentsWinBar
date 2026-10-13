using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class UsageResetTrackerTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void Annotate_IndependentSessionAndWeeklyResets_OnlyMarksOwnWindowAndProjections(bool sessionReset, bool weeklyReset)
    {
        var tracker = new UsageResetTracker();
        var previous = Data(90, Now.AddMinutes(-1), Now) with
        {
            Weekly = new() { ReportedPercent = 90, ResetAt = Now, WindowMinutes = 10080 },
            Tertiary = new() { ReportedPercent = 90, ResetAt = Now, WindowMinutes = 60 },
            ExtraWindows = [new() { Key = "extra", Window = new() { ReportedPercent = 90, ResetAt = Now } }]
        };
        tracker.Annotate(previous, Now.AddMinutes(-1));
        var current = tracker.Annotate(previous with
        {
            FetchedAt = Now, DataUpdatedAt = Now,
            Session = sessionReset ? previous.Session! with { ReportedPercent = 0, ResetAt = Now.AddHours(5) } : previous.Session,
            Weekly = weeklyReset ? previous.Weekly! with { ReportedPercent = 0, ResetAt = Now.AddDays(7) } : previous.Weekly
        }, Now);
        current.Session!.ResetDetectedAtUtc.Should().Be(sessionReset ? Now : null);
        current.Weekly!.ResetDetectedAtUtc.Should().Be(weeklyReset ? Now : null);
        current.Tertiary!.ResetDetectedAtUtc.Should().BeNull();
        current.ExtraWindows.Single().Window.ResetDetectedAtUtc.Should().BeNull();
        var dashboard = DashboardSnapshotBuilder.Build(new() { Providers = [current], NowUtc = Now }).Providers.Single();
        foreach (var window in dashboard.Windows)
            window.ResetDetectedAtUtc.Should().Be(window.Key == "session" && sessionReset || window.Key == "weekly" && weeklyReset ? Now : null);
        WidgetBarSelector.Pick(current, Now, count: 4).Select(b => b.ResetDetectedAtUtc).Should()
            .Equal(sessionReset ? Now : null, weeklyReset ? Now : null, null, null);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Annotate_TertiaryOrExtraResets_LeavesMainWindowsUntouched(bool tertiaryReset)
    {
        var tracker = new UsageResetTracker();
        var previous = Data(90, Now.AddMinutes(-1), Now) with
        {
            Weekly = new() { ReportedPercent = 90, ResetAt = Now },
            Tertiary = new() { ReportedPercent = 90, ResetAt = Now },
            ExtraWindows = [new() { Key = "extra", Window = new() { ReportedPercent = 90, ResetAt = Now } }]
        };
        tracker.Annotate(previous, Now.AddMinutes(-1));
        var current = tracker.Annotate(previous with
        {
            FetchedAt = Now, DataUpdatedAt = Now,
            Tertiary = tertiaryReset ? previous.Tertiary! with { ResetAt = Now.AddHours(1), ReportedPercent = 0 } : previous.Tertiary,
            ExtraWindows = tertiaryReset ? previous.ExtraWindows
                : [previous.ExtraWindows.Single() with { Window = previous.ExtraWindows.Single().Window with
                    { ResetAt = Now.AddHours(1), ReportedPercent = 0 } }]
        }, Now);
        current.Session!.ResetDetectedAtUtc.Should().BeNull();
        current.Weekly!.ResetDetectedAtUtc.Should().BeNull();
        current.Tertiary!.ResetDetectedAtUtc.Should().Be(tertiaryReset ? Now : null);
        current.ExtraWindows.Single().Window.ResetDetectedAtUtc.Should().Be(tertiaryReset ? null : Now);
        var dashboard = DashboardSnapshotBuilder.Build(new() { Providers = [current], NowUtc = Now }).Providers.Single();
        dashboard.Windows.Select(w => w.ResetDetectedAtUtc).Should().Equal(null, null, tertiaryReset ? Now : null, tertiaryReset ? null : Now);
        WidgetBarSelector.Pick(current, Now, count: 4).Select(b => b.ResetDetectedAtUtc).Should()
            .Equal(null, null, tertiaryReset ? Now : null, tertiaryReset ? null : Now);
    }

    [TestMethod]
    public void Annotate_AnotherProviderResets_LeavesThisProviderUntouched()
    {
        var tracker = new UsageResetTracker();
        var before = Data(90, Now.AddMinutes(-1), Now);
        tracker.Annotate(before, Now.AddMinutes(-1));
        tracker.Annotate(before with { Provider = "claude" }, Now.AddMinutes(-1));
        tracker.Annotate(Data(0, Now, Now.AddHours(5)) with { Provider = "claude" }, Now)
            .Session!.ResetDetectedAtUtc.Should().Be(Now);
        tracker.Annotate(before with { FetchedAt = Now, DataUpdatedAt = Now }, Now)
            .Session!.ResetDetectedAtUtc.Should().BeNull();
    }
    private static UsageData Data(double percent, DateTime at, DateTime reset) => new()
    {
        Provider = "codex", FetchedAt = at, DataUpdatedAt = at, UsageFetchOutcome = FetchOutcome.Success,
        Session = new() { ReportedPercent = percent, ResetAt = reset, WindowMinutes = 300 }
    };

    [TestMethod]
    public void Annotate_NaturalReset_SetsDetectionTimestamp()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddMinutes(-5), Now.AddSeconds(-5)), Now.AddMinutes(-5));
        tracker.Annotate(Data(2, Now, Now.AddHours(5)), Now).Session!.ResetDetectedAtUtc.Should().Be(Now);
    }

    [TestMethod]
    public void Annotate_NaturalRolloverWithoutLargeDrop_SetsDetectionTimestamp()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(1, Now.AddMinutes(-5), Now), Now.AddMinutes(-5));
        tracker.Annotate(Data(0, Now, Now.AddHours(5)), Now).Session!.ResetDetectedAtUtc.Should().Be(Now);
    }

    [TestMethod]
    public void Annotate_ShortWindowRolloverWithoutDrop_SetsDetectionTimestamp()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(1, Now.AddMinutes(-1), Now), Now.AddMinutes(-1));
        tracker.Annotate(Data(1, Now, Now.AddMinutes(5)), Now).Session!.ResetDetectedAtUtc.Should().Be(Now);
    }

    [TestMethod]
    public void Annotate_ResetTimeAdjustmentBeforeDueWithoutDrop_DoesNotSignalReset()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(1, Now.AddMinutes(-1), Now.AddMinutes(1)), Now.AddMinutes(-1));
        tracker.Annotate(Data(1, Now, Now.AddMinutes(5)), Now).Session!.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_DueWindowWithDropAndUnchangedReset_SetsDetectionTimestamp()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddMinutes(-5), Now), Now.AddMinutes(-5));
        tracker.Annotate(Data(0, Now, Now), Now).Session!.ResetDetectedAtUtc.Should().Be(Now);
    }

    [TestMethod]
    public void Annotate_EarlyReset_ReusesExistingPredicate()
    {
        var tracker = new UsageResetTracker();
        var previous = Data(90, Now.AddMinutes(-5), Now.AddHours(2));
        var current = Data(0, Now, Now.AddHours(4));
        EarlyResetDetector.DetectAll(previous, current, Now).Should().ContainSingle();
        tracker.Annotate(previous, Now.AddMinutes(-5));
        tracker.Annotate(current, Now).Session!.ResetDetectedAtUtc.Should().Be(Now);
    }

    [TestMethod]
    public void Annotate_FirstReading_DoesNotSignalReset()
    {
        new UsageResetTracker().Annotate(Data(0, Now, Now.AddHours(5)), Now)
            .Session!.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_StaleBaseline_DoesNotSignalReset()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddHours(-25), Now.AddHours(-24)), Now.AddHours(-25));
        tracker.Annotate(Data(0, Now, Now.AddHours(5)), Now).Session!.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    [DataRow(true, false, FetchOutcome.Success)]
    [DataRow(false, true, FetchOutcome.Success)]
    [DataRow(false, false, FetchOutcome.Failed)]
    [DataRow(false, false, FetchOutcome.Unavailable)]
    public void Annotate_CachedStaleOrFailedRefresh_DoesNotSignalOrReplaceBaseline(bool cached, bool stale, FetchOutcome outcome)
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddMinutes(-5), Now), Now.AddMinutes(-5));
        var invalid = Data(0, Now, Now.AddHours(5)) with { UsageIsCached = cached, UsageIsStale = stale, UsageFetchOutcome = outcome };
        tracker.Annotate(invalid, Now).Session!.ResetDetectedAtUtc.Should().BeNull();
        tracker.Annotate(Data(0, Now.AddSeconds(1), Now.AddHours(5)), Now.AddSeconds(1))
            .Session!.ResetDetectedAtUtc.Should().Be(Now.AddSeconds(1));
    }

    [TestMethod]
    public void Annotate_Restart_DoesNotUsePreviousSessionOrPersistedMarker()
    {
        var data = Data(90, Now.AddMinutes(-5), Now);
        new UsageResetTracker().Annotate(data, Now.AddMinutes(-5));
        var incoming = Data(0, Now, Now.AddHours(5));
        incoming = incoming with { Session = incoming.Session! with { ResetDetectedAtUtc = Now.AddSeconds(-5) } };
        new UsageResetTracker().Annotate(incoming, Now).Session!.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_DetectionThenNormalReading_RetainsSignalUntilNextDetection()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddMinutes(-5), Now), Now.AddMinutes(-5));
        tracker.Annotate(Data(0, Now, Now.AddHours(5)), Now);
        tracker.Annotate(Data(20, Now.AddMinutes(1), Now.AddHours(5)), Now.AddMinutes(1))
            .Session!.ResetDetectedAtUtc.Should().Be(Now);
        var next = Now.AddHours(5);
        tracker.Annotate(Data(0, next, next.AddHours(5)), next).Session!.ResetDetectedAtUtc.Should().Be(next);
    }

    [TestMethod]
    public void Annotate_ReplayedObservation_DoesNotSignalReset()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now, Now), Now);
        tracker.Annotate(Data(0, Now, Now.AddHours(5)), Now).Session!.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_OlderObservationMissingWindow_DoesNotEraseFreshBaseline()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddMinutes(-1), Now), Now.AddMinutes(-1));
        tracker.Annotate(Data(90, Now.AddMinutes(-2), Now) with { Session = null }, Now);
        tracker.Annotate(Data(0, Now, Now.AddHours(5)), Now).Session!.ResetDetectedAtUtc.Should().Be(Now);
    }

    [TestMethod]
    public void Annotate_CachedFirstReading_DoesNotBecomeBaseline()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddMinutes(-1), Now) with { UsageIsCached = true }, Now.AddMinutes(-1));
        tracker.Annotate(Data(0, Now, Now.AddHours(5)), Now).Session!.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_FailedRefreshAfterDetection_PreservesTimestampWithoutNewSignal()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddMinutes(-1), Now), Now.AddMinutes(-1));
        tracker.Annotate(Data(0, Now, Now.AddHours(5)), Now);
        tracker.Annotate(Data(90, Now.AddSeconds(1), Now) with { UsageFetchOutcome = FetchOutcome.Failed }, Now.AddSeconds(1))
            .Session!.ResetDetectedAtUtc.Should().Be(Now);
    }

    [TestMethod]
    public void Annotate_OldCliObservation_DoesNotCreateBaseline()
    {
        var tracker = new UsageResetTracker();
        var old = Data(90, Now, Now) with { DataUpdatedAt = Now.AddMinutes(-20) };
        tracker.Annotate(old, Now);
        tracker.Annotate(Data(0, Now.AddSeconds(1), Now.AddHours(5)), Now.AddSeconds(1))
            .Session!.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_PlanLimitOrWindowLengthChanged_StartsNewBaseline()
    {
        foreach (var change in new[] { "plan", "limit", "duration" })
        {
            var tracker = new UsageResetTracker();
            tracker.Annotate(Data(90, Now.AddMinutes(-5), Now), Now.AddMinutes(-5));
            var current = Data(0, Now, Now.AddHours(5));
            current = current with { Plan = change == "plan" ? "new plan" : null,
                Session = current.Session! with { Limit = change == "limit" ? 100 : 0,
                    WindowMinutes = change == "duration" ? 600 : 300 } };
            tracker.Annotate(current, Now).Session!.ResetDetectedAtUtc.Should().BeNull();
        }
    }

    [TestMethod]
    public void RetainProviders_DisabledAndReenabledProvider_DoesNotSignalReset()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddMinutes(-5), Now), Now.AddMinutes(-5));
        tracker.RetainProviders(["claude"]);
        tracker.Annotate(Data(0, Now, Now.AddHours(5)), Now).Session!.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_ProviderSwitch_DoesNotCompareDifferentProviders()
    {
        var tracker = new UsageResetTracker();
        tracker.Annotate(Data(90, Now.AddMinutes(-5), Now), Now.AddMinutes(-5));
        tracker.Annotate(Data(0, Now, Now.AddHours(5)) with { Provider = "claude" }, Now)
            .Session!.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Annotate_AllWindowKinds_ProjectsSignalsThroughDashboardAndWidgets()
    {
        UsageData All(UsageData data) => data with { Weekly = data.Session, Tertiary = data.Session,
            ExtraWindows = [new() { Key = "fable", Title = "Fable", Window = data.Session! }] };
        var tracker = new UsageResetTracker();
        tracker.Annotate(All(Data(90, Now.AddMinutes(-5), Now)), Now.AddMinutes(-5));
        var current = tracker.Annotate(All(Data(0, Now, Now.AddHours(5))), Now);
        var snapshot = DashboardSnapshotBuilder.Build(new() { Providers = [current], NowUtc = Now });
        snapshot.Providers.Single().Windows.Should().HaveCount(4).And.AllSatisfy(w => w.ResetDetectedAtUtc.Should().Be(Now));
        snapshot.Providers.Single().ResetDetectedAtUtc.Should().Be(Now);
        WidgetBarSelector.Pick(current, Now).Should().AllSatisfy(w => w.ResetDetectedAtUtc.Should().Be(Now));
        snapshot.Providers.Single().Windows.Should().AllSatisfy(w => w.EarlyResetAtUtc.Should().BeNull());
    }

    [TestMethod]
    public void Annotate_DuplicateExtraKeys_InvalidateBaseline()
    {
        var tracker = new UsageResetTracker();
        UsageData Extra(UsageData data, bool duplicate = false) => data with { Session = null,
            ExtraWindows = Enumerable.Range(0, duplicate ? 2 : 1).Select(_ => new ExtraUsageWindow
                { Key = "extra", Title = "Extra", Window = data.Session! }).ToArray() };
        tracker.Annotate(Extra(Data(90, Now.AddMinutes(-5), Now)), Now.AddMinutes(-5));
        tracker.Annotate(Extra(Data(90, Now.AddMinutes(-1), Now), true), Now.AddMinutes(-1));
        tracker.Annotate(Extra(Data(0, Now, Now.AddHours(5))), Now).ExtraWindows.Single().Window.ResetDetectedAtUtc.Should().BeNull();
    }

    [TestMethod]
    public void Serialize_SessionSignal_IsExcludedFromCache()
    {
        var window = new UsageWindow { ResetDetectedAtUtc = Now };
        JsonSerializer.Serialize(window).Should().NotContain(nameof(UsageWindow.ResetDetectedAtUtc));
    }
}
