using AwesomeAssertions;
using System.Text.Json;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class EarlyResetBaselineTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private sealed class FixedClock : TimeProvider
    {
        public DateTime UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
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

    private EarlyResetStore Store(TimeProvider? clock = null)
    {
        var store = new EarlyResetStore(null, NullLogger<EarlyResetStore>.Instance, clock ?? new FixedClock(), Path.Combine(_directory, "early-resets.json"));
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

    private static UsageData Extras(DateTime at, params (string Key, double Percent)[] windows) => Data(0, at) with
    {
        Session = null,
        ExtraWindows = windows.Select(w => new ExtraUsageWindow
        {
            Key = w.Key, Title = w.Key,
            Window = new() { ReportedPercent = w.Percent, ResetAt = Now.AddHours(3) }
        }).ToArray()
    };

    private static async Task<EarlyResetStoreFile> Saved(EarlyResetStore store)
    {
        await store.FlushAsync();
        return JsonSerializer.Deserialize(await File.ReadAllTextAsync(store.FilePath),
            AppJsonSerializerContext.Default.EarlyResetStoreFile)!;
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Observe_LateUniqueAtOrBeforeAmbiguityCannotRecreateBaseline(bool restart, bool sameTime)
    {
        var store = Store();
        store.Observe(Extras(Now.AddMinutes(-10), ("model", 80)), Now).Should().BeEmpty();
        store.Observe(Extras(Now.AddMinutes(-5), ("model", 80), ("MODEL", 20)), Now).Should().BeEmpty();
        var saved = await Saved(store);
        saved.Baselines.Should().BeEmpty();
        saved.InvalidatedAtUtc.Should().ContainKey("codex|extra:model")
            .WhoseValue.Should().Be(Now.AddMinutes(-5));
        if (restart)
        {
            store = Store();
            await store.LoadAsync();
        }
        store.Observe(Extras(Now.AddMinutes(sameTime ? -5 : -7), ("model", 80)), Now).Should().BeEmpty();
        (await Saved(store)).Baselines.Should().BeEmpty();
        store.Observe(Extras(Now, ("model", 0)), Now).Should().BeEmpty();
        (await Saved(store)).InvalidatedAtUtc.Should().BeEmpty();
        store.Observe(Extras(Now.AddMinutes(1), ("model", 80)), Now.AddMinutes(1)).Should().BeEmpty();
        store.Observe(Extras(Now.AddMinutes(2), ("model", 0)), Now.AddMinutes(2)).Should().ContainSingle();
    }

    [TestMethod]
    public async Task Observe_AmbiguityWithoutBaselineStillGuardsAgainstLateUniqueReading()
    {
        var store = Store();
        store.Observe(Extras(Now.AddMinutes(-5), ("model", 80), ("model", 20)), Now).Should().BeEmpty();
        await store.FlushAsync();
        var restarted = Store();
        await restarted.LoadAsync();
        restarted.Observe(Extras(Now.AddMinutes(-7), ("model", 80)), Now).Should().BeEmpty();
        restarted.Observe(Extras(Now, ("model", 0)), Now).Should().BeEmpty();
    }

    [TestMethod]
    public void Observe_OlderAmbiguityDoesNotInvalidateNewerUniqueBaseline()
    {
        var store = Store();
        store.Observe(Extras(Now.AddMinutes(-5), ("model", 80)), Now).Should().BeEmpty();
        store.Observe(Extras(Now.AddMinutes(-7), ("model", 80), ("model", 20)), Now).Should().BeEmpty();
        store.Observe(Extras(Now, ("model", 0)), Now).Should().ContainSingle();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObserveOrSave_BaselinesBeyond24HoursAreRemovedFromPersistence(bool observe)
    {
        var clock = new FixedClock();
        var store = Store(clock);
        store.Observe(Data(80, Now), Now).Should().BeEmpty();
        (await Saved(store)).Baselines.Should().ContainSingle();
        clock.UtcNow = Now.AddHours(24);
        await store.SaveAsync();
        (await Saved(store)).Baselines.Should().ContainSingle();
        clock.UtcNow = Now.AddHours(24).AddTicks(1);
        if (observe)
            store.Observe(Data(0, clock.UtcNow) with { UsageIsCached = true }, clock.UtcNow).Should().BeEmpty();
        else
            await store.SaveAsync();
        (await Saved(store)).Baselines.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Observe_BaselineKeyCapDropsOldestAndRetainsNewestAcrossRestart()
    {
        var store = Store();
        store.Observe(Extras(Now.AddMinutes(-10), ("oldest", 80)), Now).Should().BeEmpty();
        var windows = Enumerable.Range(1, EarlyResetStore.MaxBaselines)
            .Select(i => ($"model{i:D4}", 80d)).ToArray();
        store.Observe(Extras(Now.AddMinutes(-5), windows), Now).Should().BeEmpty();
        var saved = await Saved(store);
        saved.Baselines.Should().HaveCount(EarlyResetStore.MaxBaselines);
        saved.Baselines.Should().NotContain(b => b.WindowKey == "extra:oldest");
        var restarted = Store();
        await restarted.LoadAsync();
        restarted.Observe(Extras(Now, ("oldest", 0)), Now).Should().BeEmpty();
        restarted.Observe(Extras(Now, ("model0200", 0)), Now).Should().ContainSingle();
        (await Saved(restarted)).Baselines.Should().HaveCount(EarlyResetStore.MaxBaselines);
    }
}
