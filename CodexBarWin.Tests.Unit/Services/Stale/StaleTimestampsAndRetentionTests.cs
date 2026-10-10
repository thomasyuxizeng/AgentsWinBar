using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.ViewModels;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services.Stale;

[TestClass]
public class StaleTimestampsAndRetentionTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset CurrentTime { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => CurrentTime;
    }

    private string _directory = null!;
    private TestClock _clock = null!;
    private MockSettingsService _settings = null!;
    private UsageHistoryStore _history = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-stale-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _clock = new();
        _settings = new();
        _history = new();
        _settings.SetProviders([new() { Id = "claude", IsEnabled = true }]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private CacheService CreateCache(string? path = null) =>
        new(NullLogger<CacheService>.Instance, _settings, path ?? Path.Combine(_directory, "cache.json"));
    private CostCacheStore CreateCosts(string? path = null) =>
        new(_settings, NullLogger<CostCacheStore>.Instance, _clock, path ?? Path.Combine(_directory, "cost-cache.json"));

    private CodexBarService CreateCodexBarService(MockWslService wsl, ICacheService? cache = null, ICostCacheStore? costs = null) =>
        new(wsl, cache ?? CreateCache(), _settings, new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance, _clock,
            userProfile: () => @"C:\Users\Test", directoryExists: _ => true, costStore: costs ?? CreateCosts(), history: _history);

    private MainViewModel CreateMainViewModelWithHistory(UsageHistoryStore history, ICacheService cache)
    {
        var usage = new Mock<ICodexBarService>();
        var toast = new Mock<IToastNotifier>();
        var memo = new Mock<ISetupMemoService>();
        var session = new Mock<ISessionStateSource>();
        var activity = new ActivityLog();
        var persistence = new UsageHistoryPersistence(history, _settings, NullLogger<UsageHistoryPersistence>.Instance, _clock);
        return new(usage.Object, _settings, new UsageAlertEvaluator(), toast.Object, NullLogger<MainViewModel>.Instance,
            history, persistence, activity, session.Object, cache, memo.Object);
    }

    [TestMethod]
    public void SnapshotBuilder_WhenTimestampIsInFuture_ClampsStaleAgeToZero()
    {
        var now = DateTime.UtcNow;
        var futureTime = now.AddMinutes(5);
        var provider = new UsageData
        {
            Provider = "claude",
            UsageIsStale = true,
            Session = new UsageWindow { ReportedPercent = 50 },
            LastSuccessfulFetch = futureTime
        };

        var snapshot = DashboardSnapshotBuilder.Build(new()
        {
            Providers = [provider],
            NowUtc = now
        });

        snapshot.Providers.Single().StaleAge.Should().Be(TimeSpan.Zero);
    }

    [TestMethod]
    public void StaleCaption_WhenTimestampIsInFuture_ReturnsJustNow()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var future = now.AddMinutes(10);
        StaleCaption.Text(true, future, now).Should().Be("Stale · last updated just now");
    }

    [TestMethod]
    public void StaleCaption_UtcVsLocalVsUnspecified_ProducesConsistentCaptions()
    {
        var nowUtc = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var baseUtc = nowUtc.AddHours(-2);

        var utcTime = DateTime.SpecifyKind(baseUtc, DateTimeKind.Utc);
        var localTime = baseUtc.ToLocalTime();
        var unspecifiedTime = DateTime.SpecifyKind(baseUtc, DateTimeKind.Unspecified);

        var captionUtc = StaleCaption.Text(true, utcTime, nowUtc);
        var captionLocal = StaleCaption.Text(true, localTime, nowUtc);
        var captionUnspecified = StaleCaption.Text(true, unspecifiedTime, nowUtc);

        captionUtc.Should().Be("Stale · last updated 2h ago");
        captionLocal.Should().Be("Stale · last updated 2h ago");
        captionUnspecified.Should().Be("Stale · last updated 2h ago");
    }

    [TestMethod]
    public async Task CacheService_GetLastKnown_WhenTimestampIsInFuture_ReturnsNull()
    {
        var cache = CreateCache();
        var future = DateTime.UtcNow.AddMinutes(5);
        cache.Set("claude", new UsageData
        {
            Provider = "claude",
            FetchedAt = future,
            Session = new UsageWindow { ReportedPercent = 30 }
        });

        // GetLastKnown filters out records where Utc(FetchedAt) > DateTime.UtcNow
        cache.GetLastKnown("claude").Should().BeNull();
    }

    [TestMethod]
    public async Task CostCacheStore_ToleratesUpToFiveMinutesFutureClockSkew()
    {
        var store = CreateCosts();
        var fourMinutesFuture = _clock.CurrentTime.UtcDateTime.AddMinutes(4);
        var sixMinutesFuture = _clock.CurrentTime.UtcDateTime.AddMinutes(6);

        await store.SaveAsync(new Dictionary<string, CostData>
        {
            ["claude"] = new() { Provider = "claude", FetchedAt = fourMinutesFuture, TodayCostUSD = 10, FetchOutcome = FetchOutcome.Success },
            ["codex"] = new() { Provider = "codex", FetchedAt = sixMinutesFuture, TodayCostUSD = 10, FetchOutcome = FetchOutcome.Success }
        });

        var loaded = await store.LoadAsync();
        // 4 minutes in future is within 5 minute tolerance
        loaded.ContainsKey("claude").Should().BeTrue();
        // 6 minutes in future exceeds tolerance and is dropped
        loaded.ContainsKey("codex").Should().BeFalse();
    }

    [TestMethod]
    [DataRow(10, "10d ago")]
    [DataRow(30, "30d ago")]
    [DataRow(180, "180d ago")]
    [DataRow(365, "365d ago")]
    public async Task VeryOldData_CacheServiceAndCostStoreExpireAfterSevenDays(int daysOld, string expectedAge)
    {
        var now = DateTime.UtcNow;
        var oldDate = now.AddDays(-daysOld);

        // 1. CacheService persists and restores very old usage
        var cachePath = Path.Combine(_directory, $"cache-{daysOld}.json");
        var cacheData = new CacheData
        {
            Timestamp = oldDate,
            Items = new()
            {
                ["claude"] = new()
                {
                    Provider = "claude",
                    FetchedAt = oldDate,
                    Session = new UsageWindow { ReportedPercent = 40 }
                }
            }
        };
        await File.WriteAllTextAsync(cachePath, System.Text.Json.JsonSerializer.Serialize(cacheData, AppJsonSerializerContext.Default.CacheData));

        var cache = CreateCache(cachePath);
        await cache.LoadAsync();
        // Normal Get returns null because age > Expiry
        cache.Get("claude").Should().BeNull();
        // GetLastKnown returns it even though it's weeks/months old!
        var restoredUsage = cache.GetLastKnown("claude");
        restoredUsage.Should().BeNull();

        // 2. CostCacheStore persists and restores very old costs
        var costPath = Path.Combine(_directory, $"cost-{daysOld}.json");
        var store = CreateCosts(costPath);
        await store.SaveAsync(new Dictionary<string, CostData>
        {
            ["claude"] = new()
            {
                Provider = "claude",
                FetchedAt = oldDate,
                Last30DaysCostUSD = 150m,
                FetchOutcome = FetchOutcome.Success
            }
        });

        var loadedCosts = await store.LoadAsync();
        loadedCosts.ContainsKey("claude").Should().BeFalse();

        // 3. StaleCaption formats the age string accurately
        StaleCaption.Text(true, oldDate, now).Should().Be($"Stale · last updated {expectedAge}");
    }

    [TestMethod]
    public void VeryOldData_UsageHistoryStorePrunesSamplesOlderThanFourteenDays()
    {
        var now = DateTime.UtcNow;
        var tenDaysAgo = now.AddDays(-10);
        var fifteenDaysAgo = now.AddDays(-15);

        // Add 10-day-old sample (should succeed)
        var added10 = _history.Add(new()
        {
            Provider = "claude",
            WindowKey = "session",
            UsedPercent = 30,
            Timestamp = tenDaysAgo
        }, now);
        added10.Should().BeTrue();

        // Add 15-day-old sample (should be rejected as too old)
        var added15 = _history.Add(new()
        {
            Provider = "claude",
            WindowKey = "session",
            UsedPercent = 20,
            Timestamp = fifteenDaysAgo
        }, now);
        added15.Should().BeFalse();

        // Recovering last known usage from history gets the 10-day-old reading
        var restored = _history.GetLastKnownUsage("claude");
        restored.Should().NotBeNull();
        restored!.Session!.Percent.Should().Be(30);
    }

    [TestMethod]
    public void UsageData_IsStale_WhenFetchedAtIsLocalKind_ShouldNormalizeToUtcBeforeComparing()
    {
        var localTime = DateTime.Now;

        var data = new UsageData
        {
            Provider = "claude",
            FetchedAt = localTime,
            UsageIsStale = false,
            StatusIsStale = false
        };

        // If properly normalized to UTC before subtraction, age would be ~0 and IsStale would be false.
        data.IsStale.Should().BeFalse();
    }

    [TestMethod]
    public async Task UsageCache_BoundarySevenDays_KeepsAt6d23hAndDropsAt7d1m()
    {
        var now = DateTime.UtcNow;
        var age6d23h = TimeSpan.FromDays(7) - TimeSpan.FromHours(1);
        var age7d1m = TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
        var ageExact7d = TimeSpan.FromDays(7);

        // 6d 23h: kept
        var cachePath1 = Path.Combine(_directory, "cache-boundary-kept.json");
        var cache1 = CreateCache(cachePath1);
        var timeKept = now - age6d23h;
        cache1.Set("claude", new UsageData
        {
            Provider = "claude",
            FetchedAt = timeKept,
            LastSuccessfulFetch = timeKept,
            Session = new() { ReportedPercent = 42 }
        });
        cache1.GetLastKnown("claude").Should().NotBeNull();
        await cache1.SaveAsync();
        var reloaded1 = CreateCache(cachePath1);
        await reloaded1.LoadAsync();
        reloaded1.GetLastKnown("claude").Should().NotBeNull();

        // Exact 7d: kept (<= 7 days)
        var cacheExact = CreateCache();
        var timeExact = DateTime.UtcNow.AddSeconds(1) - ageExact7d;
        cacheExact.Set("claude", new UsageData
        {
            Provider = "claude",
            FetchedAt = timeExact,
            LastSuccessfulFetch = timeExact,
            Session = new() { ReportedPercent = 42 }
        });
        cacheExact.GetLastKnown("claude").Should().NotBeNull();

        // 7d 1m: dropped
        var cachePath2 = Path.Combine(_directory, "cache-boundary-dropped.json");
        var cache2 = CreateCache(cachePath2);
        var timeDropped = now - age7d1m;
        cache2.Set("claude", new UsageData
        {
            Provider = "claude",
            FetchedAt = timeDropped,
            LastSuccessfulFetch = timeDropped,
            Session = new() { ReportedPercent = 42 }
        });
        cache2.GetLastKnown("claude").Should().BeNull();
        await cache2.SaveAsync();
        var reloaded2 = CreateCache(cachePath2);
        await reloaded2.LoadAsync();
        reloaded2.GetLastKnown("claude").Should().BeNull();
    }

    [TestMethod]
    public async Task CostStore_BoundarySevenDays_KeepsAt6d23hAndDropsAt7d1m()
    {
        var now = _clock.CurrentTime.UtcDateTime;
        var age6d23h = TimeSpan.FromDays(7) - TimeSpan.FromHours(1);
        var age7d1m = TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
        var ageExact7d = TimeSpan.FromDays(7);

        // 6d 23h: kept
        var store1 = CreateCosts(Path.Combine(_directory, "cost-kept.json"));
        await store1.SaveAsync(new Dictionary<string, CostData>
        {
            ["claude"] = new()
            {
                Provider = "claude",
                FetchedAt = now - age6d23h,
                LastSuccessfulFetch = now - age6d23h,
                Last30DaysCostUSD = 25m,
                FetchOutcome = FetchOutcome.Success
            }
        });
        var loaded1 = await store1.LoadAsync();
        loaded1.ContainsKey("claude").Should().BeTrue();

        // Exact 7d: kept
        var storeExact = CreateCosts(Path.Combine(_directory, "cost-exact.json"));
        await storeExact.SaveAsync(new Dictionary<string, CostData>
        {
            ["claude"] = new()
            {
                Provider = "claude",
                FetchedAt = now - ageExact7d,
                LastSuccessfulFetch = now - ageExact7d,
                Last30DaysCostUSD = 25m,
                FetchOutcome = FetchOutcome.Success
            }
        });
        var loadedExact = await storeExact.LoadAsync();
        loadedExact.ContainsKey("claude").Should().BeTrue();

        // 7d 1m: dropped
        var store2 = CreateCosts(Path.Combine(_directory, "cost-dropped.json"));
        await store2.SaveAsync(new Dictionary<string, CostData>
        {
            ["claude"] = new()
            {
                Provider = "claude",
                FetchedAt = now - age7d1m,
                LastSuccessfulFetch = now - age7d1m,
                Last30DaysCostUSD = 25m,
                FetchOutcome = FetchOutcome.Success
            }
        });
        var loaded2 = await store2.LoadAsync();
        loaded2.ContainsKey("claude").Should().BeFalse();
    }

    [TestMethod]
    public async Task LastGoodCost_BoundarySevenDays_KeepsAt6d23hAndDropsAt7d1m()
    {
        var wsl = new MockWslService();
        var service = CreateCodexBarService(wsl);

        // Initial successful cost fetch
        wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","sessionCostUSD":5.0,"sessionTokens":500,"last30DaysCostUSD":50.0,"last30DaysTokens":5000}"""
        });
        var initial = await service.GetCostAsync("claude");
        initial.Should().NotBeNull();
        initial!.IsStale.Should().BeFalse();

        // Subsequent fetch fails
        wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = false, Error = "timeout" });

        // At 6d 23h: kept as stale
        _clock.CurrentTime += TimeSpan.FromDays(7) - TimeSpan.FromHours(1);
        var costKept = await service.GetCostAsync("claude", forceRefresh: true);
        costKept.Should().NotBeNull();
        costKept!.IsStale.Should().BeTrue();

        // At 7d 1m (advance by 1h 2m from 6d23h): dropped (returns null)
        _clock.CurrentTime += TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2);
        var costDropped = await service.GetCostAsync("claude", forceRefresh: true);
        costDropped.Should().BeNull();
    }

    [TestMethod]
    public async Task LastGoodCost_ExactSevenDays_IsKept()
    {
        var wsl = new MockWslService();
        var service = CreateCodexBarService(wsl);

        wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","sessionCostUSD":5.0,"sessionTokens":500,"last30DaysCostUSD":50.0,"last30DaysTokens":5000}"""
        });
        await service.GetCostAsync("claude");

        wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = false, Error = "timeout" });
        _clock.CurrentTime += TimeSpan.FromDays(7);
        var costExact = await service.GetCostAsync("claude", forceRefresh: true);
        costExact.Should().NotBeNull();
        costExact!.IsStale.Should().BeTrue();
    }

    [TestMethod]
    public void HistoryFallback_BoundarySevenDays_KeepsAt6d23hAndDropsAt7d1m()
    {
        var age6d23h = TimeSpan.FromDays(7) - TimeSpan.FromHours(1);
        var age7d1m = TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
        var ageExact7d = TimeSpan.FromDays(7);

        // Case 1: 6d 23h kept
        var history1 = new UsageHistoryStore();
        var timeKept = DateTime.UtcNow - age6d23h;
        history1.Add(new() { Provider = "claude", WindowKey = "session", UsedPercent = 55, Timestamp = timeKept }, DateTime.UtcNow);
        var emptyCache1 = new Mock<ICacheService>();
        var vm1 = CreateMainViewModelWithHistory(history1, emptyCache1.Object);
        vm1.RestoreLastKnownUsage();
        var claudeKept = vm1.Providers.First(p => p.Provider == "claude");
        claudeKept.HasUsage.Should().BeTrue();
        claudeKept.Session!.Percent.Should().Be(55);
        claudeKept.UsageIsStale.Should().BeTrue();

        // Case 2: Exact 7d kept
        var historyExact = new UsageHistoryStore();
        var timeExact = DateTime.UtcNow.AddSeconds(1) - ageExact7d;
        historyExact.Add(new() { Provider = "claude", WindowKey = "session", UsedPercent = 55, Timestamp = timeExact }, DateTime.UtcNow);
        var emptyCacheExact = new Mock<ICacheService>();
        var vmExact = CreateMainViewModelWithHistory(historyExact, emptyCacheExact.Object);
        vmExact.RestoreLastKnownUsage();
        var claudeExact = vmExact.Providers.First(p => p.Provider == "claude");
        claudeExact.HasUsage.Should().BeTrue();

        // Case 3: 7d 1m dropped
        var history2 = new UsageHistoryStore();
        var timeDropped = DateTime.UtcNow - age7d1m;
        history2.Add(new() { Provider = "claude", WindowKey = "session", UsedPercent = 55, Timestamp = timeDropped }, DateTime.UtcNow);
        var emptyCache2 = new Mock<ICacheService>();
        var vm2 = CreateMainViewModelWithHistory(history2, emptyCache2.Object);
        vm2.RestoreLastKnownUsage();
        vm2.Providers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Regression_FreshDataUnaffectedAcrossAllCaches()

    {

        var now = DateTime.UtcNow;

        var freshTime = now.AddMinutes(-1);



        // 1. Usage Cache

        var cache = CreateCache();

        var freshUsage = new UsageData

        {

            Provider = "claude",

            FetchedAt = freshTime,

            LastSuccessfulFetch = freshTime,

            Session = new() { ReportedPercent = 25 }

        };

        cache.Set("claude", freshUsage);

        cache.Get("claude").Should().NotBeNull();

        cache.Get("claude")!.UsageIsStale.Should().BeFalse();

        cache.GetLastKnown("claude").Should().NotBeNull();



        // 2. Cost Store

        var costStore = CreateCosts();

        await costStore.SaveAsync(new Dictionary<string, CostData>

        {

            ["claude"] = new()

            {

                Provider = "claude",

                FetchedAt = freshTime,

                LastSuccessfulFetch = freshTime,

                TodayCostUSD = 1.5m,

                FetchOutcome = FetchOutcome.Success

            }

        });

        var loadedCosts = await costStore.LoadAsync();

        loadedCosts.ContainsKey("claude").Should().BeTrue();

        loadedCosts["claude"].IsStale.Should().BeFalse();



        // 3. CodexBarService Last-Good Cost

        var wsl = new MockWslService();

        var service = CreateCodexBarService(wsl, cache, costStore);

        wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult

        {

            Success = true,

            Output = """{"provider":"claude","sessionCostUSD":1.5,"sessionTokens":100,"last30DaysCostUSD":10.0,"last30DaysTokens":1000}"""

        });

        var cost = await service.GetCostAsync("claude");

        cost.Should().NotBeNull();

        cost!.IsStale.Should().BeFalse();

        cost.TodayCostUSD.Should().Be(1.5m);



        // 4. MainViewModel History Fallback

        var history = new UsageHistoryStore();

        history.Add(new() { Provider = "claude", WindowKey = "session", UsedPercent = 25, Timestamp = freshTime }, now);

        var vm = CreateMainViewModelWithHistory(history, new Mock<ICacheService>().Object);

        vm.RestoreLastKnownUsage();

        var provider = vm.Providers.First(p => p.Provider == "claude");

        provider.HasUsage.Should().BeTrue();

        provider.Session!.Percent.Should().Be(25);

    }

}



