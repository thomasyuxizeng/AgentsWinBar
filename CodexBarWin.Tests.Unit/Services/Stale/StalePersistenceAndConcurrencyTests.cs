using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexBarWin.Tests.Unit.Services.Stale;

[TestClass]
public class StalePersistenceAndConcurrencyTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset CurrentTime { get; set; } = DateTimeOffset.UtcNow.AddHours(-1);
        public override DateTimeOffset GetUtcNow() => CurrentTime;
    }

    private string _directory = null!;
    private TestClock _clock = null!;
    private MockSettingsService _settings = null!;
    private MockWslService _wsl = null!;
    private UsageHistoryStore _history = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-stale-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _clock = new();
        _settings = new();
        _wsl = new();
        _history = new();
        _settings.SetProviders([
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "codex", IsEnabled = true }
        ]);
        _settings.Settings.ShowCostTotals = true;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.GetFiles(_directory))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_directory, recursive: true);
        }
    }

    private CacheService CreateCache(string? path = null) =>
        new(NullLogger<CacheService>.Instance, _settings, path ?? Path.Combine(_directory, "cache.json"));
    private CostCacheStore CreateCosts(string? path = null) =>
        new(_settings, NullLogger<CostCacheStore>.Instance, _clock, path ?? Path.Combine(_directory, "cost-cache.json"));
    private CodexBarService CreateService(ICacheService? cache = null, ICostCacheStore? costs = null) =>
        new(_wsl, cache ?? CreateCache(), _settings, new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance, _clock,
            userProfile: () => "C:\\Users\\Test", directoryExists: _ => true, costStore: costs ?? CreateCosts(), history: _history);

    [TestMethod]
    public async Task Persistence_SaveThenNewInstanceLoadsAndServesOnFailure()
    {
        var cachePath = Path.Combine(_directory, "shared-cache.json");
        var costPath = Path.Combine(_directory, "shared-cost.json");

        // Service 1: fetches usage and cost successfully, persisting them
        var cache1 = CreateCache(cachePath);
        var costs1 = CreateCosts(costPath);
        var service1 = CreateService(cache1, costs1);

        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":52}},"status":{"indicator":"none"}}"""
        });
        await service1.GetUsageAsync("claude");

        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","sessionCostUSD":4.2,"sessionTokens":420,"last30DaysCostUSD":42.0,"last30DaysTokens":4200}"""
        });
        await service1.GetCostAsync("claude", forceRefresh: true);

        // Advance time
        _clock.CurrentTime += TimeSpan.FromDays(2);

        // Service 2: completely new instance with its own cache and cost stores reading from disk
        var cache2 = CreateCache(cachePath);
        await cache2.LoadAsync();
        var costs2 = CreateCosts(costPath);
        var service2 = CreateService(cache2, costs2);

        // Both fetches fail
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = false, Error = "Offline" });

        var restoredUsage = await service2.GetUsageAsync("claude");
        restoredUsage!.Session!.Percent.Should().Be(52);
        restoredUsage.UsageIsStale.Should().BeTrue();

        var restoredCost = await service2.GetCostAsync("claude", forceRefresh: true);
        restoredCost!.TodayCostUSD.Should().Be(4.2m);
        restoredCost.Last30DaysCostUSD.Should().Be(42.0m);
        restoredCost.IsStale.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{")]
    [DataRow("{\"items\":")]
    [DataRow("invalid json format")]
    public async Task Persistence_CorruptedOrEmptyCacheFile_DoesNotThrowAndYieldsEmpty(string badJson)
    {
        var cachePath = Path.Combine(_directory, "corrupt-cache.json");
        await File.WriteAllTextAsync(cachePath, badJson);

        var cache = CreateCache(cachePath);
        var act = async () => await cache.LoadAsync();
        await act.Should().NotThrowAsync();
        cache.GetAll().Should().BeEmpty();
        cache.GetLastKnown("claude").Should().BeNull();
    }

    [TestMethod]
    public async Task Persistence_OversizedCacheFile_LoadsSuccessfully()
    {
        var cachePath = Path.Combine(_directory, "oversized-cache.json");
        var now = DateTime.UtcNow.ToString("O");
        // Create a large file (> 2 MB) with lots of dummy extra entries
        var extraItems = string.Join(",", Enumerable.Range(0, 1000).Select(i =>
            $"\"extra_{i}\":{{\"provider\":\"unknown_{i}\",\"fetchedAt\":\"{now}\"}}"));
        var json = $"{{\"timestamp\":\"{now}\",\"items\":{{\"claude\":{{\"provider\":\"claude\",\"fetchedAt\":\"{now}\",\"session\":{{\"reportedPercent\":48}}}},{extraItems}}}}}";
        await File.WriteAllTextAsync(cachePath, json);

        var cache = CreateCache(cachePath);
        await cache.LoadAsync();
        cache.GetLastKnown("claude")!.Session!.Percent.Should().Be(48);
    }

    [TestMethod]
    public async Task Persistence_OldSchemaCacheFileWithoutOutcome_LoadsSuccessfully()
    {
        var cachePath = Path.Combine(_directory, "old-schema-cache.json");
        var now = DateTime.UtcNow.ToString("O");
        // Old schema before FetchOutcome enum was introduced
        var json = $$"""
        {
          "timestamp": "{{now}}",
          "items": {
            "claude": {
              "provider": "claude",
              "fetchedAt": "{{now}}",
              "session": { "reportedPercent": 36 }
            }
          }
        }
        """;
        await File.WriteAllTextAsync(cachePath, json);

        var cache = CreateCache(cachePath);
        await cache.LoadAsync();
        cache.GetLastKnown("claude")!.Session!.Percent.Should().Be(36);
    }

    [TestMethod]
    public async Task Persistence_MissingDirectory_CreatesDirectoryOnSave()
    {
        var subDir = Path.Combine(_directory, "nested", "deep", "dir");
        var cachePath = Path.Combine(subDir, "cache.json");
        Directory.Exists(subDir).Should().BeFalse();

        var cache = CreateCache(cachePath);
        cache.Set("claude", new UsageData
        {
            Provider = "claude",
            FetchedAt = DateTime.UtcNow,
            Session = new UsageWindow { ReportedPercent = 70 }
        });
        await cache.SaveAsync();

        Directory.Exists(subDir).Should().BeTrue();
        File.Exists(cachePath).Should().BeTrue();
    }

    [TestMethod]
    public async Task Persistence_ReadOnlyFile_SaveAsyncDoesNotThrow()
    {
        var cachePath = Path.Combine(_directory, "readonly-cache.json");
        await File.WriteAllTextAsync(cachePath, "{}");
        File.SetAttributes(cachePath, FileAttributes.ReadOnly);

        var cache = CreateCache(cachePath);
        cache.Set("claude", new UsageData { Provider = "claude", FetchedAt = DateTime.UtcNow });

        // SaveAsync handles the IO exception internally
        var act = async () => await cache.SaveAsync();
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task Persistence_CostStoreReadOnlyFile_SaveAsyncReturnsFalseWithoutThrowing()
    {
        var costPath = Path.Combine(_directory, "readonly-cost.json");
        await File.WriteAllTextAsync(costPath, "{}");
        File.SetAttributes(costPath, FileAttributes.ReadOnly);

        var costs = CreateCosts(costPath);
        var result = await costs.SaveAsync(new Dictionary<string, CostData>
        {
            ["claude"] = new CostData { Provider = "claude", TodayCostUSD = 10, FetchOutcome = FetchOutcome.Success }
        });

        result.Should().BeFalse();
    }

    [TestMethod]
    public async Task Persistence_CorruptedSiblingEntry_RestoresValidSibling()
    {
        var cachePath = Path.Combine(_directory, "sibling-cache.json");
        var now = DateTime.UtcNow.ToString("O");
        var json = $$"""
        {
          "timestamp": "{{now}}",
          "items": {
            "claude": { "provider": "claude", "fetchedAt": "{{now}}", "session": { "reportedPercent": 63 } },
            "codex": "not an object at all"
          }
        }
        """;
        await File.WriteAllTextAsync(cachePath, json);

        var cache = CreateCache(cachePath);
        await cache.LoadAsync();
        cache.GetLastKnown("claude")!.Session!.Percent.Should().Be(63);
        cache.GetLastKnown("codex").Should().BeNull();
    }

    [TestMethod]
    public async Task Concurrency_FailureLandingAfterNewerSuccess_DoesNotOverwriteFreshDataWithStale()
    {
        var cache = CreateCache();
        var service = CreateService(cache);

        // Fetch 1: Fresh success with 95%
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":95}},"status":{"indicator":"none"}}"""
        });
        var fresh = await service.GetUsageAsync("claude");
        fresh!.UsageIsStale.Should().BeFalse();

        // Fetch 2: Delayed/late failure lands
        _clock.CurrentTime += TimeSpan.FromSeconds(5);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = false,
            Error = "Late arriving connection failure",
            ExitCode = 1
        });
        var failedResult = await service.GetUsageAsync("claude");

        // The failed call returned the stale reading...
        failedResult!.Session!.Percent.Should().Be(95);
        failedResult.UsageIsStale.Should().BeTrue();

        // BUT the underlying cache was NOT corrupted by the failure
        var cached = cache.GetLastKnown("claude");
        cached.Should().NotBeNull();
        cached!.UsageIsStale.Should().BeFalse();
        cached.Session!.Percent.Should().Be(95);
    }

    [TestMethod]
    public async Task Concurrency_ParallelRefreshes_CompleteWithoutDeadlockOrCorruption()
    {
        var cache = CreateCache();
        var service = CreateService(cache);

        _wsl.CommandHandler = (cmd, _) =>
        {
            var provider = cmd.Contains("--provider codex") ? "codex" : "claude";
            var percent = provider == "codex" ? 40 : 80;
            return Task.FromResult(new WslResult
            {
                Success = true,
                Output = $"{{\"provider\":\"{provider}\",\"source\":\"cli\",\"usage\":{{\"primary\":{{\"usedPercent\":{percent}}}}}}}"
            });
        };

        // Launch 20 concurrent requests
        var tasks = Enumerable.Range(0, 20)
            .Select(i => service.GetUsageAsync(i % 2 == 0 ? "claude" : "codex"))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        cache.GetLastKnown("claude")!.Session!.Percent.Should().Be(80);
        cache.GetLastKnown("codex")!.Session!.Percent.Should().Be(40);
    }

    [TestMethod]
    public async Task Persistence_ExplicitNullExtraWindowsInJson_ShouldLoadProviderWithoutFailing()
    {
        var cachePath = Path.Combine(_directory, "null-extra-cache.json");
        var now = DateTime.UtcNow.ToString("O");
        var json = $$"""
        {
          "timestamp": "{{now}}",
          "items": {
            "claude": {
              "provider": "claude",
              "fetchedAt": "{{now}}",
              "extraWindows": null,
              "session": { "reportedPercent": 50 }
            }
          }
        }
        """;
        await File.WriteAllTextAsync(cachePath, json);

        var cache = CreateCache(cachePath);
        await cache.LoadAsync();
        cache.GetLastKnown("claude").Should().NotBeNull();
        cache.GetLastKnown("claude")!.Session!.Percent.Should().Be(50);
    }

    [TestMethod]
    public async Task Persistence_CostStoreUnspecifiedDateTimeKind_ShouldLoadWithoutBeingDroppedAsFuture()
    {
        var costPath = Path.Combine(_directory, "unspecified-kind-cost.json");
        // Local timestamp written without timezone offset, e.g. from local log or serialization. It is built from the clock,
        // five minutes before it, so the test holds in every time zone (a fixed "20:00:00" was only in the past on a UTC+9
        // machine and was dropped as a future time on a UTC build server).
        var testClock = new TestClock { CurrentTime = DateTimeOffset.Parse("2026-10-10T12:05:00Z") };
        var localTimeStr = testClock.CurrentTime.ToLocalTime().AddMinutes(-5)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var json = $$"""
        {
          "items": {
            "claude": {
              "provider": "claude",
              "fetchedAt": "{{localTimeStr}}",
              "todayCostUSD": 5.0,
              "fetchOutcome": 1
            }
          }
        }
        """;
        await File.WriteAllTextAsync(costPath, json);

        var store = new CostCacheStore(_settings, NullLogger<CostCacheStore>.Instance, testClock, costPath);
        var loaded = await store.LoadAsync();
        loaded.ContainsKey("claude").Should().BeTrue();
    }
}
