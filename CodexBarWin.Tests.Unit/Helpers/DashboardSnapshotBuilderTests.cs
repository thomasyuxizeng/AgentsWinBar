using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class DashboardSnapshotBuilderTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, TimeZoneInfo.Local));
    private static UsageWindow Window(double percent, int remaining = 150) => new()
    { ReportedPercent = percent, WindowMinutes = 300, ResetAt = Now.AddMinutes(remaining), LastChangedAt = Now.AddMinutes(-1) };
    private static UsageData Provider(string id = "claude") => new() { Provider = id, FetchedAt = Now };
    private static DashboardInputs Inputs(params UsageData[] providers) => new() { Providers = providers, NowUtc = Now };

    [TestMethod]
    public void Build_ThresholdBoundaries_CountsWindowsAndSelectsCallouts()
    {
        var inputs = Inputs(Provider() with { Session = Window(95), Weekly = Window(94.99, 100),
            Tertiary = Window(80, 200), ExtraWindows = [new() { Key = "low", Title = "GPT", Window = Window(79.99, -1) }] },
            Provider("codex") with { Session = Window(100, 50) });
        inputs = inputs with { LastRefreshUtc = Now.AddSeconds(-20), Settings = new() { RefreshIntervalSeconds = 120 } };
        var snapshot = DashboardSnapshotBuilder.Build(inputs);
        snapshot.Summary.ProviderCount.Should().Be(2);
        snapshot.Summary.HealthyProviderCount.Should().Be(2);
        snapshot.Summary.WindowCount.Should().Be(5);
        snapshot.Summary.WindowsAtLimit.Should().Be(2);
        snapshot.Summary.WindowsNearLimit.Should().Be(2);
        snapshot.Summary.WindowsAheadOfPace.Should().Be(3);
        snapshot.Summary.MostUsedWindow.Should().BeEquivalentTo(new DashboardWindowRef
        { ProviderId = "codex", ProviderName = "Codex", WindowTitle = "Session", UsedPercent = 100, ResetsAtUtc = Now.AddMinutes(50) });
        snapshot.Summary.NextResetWindow.Should().BeEquivalentTo(snapshot.Summary.MostUsedWindow);
        snapshot.Summary.LastRefreshUtc.Should().Be(Now.AddSeconds(-20));
        snapshot.Summary.NextRefreshUtc.Should().Be(Now.AddSeconds(100));
    }

    [TestMethod]
    public void Build_AllWindowKinds_PreservesOrderLabelsKeysAndAccents()
    {
        var snapshot = DashboardSnapshotBuilder.Build(Inputs(Provider() with
        {
            Session = Window(1), Weekly = Window(2), Tertiary = Window(3),
            ExtraWindows = [new() { Key = "pool", Title = "Claude and GPT", Window = Window(4) },
                new() { Title = " Fable only ", Window = Window(5) }]
        }, Provider("gemini") with { Session = Window(10) }, Provider("custom")));
        var windows = snapshot.Providers[0].Windows;
        windows.Select(w => w.Key).Should().Equal("session", "weekly", "tertiary", "extra:pool", "extra:fable only");
        windows.Select(w => w.Kind).Should().Equal(DashboardWindowKind.Session, DashboardWindowKind.Weekly,
            DashboardWindowKind.Tertiary, DashboardWindowKind.Extra, DashboardWindowKind.Extra);
        windows.Select(w => w.Title).Should().Equal("Session", "Weekly", "Current week (Sonnet)", "Claude and GPT", " Fable only ");
        windows.Select(w => w.Accent).Should().Equal(WindowAccent.None, WindowAccent.None, WindowAccent.Claude,
            WindowAccent.ClaudeAndOpenAi, WindowAccent.Claude);
        snapshot.Providers[1].Windows.Single().Title.Should().Be("5-hour");
        snapshot.Providers[1].DisplayName.Should().Be("Gemini");
        snapshot.Providers[2].DisplayName.Should().Be("custom");
    }

    [TestMethod]
    public void Build_InjectedTime_ComputesPaceElapsedResetAndStaleness()
    {
        var window = Window(75);
        var data = Provider() with { Session = window };
        var first = DashboardSnapshotBuilder.Build(Inputs(data));
        var projected = first.Providers.Single().Windows.Single();
        projected.ElapsedPercent.Should().Be(50);
        projected.Pace.Should().BeEquivalentTo(PaceCalculator.Project(75, 300, window.ResetAt, Now));
        projected.Pace.Kind.Should().Be(PaceKind.WillRunOut);
        projected.PaceSummary.Should().Be(PaceCalculator.Summarize(projected.Pace));
        projected.ResetText.Should().Be("Resets in 2h 30m");
        projected.LastChangedAtUtc.Should().Be(Now.AddMinutes(-1));
        first.Providers.Single().IsStale.Should().BeFalse();
        var later = DashboardSnapshotBuilder.Build(Inputs(data) with { NowUtc = Now.AddMinutes(100) });
        later.Providers.Single().IsStale.Should().BeTrue();
        later.Providers.Single().Windows.Single().Pace.Kind.Should().Be(PaceKind.OnTrack);
        later.Providers.Single().Windows.Single().ElapsedPercent.Should().BeApproximately(83.33333, .00001);
    }

    [TestMethod]
    public void Build_MissingTimingOrZeroLength_ReturnsUnknownAndCliFallback()
    {
        var snapshot = DashboardSnapshotBuilder.Build(Inputs(Provider() with
        { Session = new() { ResetIn = "  CLI reset text  " }, Weekly = Window(50) with { WindowMinutes = 0 } }));
        snapshot.Providers.Single().Windows.Should().OnlyContain(w => w.ElapsedPercent == null && w.Pace.Kind == PaceKind.Unknown);
        snapshot.Providers.Single().Windows[0].ResetText.Should().Be("CLI reset text");
        snapshot.Providers.Single().Windows[0].History.Message.Should().Be("Window timing unavailable");
    }

    [TestMethod]
    public void Build_ErrorStaleAndIncidentProviders_CountsIndependentHealthStates()
    {
        var snapshot = DashboardSnapshotBuilder.Build(Inputs(Provider() with { Error = "failed" },
            Provider("codex") with { FetchedAt = Now.AddMinutes(-6), ServiceStatus = new() { Indicator = ProviderStatusIndicator.Major } },
            Provider("gemini") with { ServiceStatus = new() { Indicator = ProviderStatusIndicator.Maintenance } }));
        snapshot.Summary.HealthyProviderCount.Should().Be(1);
        snapshot.Summary.ErrorProviderCount.Should().Be(1);
        snapshot.Summary.StaleProviderCount.Should().Be(1);
        snapshot.Summary.IncidentCount.Should().Be(2);
        snapshot.Summary.WindowCount.Should().Be(0);
        snapshot.Summary.MostUsedWindow.Should().BeNull();
        snapshot.Summary.NextResetWindow.Should().BeNull();
        snapshot.Providers[0].Error.Should().Be("failed");
    }

    [TestMethod]
    public void Build_CostData_AggregatesThirtyDaysTokensAveragePeakAndModels()
    {
        var models = Enumerable.Range(0, 6).Select(i => new ModelCost { ModelName = "model" + i, Cost = i, TotalTokens = 1 }).ToList();
        models.Add(new() { ModelName = "unpriced", TotalTokens = 1000 });
        var cost = new CostData { TodayCostUSD = 4, TodayTokens = 40, Last30DaysCostUSD = 12, Last30DaysTokens = 120,
            FetchedAt = Now.AddMinutes(-2), Provenance = "listPriceEstimate", Coverage = new() { Priced = 6, Unpriced = 1 },
            Daily = [new() { Date = Today, TotalCost = 4, TotalTokens = 40, InputTokens = 10, OutputTokens = 11,
                CacheReadTokens = 12, CacheCreationTokens = 13, ReasoningTokens = 14, ModelBreakdowns = models },
                new() { Date = Today, TotalCost = 2, TotalTokens = 20, InputTokens = 1, OutputTokens = 2,
                    CacheReadTokens = 3, CacheCreationTokens = 4, ReasoningTokens = 5 },
                new() { Date = Today.AddDays(-1), TotalTokens = 60, InputTokens = 2, OutputTokens = 3,
                    CacheReadTokens = 4, CacheCreationTokens = 5, ReasoningTokens = 6 },
                new() { Date = Today.AddDays(-30), TotalCost = 999, InputTokens = 999 },
                new() { Date = Today.AddDays(1), TotalCost = 999, InputTokens = 999 }] };
        var snapshot = DashboardSnapshotBuilder.Build(Inputs(Provider() with { Cost = cost },
            Provider("codex") with { Cost = new() { TodayCostUSD = 3, TodayTokens = 30, Last30DaysCostUSD = 5, Last30DaysTokens = 50 } },
            Provider("gemini") with { Cost = new() }));
        snapshot.Summary.CostAvailable.Should().BeTrue();
        snapshot.Summary.TotalCostToday.Should().Be(7);
        snapshot.Summary.TotalCost30Days.Should().Be(17);
        snapshot.Summary.TotalTokensToday.Should().Be(70);
        snapshot.Summary.TotalTokens30Days.Should().Be(170);
        snapshot.Providers[2].Cost.Should().BeNull();
        var result = snapshot.Providers[0].Cost!;
        result.Daily.Should().HaveCount(30);
        result.Daily[0].Date.Should().Be(Today.AddDays(-29));
        result.Daily[^1].Date.Should().Be(Today);
        result.Daily.Take(28).Should().OnlyContain(d => d.TotalCost == 0 && d.TotalTokens == 0);
        result.InputTokens.Should().Be(13); result.OutputTokens.Should().Be(16);
        result.CacheReadTokens.Should().Be(19); result.CacheCreationTokens.Should().Be(22);
        result.ReasoningTokens.Should().Be(25);
        result.AverageDailyCost.Should().Be(3);
        result.PeakDay!.Date.Should().Be(Today); result.PeakDay.TotalCost.Should().Be(6);
        result.TopModels.Should().HaveCount(7);
        result.TopModels[^1].ModelName.Should().Be("unpriced"); result.TopModels[^1].Cost.Should().BeNull();
        result.TopModels[0].ModelName.Should().Be("model5");
        result.CapturedAtUtc.Should().Be(cost.FetchedAt);
        result.Provenance.Should().Be(cost.Provenance); result.Coverage.Should().BeEquivalentTo(cost.Coverage);
    }

    [TestMethod]
    public void Build_MoreThanEightModels_CapsTopModels()
    {
        var cost = new CostData { Last30DaysTokens = 10, Daily = [new() { Date = Today,
            ModelBreakdowns = Enumerable.Range(0, 12).Select(i => new ModelCost { ModelName = "m" + i, Cost = i }).ToArray() }] };
        DashboardSnapshotBuilder.Build(Inputs(Provider() with { Cost = cost })).Providers.Single().Cost!.TopModels.Should().HaveCount(8);
    }

    [TestMethod]
    public void Build_CostToggleOff_RemovesCostsAndZerosTotals()
    {
        var snapshot = DashboardSnapshotBuilder.Build(Inputs(Provider() with { Cost = new() {
            TodayCostUSD = 9, TodayTokens = 90, Last30DaysCostUSD = 99, Last30DaysTokens = 990 } })
            with { Settings = new() { ShowCostTotals = false } });
        snapshot.Providers.Single().Cost.Should().BeNull();
        snapshot.Summary.CostAvailable.Should().BeFalse();
        snapshot.Summary.TotalCostToday.Should().Be(0); snapshot.Summary.TotalCost30Days.Should().Be(0);
        snapshot.Summary.TotalTokensToday.Should().Be(0); snapshot.Summary.TotalTokens30Days.Should().Be(0);
        snapshot.Diagnostics.CostTotalsEnabled.Should().BeFalse();
    }

    [TestMethod]
    public void Build_CostTotalsWithoutDailyUsage_LeavesAverageAndPeakUnknown()
    {
        var cost = DashboardSnapshotBuilder.Build(Inputs(Provider() with { Cost = new() { Last30DaysTokens = 1 } })).Providers.Single().Cost!;
        cost.Daily.Should().HaveCount(30); cost.AverageDailyCost.Should().BeNull(); cost.PeakDay.Should().BeNull();
    }

    [TestMethod]
    public void Build_HistorySamples_FiltersProviderAndWindowAndReportsExtents()
    {
        UsageHistorySample Sample(string provider, string key, int minutes, double percent) => new()
        { Provider = provider, WindowKey = key, Timestamp = Now.AddMinutes(minutes), ResetsAt = Now.AddMinutes(150), UsedPercent = percent };
        var snapshot = DashboardSnapshotBuilder.Build(Inputs(Provider() with { Session = Window(50) }, Provider("codex")) with
        { HistorySamples = [Sample("CLAUDE", "session", -20, 20), Sample("claude", "session", -10, 30),
            Sample("claude", "weekly", -5, 40), Sample("codex", "session", -30, 70)], HistoryFileBytes = 123 });
        snapshot.Providers[0].HistorySampleCount.Should().Be(3);
        snapshot.Providers[0].HistoryOldestUtc.Should().Be(Now.AddMinutes(-20));
        snapshot.Providers[0].Windows.Single().History.SampleCount.Should().Be(2);
        snapshot.Providers[0].Windows.Single().History.IsReady.Should().BeTrue();
        snapshot.Providers[1].HistorySampleCount.Should().Be(1);
        snapshot.Diagnostics.HistorySampleCount.Should().Be(4);
        snapshot.Diagnostics.HistoryOldestUtc.Should().Be(Now.AddMinutes(-30));
        snapshot.Diagnostics.HistoryNewestUtc.Should().Be(Now.AddMinutes(-5));
        snapshot.Diagnostics.HistoryFileBytes.Should().Be(123);
    }

    [TestMethod]
    public void Build_DiagnosticsAndRecentActivity_MapsSettingsAndCapsNewestFirst()
    {
        var cache = new DashboardCostCacheEntry { ProviderId = "claude", LastAttemptUtc = Now, HasData = true };
        var snapshot = DashboardSnapshotBuilder.Build(Inputs() with
        {
            Settings = new() { Theme = AppTheme.Dark, RefreshIntervalSeconds = 42, CacheExpiryMinutes = 7,
                AlwaysOnOverlayEnabled = true, TaskbarDisplayEnabled = true, AlertsEnabled = false,
                DeveloperModeEnabled = true, StartWithWindows = true,
                Timeouts = new() { WslCommandTimeoutSeconds = 51, CliProviderTimeoutSeconds = 52, StandardProviderTimeoutSeconds = 53 } },
            CliVersion = "1.2", CliAvailable = true, AppVersion = "2.3", WslDistro = "Ubuntu", SettingsPath = "settings.json",
            RecentRefreshes = Enumerable.Range(0, 70).Select(i => new DashboardRefreshEntry { TimestampUtc = Now.AddSeconds(i) }).ToArray(),
            RecentAlerts = Enumerable.Range(0, 50).Select(i => new DashboardAlertEntry { TimestampUtc = Now.AddSeconds(i) }).ToArray(), CostCache = [cache]
        });
        snapshot.GeneratedAtUtc.Should().Be(Now);
        var d = snapshot.Diagnostics;
        d.Theme.Should().Be("Dark"); d.RefreshIntervalSeconds.Should().Be(42); d.CacheExpiryMinutes.Should().Be(7);
        d.WslCommandTimeoutSeconds.Should().Be(51); d.CliProviderTimeoutSeconds.Should().Be(52); d.StandardProviderTimeoutSeconds.Should().Be(53);
        d.OverlayEnabled.Should().BeTrue(); d.TaskbarStripEnabled.Should().BeTrue(); d.AlertsEnabled.Should().BeFalse();
        d.CostTotalsEnabled.Should().BeTrue(); d.DeveloperModeEnabled.Should().BeTrue(); d.StartWithWindows.Should().BeTrue();
        d.CliVersion.Should().Be("1.2"); d.CliAvailable.Should().BeTrue(); d.AppVersion.Should().Be("2.3");
        d.WslDistro.Should().Be("Ubuntu"); d.SettingsPath.Should().Be("settings.json"); d.CostCache.Should().Equal(cache);
        d.RecentRefreshes.Should().HaveCount(40); d.RecentRefreshes[0].TimestampUtc.Should().Be(Now.AddSeconds(69));
        d.RecentRefreshes.Should().BeInDescendingOrder(e => e.TimestampUtc);
        d.RecentAlerts.Should().HaveCount(20); d.RecentAlerts[0].TimestampUtc.Should().Be(Now.AddSeconds(49));
        d.RecentAlerts.Should().BeInDescendingOrder(e => e.TimestampUtc);
    }

    [TestMethod]
    public void Build_NullOrEmptyInputs_ReturnsEmptySnapshotWithoutThrowing()
    {
        var snapshot = DashboardSnapshotBuilder.Build(new() { NowUtc = Now, Providers = null!, Settings = null!,
            HistorySamples = null!, RecentRefreshes = null!, RecentAlerts = null!, CostCache = null! });
        snapshot.Providers.Should().BeEmpty(); snapshot.Summary.ProviderCount.Should().Be(0);
        snapshot.Summary.CostAvailable.Should().BeFalse(); snapshot.Summary.NextRefreshUtc.Should().BeNull();
        snapshot.Summary.MostUsedWindow.Should().BeNull(); snapshot.Summary.NextResetWindow.Should().BeNull();
        snapshot.Diagnostics.HistoryOldestUtc.Should().BeNull(); snapshot.Diagnostics.HistoryNewestUtc.Should().BeNull();
        snapshot.Diagnostics.RecentRefreshes.Should().BeEmpty(); snapshot.Diagnostics.RecentAlerts.Should().BeEmpty();
        snapshot.Diagnostics.CostCache.Should().BeEmpty();
        DashboardSnapshotBuilder.Build(Inputs(Provider() with { ExtraWindows = null!, Cost = new() { Daily = null! } }))
            .Providers.Single().Cost.Should().BeNull();
    }

    [TestMethod]
    public void Build_DuplicateAndEmptyExtraKeys_AssignsDistinctDeterministicKeys()
    {
        var input = Inputs(Provider() with { ExtraWindows = [
            new() { Key = "pool", Window = Window(1) },
            new() { Key = "pool", Window = Window(2) },
            new() { Key = "pool#2", Window = Window(3) },
            new() { Window = Window(4) }, new() { Title = null!, Window = Window(5) }] });
        var windows = DashboardSnapshotBuilder.Build(input).Providers[0].Windows;
        windows.Select(w => w.Key).Distinct().Count().Should().Be(5);
        windows.Select(w => w.Key).Should().Equal(DashboardSnapshotBuilder.Build(input).Providers[0].Windows.Select(w => w.Key));
        windows.Select(w => w.UsedPercent).Should().Equal(1, 2, 3, 4, 5);
    }

    [TestMethod]
    [DataRow(double.NaN, 0d)]
    [DataRow(double.PositiveInfinity, 0d)]
    [DataRow(-10d, 0d)]
    [DataRow(140d, 100d)]
    public void Build_InvalidPercent_ReturnsFiniteBoundedUsageAndPace(double percent, double expected)
    {
        var snapshot = DashboardSnapshotBuilder.Build(Inputs(Provider() with { Session = Window(percent) }));
        var window = snapshot.Providers[0].Windows[0];
        window.UsedPercent.Should().Be(expected);
        double.IsFinite(window.Pace.ProjectedUsedAtReset).Should().BeTrue();
        snapshot.Summary.MostUsedWindow!.UsedPercent.Should().Be(expected);
        if (!double.IsFinite(percent)) window.Pace.Kind.Should().Be(PaceKind.Unknown);
    }

    [TestMethod]
    public void Build_TodayOnlyAndUnpricedModelUsage_PreservesCostAvailability()
    {
        var input = Inputs(Provider() with { Cost = new() { TodayTokens = 12, TodayCostUSD = 1 } });
        input = input with { Settings = new() { ShowCostTotals = true } };
        var snapshot = DashboardSnapshotBuilder.Build(input);
        snapshot.Summary.TotalCostToday.Should().Be(1);
        snapshot.Providers[0].Cost!.TopModels.Should().BeEmpty();
        input = input with { Providers = [Provider() with { Cost = new() { Daily = [new() {
            Date = Today, ModelBreakdowns = [new() { ModelName = "unpriced", TotalTokens = 10 }] }] } }] };
        var cost = DashboardSnapshotBuilder.Build(input).Providers[0].Cost!;
        cost.TopModels.Single().Cost.Should().BeNull();
        cost.AverageDailyCost.Should().Be(0);
        cost.PeakDay.Should().NotBeNull();
        DashboardSnapshotBuilder.Build(input with { Settings = new() { ShowCostTotals = false } }).Summary.CostAvailable.Should().BeFalse();
    }

    [TestMethod]
    public void Build_FarFutureReset_DoesNotOverflowDurationText()
    {
        var window = DashboardSnapshotBuilder.Build(Inputs(Provider() with { Session = Window(10) with {
            ResetAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) } })).Providers[0].Windows[0];
        window.ResetText.Should().Contain("d ").And.NotContain("<1m");
        window.PaceSummary.Should().NotContain("NaN").And.NotContain("Infinity");
    }

    [TestMethod]
    public void Build_ProvidersRemovedAndAddedBetweenSnapshots_HasNoRetainedTotalsOrWindows()
    {
        var first = DashboardSnapshotBuilder.Build(Inputs(Provider() with { Session = Window(99) }, Provider("codex")));
        var second = DashboardSnapshotBuilder.Build(Inputs(Provider("gemini")));
        var empty = DashboardSnapshotBuilder.Build(Inputs());
        first.Summary.WindowsAtLimit.Should().Be(1);
        second.Providers.Select(p => p.Id).Should().Equal("gemini");
        second.Summary.WindowCount.Should().Be(0);
        second.Summary.MostUsedWindow.Should().BeNull();
        empty.Providers.Should().BeEmpty(); empty.Summary.ProviderCount.Should().Be(0);
        first.Providers.Should().HaveCount(2);
    }

    [TestMethod]
    public void Build_DuplicateHistoryKeys_DoesNotAttributeAmbiguousSamplesToEitherWindow()
    {
        var input = Inputs(Provider() with { ExtraWindows = [
            new() { Key = "pool", Window = Window(10) }, new() { Key = "pool", Window = Window(90) }] });
        input = input with { HistorySamples = [new() { Provider = "claude", WindowKey = "extra:pool",
            Timestamp = Now.AddMinutes(-1), ResetsAt = Window(10).ResetAt, UsedPercent = 10 }] };
        var windows = DashboardSnapshotBuilder.Build(input).Providers[0].Windows;
        windows.Should().OnlyContain(w => w.History.SampleCount == 0);
        windows.Select(w => w.UsedPercent).Should().Equal(10, 90);
    }

    [TestMethod]
    public void Build_NegativeCostTotals_ClampsTotalsConsistentlyWithDailySeries()
    {
        var snapshot = DashboardSnapshotBuilder.Build(Inputs(Provider() with { Cost = new() {
            TodayCostUSD = -5, TodayTokens = -5, Last30DaysCostUSD = -10, Last30DaysTokens = -10,
            Daily = [new() { Date = Today, TotalTokens = 1 }] } }));
        snapshot.Summary.TotalCostToday.Should().Be(0); snapshot.Summary.TotalCost30Days.Should().Be(0);
        snapshot.Summary.TotalTokensToday.Should().Be(0); snapshot.Summary.TotalTokens30Days.Should().Be(0);
    }
}
