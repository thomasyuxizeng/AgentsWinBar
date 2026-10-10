using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class LastKnownGoodTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow.AddHours(-2);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private string _directory = null!;
    private Clock _clock = null!;
    private MockSettingsService _settings = null!;
    private MockWslService _wsl = null!;
    private UsageHistoryStore _history = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _clock = new(); _settings = new(); _wsl = new(); _history = new();
        _settings.SetProviders([new() { Id = "gemini", IsEnabled = true }]);
        _settings.Settings.ShowCostTotals = true;
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private CacheService Cache() => new(NullLogger<CacheService>.Instance, _settings, Path.Combine(_directory, "cache.json"));
    private CostCacheStore Costs() => new(_settings, NullLogger<CostCacheStore>.Instance, _clock, Path.Combine(_directory, "cost-cache.json"));
    private CodexBarService Service(ICacheService? cache = null) => new(_wsl, cache ?? Cache(), _settings,
        new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance, _clock,
        userProfile: () => "C:\\Users\\Test", directoryExists: _ => true, costStore: Costs(), history: _history);

    private void Response(string json, bool success = true) => _wsl.CommandHandler = (_, _) =>
        Task.FromResult(new WslResult { Success = success, Output = json, Error = success ? "" : "Disconnected", ExitCode = success ? 0 : 1 });

    private const string GoodUsage = """{"provider":"gemini","source":"cli","usage":{"primary":{"usedPercent":42},"secondary":{"usedPercent":73}},"status":{"indicator":"none"}}""";
    private const string GoodCost = """{"provider":"antigravity","sessionCostUSD":2.5,"sessionTokens":250,"last30DaysCostUSD":40,"last30DaysTokens":4000}""";

    [TestMethod]
    [DataRow("", false)]
    [DataRow("[]", true)]
    [DataRow("not json", true)]
    [DataRow("{\"provider\":\"gemini\",\"source\":\"offline\",\"usage\":{\"primary\":{\"usedPercent\":0}}}", true)]
    [DataRow("{\"provider\":\"gemini\",\"usage\":{\"primary\":{}}}", true)]
    [DataRow("{\"provider\":\"gemini\",\"error\":\"Unavailable\"}", true)]
    public async Task GetAllUsageAsync_WhenFailedEmptyOrOffline_RetainsWindowsAndStatus(string json, bool success)
    {
        var service = Service(); Response(GoodUsage);
        var good = (await service.GetAllUsageAsync()).Single();
        _clock.Now += TimeSpan.FromMinutes(12); Response(json, success);
        var stale = (await service.GetAllUsageAsync()).Single();
        stale.Session!.Percent.Should().Be(42);
        stale.Weekly!.Percent.Should().Be(73);
        stale.ServiceStatus.Should().BeEquivalentTo(good.ServiceStatus);
        stale.UsageIsStale.Should().BeTrue(); stale.StatusIsStale.Should().BeTrue();
        stale.LastSuccessfulFetch.Should().Be(good.LastSuccessfulFetch);
        var snapshot = DashboardSnapshotBuilder.Build(new() { Providers = [stale], NowUtc = _clock.Now.UtcDateTime });
        snapshot.Providers.Single().StaleAge.Should().Be(TimeSpan.FromMinutes(12));
        _history.Record(stale, _clock.Now.UtcDateTime).Should().Be(0);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenRealZeroSucceeds_ReplacesCachedUsageAndRetainsMissingStatus()
    {
        var cache = Cache(); var service = Service(cache); Response(GoodUsage);
        await service.GetAllUsageAsync(); _clock.Now += TimeSpan.FromMinutes(12);
        Response("""{"provider":"gemini","usage":{"primary":{"usedPercent":0}}}""");
        var zero = (await service.GetAllUsageAsync()).Single();
        zero.UsageFetchOutcome.Should().Be(FetchOutcome.Success);
        zero.UsageIsStale.Should().BeFalse(); zero.Session!.Percent.Should().Be(0);
        zero.Weekly.Should().BeNull(); zero.HasError.Should().BeFalse();
        zero.LastSuccessfulFetch.Should().Be(_clock.Now.UtcDateTime);
        zero.StatusIsStale.Should().BeTrue(); zero.ServiceStatus.Should().NotBeNull();
        cache.GetLastKnown("gemini")!.Session!.Percent.Should().Be(0);
        Response("", false);
        (await service.GetAllUsageAsync()).Single().Session!.Percent.Should().Be(0);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenOnlyStatusSucceeds_PersistsStatusIndependently()
    {
        var cache = Cache(); var service = Service(cache); Response(GoodUsage);
        var good = (await service.GetAllUsageAsync()).Single(); _clock.Now += TimeSpan.FromMinutes(5);
        Response("""{"provider":"gemini","status":{"indicator":"major"}}""");
        var partial = (await service.GetAllUsageAsync()).Single();
        partial.Session!.Percent.Should().Be(42); partial.UsageIsStale.Should().BeTrue();
        partial.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Major);
        partial.StatusIsStale.Should().BeFalse();
        partial.LastSuccessfulFetch.Should().Be(good.LastSuccessfulFetch);
        partial.LastSuccessfulStatusFetch.Should().Be(_clock.Now.UtcDateTime);
        var restored = Cache(); await restored.LoadAsync(); Response("", false);
        var stale = (await Service(restored).GetAllUsageAsync()).Single();
        stale.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Major);
        stale.LastSuccessfulStatusFetch.Should().Be(partial.LastSuccessfulStatusFetch);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenRestarted_RestoresExpiredDiskCache()
    {
        Response(GoodUsage); var first = (await Service().GetAllUsageAsync()).Single();
        _clock.Now += TimeSpan.FromDays(10);
        var cache = Cache(); await cache.LoadAsync(); Response("", false);
        var stale = (await Service(cache).GetAllUsageAsync()).Single();
        stale.Session!.Percent.Should().Be(42); stale.LastSuccessfulFetch.Should().Be(first.LastSuccessfulFetch);
        stale.UsageIsStale.Should().BeTrue();
        File.Exists(Path.Combine(_directory, "cache.json.tmp")).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenNoCacheButHistoryExists_RestoresLatestPerWindow()
    {
        _history.Add(new() { Provider = "gemini", WindowKey = "session", UsedPercent = 15, Timestamp = _clock.Now.UtcDateTime.AddMinutes(-20) }, _clock.Now.UtcDateTime);
        _history.Add(new() { Provider = "gemini", WindowKey = "session", UsedPercent = 25, Timestamp = _clock.Now.UtcDateTime.AddMinutes(-12) }, _clock.Now.UtcDateTime);
        _history.Add(new() { Provider = "gemini", WindowKey = "extra:flash", UsedPercent = 71, Timestamp = _clock.Now.UtcDateTime.AddMinutes(-12) }, _clock.Now.UtcDateTime);
        Response("", false);
        var stale = (await Service().GetAllUsageAsync()).Single();
        stale.Session!.Percent.Should().Be(25); stale.ExtraWindows.Single().Window.Percent.Should().Be(71);
        stale.UsageIsStale.Should().BeTrue();
        var snapshot = DashboardSnapshotBuilder.Build(new() { Providers = [stale], HistorySamples = _history.Samples, NowUtc = _clock.Now.UtcDateTime });
        snapshot.Providers.Single().HistorySampleCount.Should().Be(3);
        snapshot.Providers.Single().StaleAge.Should().Be(TimeSpan.FromMinutes(12));
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenNoCacheOrHistory_ReturnsEmptyState()
    {
        Response("", false); var service = Service();
        var empty = (await service.GetAllUsageAsync()).Single();
        empty.HasUsage.Should().BeFalse(); empty.UsageIsStale.Should().BeFalse();
        empty.LastSuccessfulFetch.Should().BeNull(); empty.HasError.Should().BeTrue();
        (await service.GetCostAsync("gemini")).Should().BeNull();
        var snapshot = DashboardSnapshotBuilder.Build(new() { Providers = [empty], NowUtc = _clock.Now.UtcDateTime });
        snapshot.Providers.Single().Windows.Should().BeEmpty();
        snapshot.Providers.Single().Cost.Should().BeNull();
    }

    [TestMethod]
    [DataRow("", false)]
    [DataRow("[]", true)]
    [DataRow("{\"provider\":\"antigravity\",\"daily\":[]}", true)]
    [DataRow("{\"provider\":\"antigravity\",\"sessionTokens\":0,\"error\":\"offline\"}", true)]
    [DataRow("{\"provider\":\"antigravity\",\"sessionTokens\":0,\"source\":\"offline\"}", true)]
    public async Task GetCostAsync_WhenUnavailable_RetainsOriginalValuesAndAge(string json, bool success)
    {
        var service = Service(); Response(GoodCost); var good = await service.GetCostAsync("gemini");
        _clock.Now += TimeSpan.FromDays(8); Response(json, success);
        var stale = await service.GetCostAsync("gemini", forceRefresh: true);
        stale!.TodayTokens.Should().Be(250); stale.TodayCostUSD.Should().Be(2.5m);
        stale.Last30DaysTokens.Should().Be(4000); stale.IsStale.Should().BeTrue();
        stale.LastSuccessfulFetch.Should().Be(good!.LastSuccessfulFetch);
        var snapshot = DashboardSnapshotBuilder.Build(new() { Providers = [new() { Provider = "gemini", Cost = stale }],
            Settings = _settings.Settings, NowUtc = _clock.Now.UtcDateTime });
        snapshot.Providers.Single().Cost!.StaleAge.Should().Be(TimeSpan.FromDays(8));
        snapshot.Providers.Single().IsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenSuccessfulZero_ReplacesPersistedCostAndSurvivesRestart()
    {
        var service = Service(); Response(GoodCost); await service.GetCostAsync("gemini");
        _clock.Now += TimeSpan.FromMinutes(12);
        Response("""{"provider":"antigravity","sessionCostUSD":0,"sessionTokens":0,"last30DaysCostUSD":0,"last30DaysTokens":0}""");
        var zero = await service.GetCostAsync("gemini", forceRefresh: true);
        zero!.HasData.Should().BeTrue(); zero.FetchOutcome.Should().Be(FetchOutcome.Success);
        zero.IsStale.Should().BeFalse(); zero.TodayTokens.Should().Be(0); zero.Last30DaysCostUSD.Should().Be(0);
        _clock.Now += TimeSpan.FromDays(10); Response("", false);
        var restored = await Service().GetCostAsync("gemini");
        restored!.HasData.Should().BeTrue(); restored.IsStale.Should().BeTrue();
        restored.Last30DaysCostUSD.Should().Be(0); restored.LastSuccessfulFetch.Should().Be(zero.LastSuccessfulFetch);
    }

    [TestMethod]
    public async Task GetCostAsync_WhenRestartedAndDisconnected_RetainsNonzeroDiskTotals()
    {
        Response(GoodCost); var good = await Service().GetCostAsync("gemini");
        _clock.Now += TimeSpan.FromDays(10); Response("", false);
        var restored = await Service().GetCostAsync("gemini");
        restored!.TodayCostUSD.Should().Be(2.5m); restored.TodayTokens.Should().Be(250);
        restored.LastSuccessfulFetch.Should().Be(good!.LastSuccessfulFetch); restored.IsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenTimeoutThrown_FallsBackWithoutReplacingCache()
    {
        var service = Service(); Response(GoodUsage); await service.GetAllUsageAsync();
        _wsl.BatchHandler = (_, _, _) => throw new IOException("Disconnected");
        _wsl.CommandHandler = (_, _) => throw new OperationCanceledException();
        var stale = (await service.GetAllUsageAsync()).Single();
        stale.Session!.Percent.Should().Be(42); stale.UsageIsStale.Should().BeTrue();
        stale.Error.Should().Be("Request timed out");
    }

    [TestMethod]
    public void Build_WhenTimestampIsInFuture_ClampsStaleAgeToZero()
    {
        var snapshot = DashboardSnapshotBuilder.Build(new() { Providers = [new() { Provider = "gemini", UsageIsStale = true,
            Session = new(), LastSuccessfulFetch = _clock.Now.UtcDateTime.AddMinutes(1) }], NowUtc = _clock.Now.UtcDateTime });
        snapshot.Providers.Single().StaleAge.Should().Be(TimeSpan.Zero);
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenSuccessfulProviderHasFailingSibling_PersistsSuccessfulReading()
    {
        _settings.SetProviders([new() { Id = "gemini", IsEnabled = true }, new() { Id = "codex", IsEnabled = true }]);
        _wsl.CommandHandler = (command, _) => Task.FromResult(command.Contains("--provider gemini")
            ? new WslResult { Success = true, Output = GoodUsage } : new WslResult { Error = "offline" });
        await Service().GetAllUsageAsync();
        var cache = Cache(); await cache.LoadAsync();
        cache.GetLastKnown("gemini")!.Session!.Percent.Should().Be(42);
        cache.GetLastKnown("codex").Should().BeNull();
    }
}
