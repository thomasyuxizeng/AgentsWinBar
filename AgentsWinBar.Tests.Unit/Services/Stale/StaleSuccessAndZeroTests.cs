using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Tests.Unit.Services.Stale;

[TestClass]
public class StaleSuccessAndZeroTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset CurrentTime { get; set; } = DateTimeOffset.UtcNow.AddHours(-2);
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
        _directory = Path.Combine(Path.GetTempPath(), "cbw-stale-zero-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _clock = new();
        _settings = new();
        _wsl = new();
        _history = new();
        _settings.SetProviders([new() { Id = "claude", IsEnabled = true }]);
        _settings.Settings.ShowCostTotals = true;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private CacheService CreateCache() => new(NullLogger<CacheService>.Instance, _settings, Path.Combine(_directory, "cache.json"));
    private CostCacheStore CreateCosts() => new(_settings, NullLogger<CostCacheStore>.Instance, _clock, Path.Combine(_directory, "cost-cache.json"));
    private CodexBarService CreateService(ICacheService? cache = null, ICostCacheStore? costs = null) =>
        new(_wsl, cache ?? CreateCache(), _settings, new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance, _clock,
            userProfile: () => "C:\\Users\\Test", directoryExists: _ => true, costStore: costs ?? CreateCosts(), history: _history);

    [TestMethod]
    public async Task GetUsageAsync_WhenSuccessFollowsStale_ReplacesCachedUsageAndClearsStaleFlags()
    {
        var cache = CreateCache();
        var service = CreateService(cache);
        // Step 1: Initial success with 40%
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":40}},"status":{"indicator":"none"}}"""
        });
        var initial = await service.GetUsageAsync("claude");
        initial!.Session!.Percent.Should().Be(40);
        initial.UsageIsStale.Should().BeFalse();

        // Step 2: Fetch fails -> becomes stale with 40%
        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = false, Error = "network down" });
        var stale = await service.GetUsageAsync("claude");
        stale!.Session!.Percent.Should().Be(40);
        stale.UsageIsStale.Should().BeTrue();

        // Step 3: Fetch succeeds with 85% -> fresh 85% replaces stale cache
        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":85}},"status":{"indicator":"none"}}"""
        });
        var refreshed = await service.GetUsageAsync("claude");
        refreshed!.Session!.Percent.Should().Be(85);
        refreshed.UsageIsStale.Should().BeFalse();
        refreshed.HasError.Should().BeFalse();
        refreshed.LastSuccessfulFetch.Should().Be(_clock.CurrentTime.UtcDateTime);

        // Verify cache in memory holds the fresh 85%
        cache.GetLastKnown("claude")!.Session!.Percent.Should().Be(85);
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenRealZeroSucceeds_ReplacesCachedValueAndSurvivesSubsequentFailure()
    {
        var cache = CreateCache();
        var service = CreateService(cache);
        // Initial 50%
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":50}},"status":{"indicator":"none"}}"""
        });
        await service.GetUsageAsync("claude");

        // Fresh zero reading (e.g. quota reset)
        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":0}},"status":{"indicator":"none"}}"""
        });
        var zero = await service.GetUsageAsync("claude");
        zero!.Session!.Percent.Should().Be(0);
        zero.UsageIsStale.Should().BeFalse();
        zero.HasUsage.Should().BeTrue();

        // Third fetch fails -> must keep 0% marked as stale, not revert to 50%
        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = false, Error = "timeout" });
        var staleZero = await service.GetUsageAsync("claude");
        staleZero!.Session!.Percent.Should().Be(0);
        staleZero.UsageIsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenRealZeroSucceeds_ReplacesCachedCostsAndSurvivesSubsequentFailure()
    {
        var costs = CreateCosts();
        var service = CreateService(costs: costs);
        // Initial $5.00
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","sessionCostUSD":5.0,"sessionTokens":500,"last30DaysCostUSD":50.0,"last30DaysTokens":5000}"""
        });
        await service.GetCostAsync("claude");

        // Fresh zero reading
        _clock.CurrentTime += TimeSpan.FromMinutes(15);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","sessionCostUSD":0.0,"sessionTokens":0,"last30DaysCostUSD":0.0,"last30DaysTokens":0}"""
        });
        var zeroCost = await service.GetCostAsync("claude", forceRefresh: true);
        zeroCost!.TodayCostUSD.Should().Be(0m);
        zeroCost.TodayTokens.Should().Be(0);
        zeroCost.Last30DaysCostUSD.Should().Be(0m);
        zeroCost.IsStale.Should().BeFalse();
        zeroCost.HasData.Should().BeTrue();

        // Third fetch fails -> must keep 0 cost marked stale
        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = false, Error = "timeout" });
        var staleZeroCost = await service.GetCostAsync("claude", forceRefresh: true);
        staleZeroCost!.TodayCostUSD.Should().Be(0m);
        staleZeroCost.Last30DaysCostUSD.Should().Be(0m);
        staleZeroCost.IsStale.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("null", false)]
    [DataRow("0", true)]
    public async Task GetUsageAsync_NullVsZeroUsedPercent_NullDoesNotReplaceWhileZeroReplaces(string percentLiteral, bool isZero)
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":75}}}"""
        });
        await service.GetUsageAsync("claude");

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = "{\"provider\":\"claude\",\"source\":\"cli\",\"usage\":{\"primary\":{\"usedPercent\":" + percentLiteral + "}}}"
        });

        var result = await service.GetUsageAsync("claude");
        if (isZero)
        {
            result!.Session!.Percent.Should().Be(0);
            result.UsageIsStale.Should().BeFalse();
        }
        else
        {
            result!.Session!.Percent.Should().Be(75);
            result.UsageIsStale.Should().BeTrue();
        }
    }

    [TestMethod]
    [DataRow("null", false)]
    [DataRow("0", true)]
    public async Task GetCostAsync_NullVsZeroCost_NullDoesNotReplaceWhileZeroReplaces(string costLiteral, bool isZero)
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","sessionCostUSD":12.5,"sessionTokens":1000}"""
        });
        await service.GetCostAsync("claude");

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = $$$"""{"provider":"claude","sessionCostUSD":{{{costLiteral}}}}"""
        });

        var result = await service.GetCostAsync("claude", forceRefresh: true);
        if (isZero)
        {
            result!.TodayCostUSD.Should().Be(0m);
            result.IsStale.Should().BeFalse();
        }
        else
        {
            result!.TodayCostUSD.Should().Be(12.5m);
            result.IsStale.Should().BeTrue();
        }
    }

    [TestMethod]
    [DataRow("null", false)]
    [DataRow("0", true)]
    public async Task GetUsageAsync_ExtraWindowNullVsZero_NullRetainsWhileZeroReplaces(string percentLiteral, bool isZero)
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """
                {"provider":"claude","source":"cli","usage":{
                    "extraRateWindows":[{"id":"sonnet","title":"Sonnet","window":{"usedPercent":65}}]
                }}
                """
        });
        await service.GetUsageAsync("claude");

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = $$$"""
                {"provider":"claude","source":"cli","usage":{
                    "extraRateWindows":[{"id":"sonnet","title":"Sonnet","window":{"usedPercent":{{{percentLiteral}}}}}]
                }}
                """
        });

        var result = await service.GetUsageAsync("claude");
        if (isZero)
        {
            result!.ExtraWindows.Single().Window.Percent.Should().Be(0);
            result.UsageIsStale.Should().BeFalse();
        }
        else
        {
            result!.ExtraWindows.Single().Window.Percent.Should().Be(65);
            result.UsageIsStale.Should().BeTrue();
        }
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(-99)]
    public async Task GetUsageAsync_WhenUsedPercentIsNegative_RejectsReadingAndRetainsCache(int negativePercent)
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":50}}}"""
        });
        await service.GetUsageAsync("claude");

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = "{\"provider\":\"claude\",\"source\":\"cli\",\"usage\":{\"primary\":{\"usedPercent\":" + negativePercent + "}}}"
        });

        var result = await service.GetUsageAsync("claude");
        result!.Session!.Percent.Should().Be(50);
        result.UsageIsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenUsedPercentExceeds100_ShouldAcceptAsOverQuotaReading()
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":105}}}"""
        });

        var result = await service.GetUsageAsync("claude");
        result!.Session.Should().NotBeNull();
        result.Session!.Percent.Should().Be(105);
        result.UsageIsStale.Should().BeFalse();
    }
}
