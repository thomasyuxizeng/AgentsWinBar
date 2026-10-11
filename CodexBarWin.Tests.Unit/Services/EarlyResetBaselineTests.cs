using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class EarlyResetBaselineTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
    private readonly List<EarlyResetStore> _stores = [];
    private string _directory = null!;

    [TestInitialize]
    public void Setup() => _directory = Path.Combine(Path.GetTempPath(), "cbw-baseline-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var store in _stores) await store.FlushAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private EarlyResetStore Store()
    {
        var store = new EarlyResetStore(null, NullLogger<EarlyResetStore>.Instance, new FixedClock(), Path.Combine(_directory, "early-resets.json"));
        _stores.Add(store);
        return store;
    }

    private static UsageData Data(double percent, DateTime at) => new()
    {
        Provider = "codex", Plan = "plus", UsageFetchOutcome = FetchOutcome.Success, DataUpdatedAt = at, FetchedAt = Now,
        Session = new() { ReportedPercent = percent, Limit = 100, WindowMinutes = 300, ResetAt = Now.AddHours(3) }
    };

    [TestMethod]
    [DataRow("replay")]
    [DataRow("failed")]
    [DataRow("stale")]
    [DataRow("old")]
    [DataRow("same-time")]
    [DataRow("future")]
    public async Task Observe_InvalidInterveningReadingNeitherReplacesNorInvalidatesPersistedFreshBaseline(string kind)
    {
        var store = Store();
        store.Observe(Data(80, Now.AddMinutes(-10)), Now).Should().BeEmpty();
        var invalid = Data(20, Now.AddMinutes(-5));
        invalid = kind switch
        {
            "replay" => invalid with { UsageIsCached = true },
            "failed" => invalid with { UsageFetchOutcome = FetchOutcome.Failed },
            "stale" => invalid with { UsageIsStale = true },
            "old" => invalid with { DataUpdatedAt = Now.AddMinutes(-11) },
            "same-time" => invalid with { DataUpdatedAt = Now.AddMinutes(-10) },
            _ => invalid with { DataUpdatedAt = Now.AddMinutes(6) }
        };
        store.Observe(invalid, Now).Should().BeEmpty();
        await store.FlushAsync();
        var restarted = Store();
        await restarted.LoadAsync();
        var current = Data(5, Now) with { Session = Data(5, Now).Session! with { ResetAt = Now.AddDays(7) } };
        var events = restarted.Observe(current, Now);
        events.Should().ContainSingle();
        events.Single().PreviousPercent.Should().Be(80);
        restarted.Observe(current, Now).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("plan")]
    [DataRow("limit")]
    [DataRow("minutes")]
    public void Observe_MetadataChangeStartsNewBaseline_SubsequentDropUsesNewMetadata(string change)
    {
        var store = Store();
        store.Observe(Data(80, Now.AddMinutes(-10)), Now).Should().BeEmpty();
        var next = Data(50, Now.AddMinutes(-5));
        next = change switch
        {
            "plan" => next with { Plan = "pro" },
            "limit" => next with { Session = next.Session! with { Limit = 200 } },
            _ => next with { Session = next.Session! with { WindowMinutes = 600 } }
        };
        store.Observe(next, Now).Should().BeEmpty();
        var reset = next with { DataUpdatedAt = Now, Session = next.Session! with { ReportedPercent = 0 } };
        store.Observe(reset, Now).Should().ContainSingle(e => e.PreviousPercent == 50);
    }

    [TestMethod]
    public void Observe_PerWindowBaselineSurvivesOtherWindowsOnlyFreshSuccess()
    {
        var store = Store();
        store.Observe(Data(80, Now.AddMinutes(-10)), Now).Should().BeEmpty();
        store.Observe(Data(20, Now.AddMinutes(-5)) with { Session = null,
            Weekly = new() { ReportedPercent = 70, ResetAt = Now.AddDays(3) } }, Now).Should().BeEmpty();
        store.Observe(Data(0, Now), Now).Should().ContainSingle(e => e.WindowKey == "session" && e.PreviousPercent == 80);
    }

    [TestMethod]
    public async Task Observe_BaselineOlderThan24HoursNeverCompared_AfterRestartOrWhileRunning()
    {
        var store = Store();
        store.Observe(Data(80, Now.AddHours(-25)), Now.AddHours(-25)).Should().BeEmpty();
        await store.FlushAsync();
        var restarted = Store();
        await restarted.LoadAsync();
        store.Observe(Data(0, Now), Now).Should().BeEmpty();
        restarted.Observe(Data(0, Now), Now).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Observe_AmbiguousFreshExtraKeysBreakContinuity_EvenAcrossRestart(bool hasId)
    {
        UsageData ExtraData(DateTime at, params double[] values) => Data(0, at) with
        {
            Session = null,
            ExtraWindows = values.Select(p => new ExtraUsageWindow { Key = hasId ? "model" : "", Title = "Model",
                Window = new() { ReportedPercent = p, ResetAt = Now.AddHours(3) } }).ToArray()
        };
        var store = Store();
        store.Observe(ExtraData(Now.AddMinutes(-10), 80), Now).Should().BeEmpty();
        store.Observe(ExtraData(Now.AddMinutes(-5), 80, 20), Now).Should().BeEmpty();
        await store.FlushAsync();
        var restarted = Store();
        await restarted.LoadAsync();
        restarted.Observe(ExtraData(Now, 0), Now).Should().BeEmpty();
    }
}
