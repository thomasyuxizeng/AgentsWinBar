using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class ReleaseRetentionRegressionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow.AddHours(-1);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();
    private readonly MockWslService _wsl = new();
    private readonly MockSettingsService _settings = new();
    private readonly MockCacheService _cache = new();
    private readonly UsageHistoryStore _history = new();

    private CodexBarService Service(IGeminiStatusFallback? status = null) => new(_wsl, _cache, _settings,
        new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance, _clock, history: _history,
        geminiStatusFallback: status);
    private static WslResult Reading(double percent, string provider = "claude", bool withStatus = true) => new()
    {
        Success = true,
        Output = System.Text.Json.JsonSerializer.Serialize(new
        {
            provider, usage = new { primary = new { usedPercent = percent } },
            status = withStatus ? new { indicator = "major" } : null
        })
    };
    private void Respond(WslResult result) => _wsl.CommandHandler = (_, _) => Task.FromResult(result);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetUsageAsync_ShimCache_PreservesObservationAndDoesNotRecordAgain(bool cacheSource)
    {
        var at = _clock.Now.UtcDateTime.AddDays(-6);
        Respond(new() { Success = true, Output = $$$$"""
            {"provider":"claude","source":"{{{{(cacheSource ? "cache" : "oauth")}}}}","cached":{{{{(!cacheSource).ToString().ToLowerInvariant()}}}},
             "cacheUpdatedAt":"{{{{at:O}}}}","usage":{"updatedAt":"{{{{at:O}}}}","primary":{"usedPercent":42}}}
            """ });
        var service = Service();
        var first = (await service.GetUsageAsync("claude"))!;
        first.UsageIsStale.Should().BeTrue();
        first.FetchedAt.Should().Be(at);
        first.LastSuccessfulFetch.Should().Be(at);
        _history.Record(first, _clock.Now.UtcDateTime).Should().Be(0);
        _clock.Now += TimeSpan.FromHours(12);
        var again = (await service.GetUsageAsync("claude"))!;
        again.LastSuccessfulFetch.Should().Be(at);
        _clock.Now += TimeSpan.FromDays(1);
        var expired = (await service.GetUsageAsync("claude"))!;
        expired.HasUsage.Should().BeFalse();
        expired.LastSuccessfulFetch.Should().BeNull();
        _history.Samples.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(8, true)]
    [DataRow(0, false)]
    public async Task GetUsageAsync_ExpiredOrUndatedShimCache_IsMissing(int days, bool hasTime)
    {
        var updated = hasTime ? $",\"updatedAt\":\"{_clock.Now.UtcDateTime.AddDays(-days):O}\"" : "";
        Respond(new() { Success = true, Output = $$$$"""{"provider":"claude","cached":true,"usage":{"primary":{"usedPercent":42}{{{{updated}}}}}}""" });
        var data = (await Service().GetUsageAsync("claude"))!;
        data.HasUsage.Should().BeFalse();
        _cache.SetCallCount.Should().Be(0);
        _history.Record(data, _clock.Now.UtcDateTime).Should().Be(0);
    }

    [TestMethod]
    public async Task GetUsageAsync_ShimCacheWithoutUsageTimestamp_UsesOriginalFileTime()
    {
        var at = _clock.Now.UtcDateTime.AddDays(-2);
        Respond(new() { Success = true, Output = $$$$"""{"provider":"claude","cached":true,"cacheUpdatedAt":"{{{{at:O}}}}","usage":{"primary":{"usedPercent":42}}}""" });
        var data = (await Service().GetUsageAsync("claude"))!;
        data.LastSuccessfulFetch.Should().Be(at);
        data.DataUpdatedAt.Should().Be(at);
        data.UsageIsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetUsageAsync_OlderShimCache_DoesNotReplaceNewerRealObservation()
    {
        var service = Service();
        Respond(Reading(70));
        var fresh = (await service.GetUsageAsync("claude"))!;
        Respond(new() { Success = true, Output = $$$$"""{"provider":"claude","cached":true,"usage":{"updatedAt":"{{{{_clock.Now.UtcDateTime.AddDays(-1):O}}}}","primary":{"usedPercent":40}}}""" });
        var result = (await service.GetUsageAsync("claude"))!;
        result.Session!.Percent.Should().Be(70);
        result.LastSuccessfulFetch.Should().Be(fresh.LastSuccessfulFetch);
        _cache.Get("claude")!.Session!.Percent.Should().Be(70);
    }

    [TestMethod]
    public void GetLastKnownUsage_MixedWindowAges_FiltersExpiredWindowsAndUsesOldestRetainedTime()
    {
        var now = _clock.Now.UtcDateTime;
        _history.Restore([
            new() { Provider = "claude", WindowKey = "weekly", UsedPercent = 80, Timestamp = now.AddDays(-10) },
            new() { Provider = "claude", WindowKey = "session", UsedPercent = 40, Timestamp = now.AddDays(-1) },
            new() { Provider = "claude", WindowKey = "extra:retained", UsedPercent = 30, Timestamp = now.AddDays(-6) }
        ], now);
        var recovered = _history.GetLastKnownUsage("claude", now)!;
        recovered.Weekly.Should().BeNull();
        recovered.Session!.Percent.Should().Be(40);
        recovered.LastSuccessfulFetch.Should().Be(now.AddDays(-6));
        var snapshot = DashboardSnapshotBuilder.Build(new() { NowUtc = now, HistorySamples = _history.Samples,
            Providers = [new() { Provider = "claude", Error = "offline" }] });
        snapshot.Providers.Single().Windows.Select(w => w.Key).Should().Equal("session", "extra:retained");
        snapshot.Providers.Single().LastSuccessfulUsageFetchUtc.Should().Be(now.AddDays(-6));
        _history.GetLastKnownUsage("claude", now.AddDays(2))!.ExtraWindows.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetUsageAsync_ConcurrentOlderResult_CannotOverwriteNewerSuccess(bool olderSucceeds)
    {
        var fallback = new Mock<IGeminiStatusFallback>();
        fallback.Setup(s => s.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderStatus { Indicator = ProviderStatusIndicator.Major });
        var service = Service(fallback.Object);
        Respond(Reading(40, "gemini"));
        await service.GetUsageAsync("gemini");
        var started = Signal<bool>();
        var releaseOlder = Signal<WslResult>();
        var newerPublished = Signal<bool>();
        var calls = 0;
        _wsl.CommandHandler = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult(true);
                return releaseOlder.Task;
            }
            return Task.FromResult(Reading(70, "gemini"));
        };
        var older = service.GetUsageAsync("gemini");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        async Task<UsageData?> Newer()
        {
            var data = await service.GetUsageAsync("gemini");
            newerPublished.SetResult(true);
            return data;
        }
        var newer = Newer();
        await newerPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        older.IsCompleted.Should().BeFalse();
        releaseOlder.SetResult(olderSucceeds ? Reading(45, "gemini") : new() { Error = "offline" });
        var results = await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(5));
        results[0]!.Session!.Percent.Should().Be(70);
        results[0]!.UsageIsStale.Should().BeTrue();
        _history.Record(results[0]!, _clock.Now.UtcDateTime).Should().Be(0);
        _cache.Get("gemini")!.Session!.Percent.Should().Be(70);
        _cache.Get("gemini")!.UsageIsStale.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetUsageAsync_ConcurrentReadMergeWrite_StatusFallbackCannotRecycleOldUsageOverNewSuccess()
    {
        var status = new Mock<IGeminiStatusFallback>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderStatus { Indicator = ProviderStatusIndicator.Major });
        var cache = new Mock<ICacheService>();
        var stored = new System.Collections.Concurrent.ConcurrentDictionary<string, UsageData>();
        stored["gemini"] = new() { Provider = "gemini", FetchedAt = _clock.Now.UtcDateTime,
            Session = new() { ReportedPercent = 40 } };
        var readOlder = Signal<bool>();
        var releaseRead = Signal<bool>();
        var newerStarted = Signal<bool>();
        var reads = 0;
        cache.Setup(c => c.GetLastKnown("gemini")).Returns(() =>
        {
            var captured = stored["gemini"];
            if (Interlocked.Increment(ref reads) == 1)
            {
                readOlder.SetResult(true);
                releaseRead.Task.GetAwaiter().GetResult();
            }
            return captured;
        });
        cache.Setup(c => c.Set("gemini", It.IsAny<UsageData>()))
            .Callback((string provider, UsageData data) => stored[provider] = data);
        cache.Setup(c => c.SaveAsync()).Returns(Task.CompletedTask);
        var commands = 0;
        _wsl.CommandHandler = (_, _) => Task.FromResult(Interlocked.Increment(ref commands) == 1
            ? new WslResult { Error = "offline" } : Reading(70, "gemini"));
        var service = new CodexBarService(_wsl, cache.Object, _settings, new MockSampleDataLoader(),
            NullLogger<CodexBarService>.Instance, _clock, geminiStatusFallback: status.Object);
        var older = Task.Run(() => service.GetUsageAsync("gemini"));
        Task<UsageData?>? newer = null;
        try
        {
            await readOlder.Task.WaitAsync(TimeSpan.FromSeconds(5));
            newer = Task.Run(() =>
            {
                newerStarted.SetResult(true);
                return service.GetUsageAsync("gemini");
            });
            await newerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Without an atomic merge, B finishes here while A holds the captured 40% value.
            // With the lock, B must wait until A releases its read/merge/write section.
            await Task.WhenAny(newer, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        finally { releaseRead.TrySetResult(true); }
        await Task.WhenAll(older, newer!).WaitAsync(TimeSpan.FromSeconds(5));
        stored["gemini"].Session!.Percent.Should().Be(70);
        stored["gemini"].UsageIsStale.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetUsageAsync_HistoryOverQuota_RestoresLatest105Percent()
    {
        var now = _clock.Now.UtcDateTime;
        _history.Record(new() { Provider = "claude", Session = new() { ReportedPercent = 99 } }, now.AddMinutes(-20)).Should().Be(1);
        _history.Record(new() { Provider = "claude", Session = new() { ReportedPercent = 105 } }, now).Should().Be(1);
        _history.Get("claude", "session")[^1].UsedPercent.Should().Be(105);
        Respond(new() { Error = "offline" });
        var restored = (await Service().GetUsageAsync("claude"))!;
        restored.Session!.Percent.Should().Be(105);
        var window = new UsageWindow { WindowMinutes = 300, ResetAt = now.AddHours(1) };
        var series = BurndownBuilder.Build(_history.Samples.Select(s => s with { ResetsAt = window.ResetAt }), window, now);
        series.Usage.Select(p => p.UsedPercent).Should().Equal(99, 105);
        DashboardValues.Percent(restored.Session.Percent).Should().Be(100);
        WidgetBarSelector.Pick(restored, now).Single().Window.Percent.Should().Be(105);
    }

    [TestMethod]
    [DataRow(7, true)]
    [DataRow(8, true)]
    [DataRow(30, true)]
    [DataRow(365, true)]
    public async Task GetUsageAsync_AttachedCost_RetainedIndefinitelyWhenCarriedForward(int ageDays, bool retained)
    {
        var now = _clock.Now.UtcDateTime;
        _cache.PreloadCache("claude", new() { Provider = "claude", FetchedAt = now,
            Session = new() { ReportedPercent = 40 }, Cost = new() { TodayCostUSD = 20, FetchedAt = now,
                LastSuccessfulFetch = now.AddDays(-ageDays) } });
        Respond(Reading(70));
        var data = (await Service().GetUsageAsync("claude"))!;
        (data.Cost != null).Should().Be(retained);
        (_cache.Get("claude")!.Cost != null).Should().Be(retained);
    }

    [TestMethod]
    [DataRow(7, true)]
    [DataRow(8, true)]
    [DataRow(30, true)]
    [DataRow(365, true)]
    public void Build_AttachedCost_RetainedIndefinitelyWhileUsageExpires(int ageDays, bool retained)
    {
        var now = _clock.Now.UtcDateTime;
        var snapshot = DashboardSnapshotBuilder.Build(new() { NowUtc = now, Settings = new() { ShowCostTotals = true },
            Providers = [new() { Provider = "claude", FetchedAt = now, Session = new() { ReportedPercent = 70 },
                Cost = new() { TodayCostUSD = 20, FetchedAt = now, LastSuccessfulFetch = now.AddDays(-ageDays) } }] });
        (snapshot.Providers.Single().Cost != null).Should().Be(retained);
        snapshot.Summary.CostAvailable.Should().Be(retained);
        snapshot.Summary.TotalCostToday.Should().Be(retained ? 20 : 0);
        snapshot.Providers.Single().Windows.Single().UsedPercent.Should().Be(70);
    }

    [TestMethod]
    [DataRow(7, true)]
    [DataRow(8, false)]
    public async Task Build_FreshUsageWithFailedStatus_ExposesOriginalStatusAgeAndExpiresIt(int ageDays, bool retained)
    {
        var service = Service();
        Respond(Reading(40));
        var first = (await service.GetUsageAsync("claude"))!;
        _clock.Now += TimeSpan.FromDays(ageDays);
        Respond(Reading(70, withStatus: false));
        var refreshed = (await service.GetUsageAsync("claude"))!;
        // Also project a long-lived VM's incident directly, bypassing service pruning.
        var attached = refreshed with { ServiceStatus = first.ServiceStatus, StatusIsStale = true,
            LastSuccessfulStatusFetch = first.LastSuccessfulStatusFetch };
        foreach (var data in new[] { refreshed, attached })
        {
            var snapshot = DashboardSnapshotBuilder.Build(new() { Providers = [data], NowUtc = _clock.Now.UtcDateTime });
            var provider = snapshot.Providers.Single();
            provider.StatusIsStale.Should().Be(retained);
            provider.LastSuccessfulStatusFetchUtc.Should().Be(retained ? first.LastSuccessfulStatusFetch : null);
            (provider.ServiceStatus != null).Should().Be(retained);
            provider.LastSuccessfulUsageFetchUtc.Should().Be(_clock.Now.UtcDateTime);
            provider.FetchedAtUtc.Should().Be(_clock.Now.UtcDateTime);
            snapshot.Summary.IncidentCount.Should().Be(retained ? 1 : 0);
        }
    }
}
