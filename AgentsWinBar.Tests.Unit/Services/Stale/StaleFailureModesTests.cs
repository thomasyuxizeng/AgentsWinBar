using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Tests.Unit.Services.Stale;

[TestClass]
public class StaleFailureModesTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset CurrentTime { get; set; } = DateTimeOffset.UtcNow.AddHours(-3);
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
        _directory = Path.Combine(Path.GetTempPath(), "cbw-stale-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _clock = new();
        _settings = new();
        _wsl = new();
        _history = new();
        _settings.SetProviders([
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "codex", IsEnabled = true },
            new() { Id = "gemini", IsEnabled = true }
        ]);
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

    private static string UsageJson(string provider, int percent, string status = "none") =>
        $$$"""{"provider":"{{{provider}}}","source":"cli","usage":{"primary":{"usedPercent":{{{percent}}}}},"status":{"indicator":"{{{status}}}"}}""";

    private static string CostJson(string provider, decimal sessionCost, long sessionTokens, decimal monthCost, long monthTokens)
    {
        var costProvider = provider == "gemini" ? "antigravity" : provider;
        return $$$"""{"provider":"{{{costProvider}}}","sessionCostUSD":{{{sessionCost}}},"sessionTokens":{{{sessionTokens}}},"last30DaysCostUSD":{{{monthCost}}},"last30DaysTokens":{{{monthTokens}}}}""";
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    [DataRow("gemini")]
    public async Task GetUsageAsync_WhenExceptionThrown_KeepsLastKnownGoodUsageAndMarksStale(string provider)
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = UsageJson(provider, 45) });
        var good = await service.GetUsageAsync(provider);
        good!.Session!.Percent.Should().Be(45);
        good.UsageIsStale.Should().BeFalse();

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => throw new IOException("WSL bridge disconnected");

        var stale = await service.GetUsageAsync(provider);
        stale.Should().NotBeNull();
        stale!.Session!.Percent.Should().Be(45);
        stale.UsageIsStale.Should().BeTrue();
        stale.StatusIsStale.Should().BeTrue();
        stale.LastSuccessfulFetch.Should().Be(good.LastSuccessfulFetch);
        stale.Error.Should().Be("Usage fetch unavailable");
    }

    [TestMethod]
    [DataRow(124, "Process timed out")]
    [DataRow(-1, "Command timed out after 10000ms")]
    public async Task GetUsageAsync_WhenTimeoutOccurs_MarksStaleWithTimeoutMessage(int exitCode, string error)
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = UsageJson("claude", 60) });
        var good = await service.GetUsageAsync("claude");

        _clock.CurrentTime += TimeSpan.FromMinutes(15);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = false, ExitCode = exitCode, Error = error });

        var stale = await service.GetUsageAsync("claude");
        stale!.Session!.Percent.Should().Be(60);
        stale.UsageIsStale.Should().BeTrue();
        stale.Error.Should().Be("Request timed out");
        stale.LastSuccessfulFetch.Should().Be(good!.LastSuccessfulFetch);
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenCallerCancelsToken_ThrowsOperationCanceledException()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await service.GetUsageAsync("claude", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    [DataRow(1, "Permission denied", "Permission denied")]
    [DataRow(2, "", "Command failed (exit code 2)")]
    [DataRow(127, "command not found", "command not found")]
    public async Task GetUsageAsync_WhenNonZeroExitCode_PreservesDataAndCapturesError(int exitCode, string error, string expectedError)
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = UsageJson("codex", 80) });
        await service.GetUsageAsync("codex");

        _clock.CurrentTime += TimeSpan.FromMinutes(5);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = false, ExitCode = exitCode, Error = error });

        var stale = await service.GetUsageAsync("codex");
        stale!.Session!.Percent.Should().Be(80);
        stale.UsageIsStale.Should().BeTrue();
        stale.Error.Should().Be(expectedError);
        if (exitCode == 127) stale.CommandNotFound.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   \r\n\t  ")]
    [DataRow("[]")]
    public async Task GetUsageAsync_WhenEmptyOutput_PreservesLastKnownGood(string output)
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = UsageJson("gemini", 33) });
        await service.GetUsageAsync("gemini");

        _clock.CurrentTime += TimeSpan.FromMinutes(7);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = output, ExitCode = 0 });

        var stale = await service.GetUsageAsync("gemini");
        stale!.Session!.Percent.Should().Be(33);
        stale.UsageIsStale.Should().BeTrue();
        stale.Error.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    [DataRow("not a json payload")]
    [DataRow("{\"provider\":\"claude\",")]
    [DataRow("{\"provider\":\"claude\",\"usage\":{\"primary\":{\"usedPercent\":}}}")]
    public async Task GetUsageAsync_WhenMalformedOrTruncatedJson_PreservesLastKnownGood(string malformed)
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = UsageJson("claude", 77) });
        await service.GetUsageAsync("claude");

        _clock.CurrentTime += TimeSpan.FromMinutes(9);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = malformed, ExitCode = 0 });

        var stale = await service.GetUsageAsync("claude");
        stale!.Session!.Percent.Should().Be(77);
        stale.UsageIsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenJsonHasZeroPercentButMarkedError_DoesNotReplaceCacheWithZero()
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = UsageJson("gemini", 90) });
        await service.GetUsageAsync("gemini");

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"gemini","error":"Rate limit reached","usage":{"primary":{"usedPercent":0}}}"""
        });

        var stale = await service.GetUsageAsync("gemini");
        stale!.Session!.Percent.Should().Be(90);
        stale.UsageIsStale.Should().BeTrue();
        stale.Error.Should().Be("Rate limit reached");
    }

    [TestMethod]
    public async Task GetCostAsync_WhenCostHasZeroTotalsButMarkedError_DoesNotReplaceCacheWithZero()
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = CostJson("gemini", 5.0m, 500, 30.0m, 3000) });
        var good = await service.GetCostAsync("gemini");
        good!.Last30DaysCostUSD.Should().Be(30.0m);

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"antigravity","error":"auth failure","sessionCostUSD":0,"sessionTokens":0,"last30DaysCostUSD":0,"last30DaysTokens":0}"""
        });

        var stale = await service.GetCostAsync("gemini", forceRefresh: true);
        stale!.Last30DaysCostUSD.Should().Be(30.0m);
        stale.IsStale.Should().BeTrue();
        stale.LastSuccessfulFetch.Should().Be(good.LastSuccessfulFetch);
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenProviderOfflineWithSourceOffline_KeepsLastKnownGood()
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = UsageJson("codex", 65) });
        await service.GetUsageAsync("codex");

        _clock.CurrentTime += TimeSpan.FromMinutes(11);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"codex","source":"offline","usage":{"primary":{"usedPercent":0}}}"""
        });

        var stale = await service.GetUsageAsync("codex");
        stale!.Session!.Percent.Should().Be(65);
        stale.UsageIsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenProviderOfflineWithSourceOffline_KeepsLastKnownGoodCost()
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = CostJson("claude", 3.0m, 300, 20.0m, 2000) });
        var good = await service.GetCostAsync("claude");

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","source":"offline","sessionCostUSD":0,"sessionTokens":0}"""
        });

        var stale = await service.GetCostAsync("claude", forceRefresh: true);
        stale!.Last30DaysCostUSD.Should().Be(20.0m);
        stale.IsStale.Should().BeTrue();
        stale.LastSuccessfulFetch.Should().Be(good!.LastSuccessfulFetch);
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenStatusIsMissing_UsageIsFreshWhileStatusIsStale()
    {
        var service = CreateService();
        // First fetch: has both usage and status
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = UsageJson("gemini", 50, "minor") });
        var first = await service.GetUsageAsync("gemini");
        first!.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);

        _clock.CurrentTime += TimeSpan.FromMinutes(8);
        // Second fetch: usage succeeds with new percent, but status field is completely missing
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"gemini","source":"cli","usage":{"primary":{"usedPercent":55}}}"""
        });

        var second = await service.GetUsageAsync("gemini");
        second!.Session!.Percent.Should().Be(55);
        second.UsageIsStale.Should().BeFalse();
        second.StatusIsStale.Should().BeTrue();
        second.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        second.LastSuccessfulFetch.Should().Be(_clock.CurrentTime.UtcDateTime);
        second.LastSuccessfulStatusFetch.Should().Be(first.LastSuccessfulStatusFetch);
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenOnlyStatusIsPresent_StatusIsFreshWhileUsageIsStale()
    {
        var service = CreateService();
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult { Success = true, Output = UsageJson("claude", 40, "none") });
        var first = await service.GetUsageAsync("claude");

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        // Second fetch: status has critical update, but usage is null/missing
        _wsl.CommandHandler = (_, _) => Task.FromResult(new WslResult
        {
            Success = true,
            Output = """{"provider":"claude","status":{"indicator":"critical"}}"""
        });

        var second = await service.GetUsageAsync("claude");
        second!.Session!.Percent.Should().Be(40);
        second.UsageIsStale.Should().BeTrue();
        second.StatusIsStale.Should().BeFalse();
        second.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Critical);
        second.LastSuccessfulFetch.Should().Be(first!.LastSuccessfulFetch);
        second.LastSuccessfulStatusFetch.Should().Be(_clock.CurrentTime.UtcDateTime);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenOneProviderFailsAndOthersSucceed_FailedProviderIsStaleWhileOthersAreFresh()
    {
        var service = CreateService();
        _wsl.CommandHandler = (cmd, _) =>
        {
            if (cmd.Contains("--provider claude")) return Task.FromResult(new WslResult { Success = true, Output = UsageJson("claude", 10) });
            if (cmd.Contains("--provider codex")) return Task.FromResult(new WslResult { Success = true, Output = UsageJson("codex", 20) });
            return Task.FromResult(new WslResult { Success = true, Output = UsageJson("gemini", 30) });
        };
        var initial = await service.GetAllUsageAsync();
        initial.Should().HaveCount(3);

        _clock.CurrentTime += TimeSpan.FromMinutes(10);
        // codex fails, claude and gemini succeed
        _wsl.CommandHandler = (cmd, _) =>
        {
            if (cmd.Contains("--provider claude")) return Task.FromResult(new WslResult { Success = true, Output = UsageJson("claude", 15) });
            if (cmd.Contains("--provider codex")) return Task.FromResult(new WslResult { Success = false, Error = "timeout", ExitCode = 124 });
            return Task.FromResult(new WslResult { Success = true, Output = UsageJson("gemini", 35) });
        };

        var results = (await service.GetAllUsageAsync()).ToDictionary(r => r.Provider);
        results["claude"].Session!.Percent.Should().Be(15);
        results["claude"].UsageIsStale.Should().BeFalse();

        results["codex"].Session!.Percent.Should().Be(20);
        results["codex"].UsageIsStale.Should().BeTrue();
        results["codex"].Error.Should().Be("Request timed out");

        results["gemini"].Session!.Percent.Should().Be(35);
        results["gemini"].UsageIsStale.Should().BeFalse();
    }
}
