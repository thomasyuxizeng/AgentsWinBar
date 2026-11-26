using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Services.Native;
using AgentsWinBar.Tests.Unit.Mocks;
using AgentsWinBar.Tests.Unit.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

/// <summary>Cost source routing and the interpretation of native cost reports.</summary>
[TestClass]
public sealed class CodexBarServiceNativeCostTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 6, 0, 0, TimeSpan.Zero);
    private MockSettingsService _settings = null!;
    private ManualClock _clock = null!;

    [TestInitialize]
    public void Setup()
    {
        _settings = new();
        _clock = new(Now);
        _settings.SetProviders([
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "codex", IsEnabled = true },
            new() { Id = "gemini", IsEnabled = true }
        ]);
        _settings.Settings.Normalize();
    }

    private CodexBarService Create(IWslService wsl, INativeCostService? cost, ICostLedger? ledger = null,
        INativeUsageService? usage = null) =>
        new(wsl, new MockCacheService(), _settings, new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance,
            _clock, costLedger: ledger, nativeUsage: usage, nativeCost: cost);

    private static IWslService NoWsl() => new Mock<IWslService>(MockBehavior.Strict).Object;

    private static string Report(string cliProvider, long todayTokens, decimal todayCost, long unpriced = 0) =>
        JsonSerializer.Serialize(new CostDataDto
        {
            Provider = cliProvider, CurrencyCode = "USD", Source = "local", Provenance = "native-jsonl",
            SessionTokens = todayTokens, SessionCostUSD = todayCost, Last30DaysTokens = todayTokens + 1_000,
            Last30DaysCostUSD = todayCost + 1, UnpricedTokens = unpriced, Coverage = new() { Priced = 3, Unpriced = 1 },
            Daily =
            [
                new()
                {
                    Date = new DateOnly(2026, 11, 25), TotalTokens = todayTokens, TotalCost = todayCost, UnpricedTokens = unpriced,
                    ModelBreakdowns = [new() { ModelName = "claude-opus-5-5", TotalTokens = todayTokens, Cost = todayCost }]
                }
            ]
        }, AppJsonSerializerContext.Default.CostDataDto);

    private static FakeNativeCost AllProviders() => new(provider => provider switch
    {
        "claude" => Report("claude", 5_000, 1.25m, unpriced: 40),
        "codex" => Report("codex", 7_000, 2.5m),
        _ => Report("antigravity", 0, 0)
    });

    [TestMethod]
    public void UsesNativeCost_WhenSettingsAreDefault_IsNative()
    {
        _settings.Settings.CostSource.Should().Be(UsageSource.Native);
        _settings.Settings.UsesNativeCost().Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenCostSourceIsNative_NeverReachesWslAndInterpretsTheReport()
    {
        var native = AllProviders();

        var cost = await Create(NoWsl(), native).GetCostAsync("Claude");

        cost.Should().NotBeNull();
        cost!.Provider.Should().Be("claude");
        cost.TodayTokens.Should().Be(5_000);
        cost.TodayCostUSD.Should().Be(1.25m);
        cost.Last30DaysTokens.Should().Be(6_000);
        cost.UnpricedTokens.Should().Be(40);
        cost.Daily.Single().UnpricedTokens.Should().Be(40);
        cost.Coverage.Unpriced.Should().Be(1);
        cost.Provenance.Should().Be("native-jsonl");
        cost.FetchOutcome.Should().Be(FetchOutcome.Success);
        cost.IsStale.Should().BeFalse();
        cost.FetchedAt.Should().Be(Now.UtcDateTime);
        native.Calls.Should().ContainSingle().Which.Should().Equal("claude");
    }

    [TestMethod]
    public async Task GetCostsAsync_WhenCostSourceIsNative_UpdatesOnceForEveryProviderWithoutWsl()
    {
        var native = AllProviders();
        var results = new Dictionary<string, CostData?>();

        await foreach (var (provider, cost) in Create(NoWsl(), native).GetCostsAsync(["claude", "codex", "gemini"]))
            results[provider] = cost;

        native.Calls.Should().ContainSingle().Which.Should().Equal("claude", "codex", "gemini");
        results["claude"]!.TodayTokens.Should().Be(5_000);
        results["codex"]!.TodayCostUSD.Should().Be(2.5m);
        // The CLI reports Gemini as a reading without usage; the native report is interpreted the same way.
        results["gemini"]!.Provider.Should().Be("gemini");
        results["gemini"]!.TodayTokens.Should().Be(0);
        results["gemini"]!.IsStale.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAllUsageAndCosts_WhenEverySourceIsNative_NeverStartsWsl()
    {
        var service = Create(NoWsl(), AllProviders(), usage: new FakeNativeUsage());

        var usage = await service.GetAllUsageAsync();
        var costs = new List<CostData?>();
        await foreach (var (_, cost) in service.GetCostsAsync(["claude", "codex", "gemini"])) costs.Add(cost);

        usage.Should().HaveCount(3);
        costs.Should().HaveCount(3).And.NotContainNulls();
        (await service.IsAvailableAsync()).Should().BeTrue();
    }

    [TestMethod]
    [DataRow("wsl")]
    [DataRow("WSL")]
    public async Task GetCostAsync_WhenCostSourceIsWsl_RunsTheCliAndNotTheNativeScanner(string stored)
    {
        _settings.Settings.CostSource = stored;
        var native = AllProviders();
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        wsl.Setup(w => w.ExecuteAsync(It.Is<string>(command => command.Contains("codexbar cost --provider claude")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WslResult { Success = true, Output = Report("claude", 9_000, 3m) });

        var cost = await Create(wsl.Object, native).GetCostAsync("claude");

        cost!.TodayTokens.Should().Be(9_000);
        native.Calls.Should().BeEmpty();
        wsl.Verify(w => w.ExecuteAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetCostAsync_WhenNoNativeScannerIsRegistered_FallsBackToWsl()
    {
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        wsl.Setup(w => w.ExecuteAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WslResult { Success = true, Output = Report("codex", 100, 1m) });

        var cost = await Create(wsl.Object, cost: null).GetCostAsync("codex");

        cost!.TodayTokens.Should().Be(100);
    }

    [TestMethod]
    public async Task GetCostAsync_WhenNativeResultIsFresh_ServesItFromTheCacheForThirtyMinutes()
    {
        var native = AllProviders();
        var service = Create(NoWsl(), native);

        await service.GetCostAsync("claude");
        _clock.Now = Now.AddMinutes(29);
        var cached = await service.GetCostAsync("claude");
        _clock.Now = Now.AddMinutes(31);
        await service.GetCostAsync("claude");

        cached!.TodayTokens.Should().Be(5_000);
        native.Calls.Should().HaveCount(2);
    }

    // Review finding 9: a clock that steps backward must not keep the cost cache alive for the size of the jump.
    [TestMethod]
    public async Task GetCostAsync_WhenClockMovesBackward_RescansInsteadOfServingCache()
    {
        var native = AllProviders();
        var service = Create(NoWsl(), native);

        await service.GetCostAsync("claude");
        _clock.Now = Now.AddHours(-2);
        await service.GetCostAsync("claude");

        native.Calls.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task GetCostAsync_WhenNativeScanThrowsAfterAGoodResult_ServesLastGoodAsStale()
    {
        var fail = false;
        var native = new FakeNativeCost(_ => fail ? throw new IOException("index locked") : Report("claude", 5_000, 1.25m));
        var service = Create(NoWsl(), native);
        await service.GetCostAsync("claude");
        fail = true;

        var stale = await service.GetCostAsync("claude", forceRefresh: true);

        stale.Should().NotBeNull();
        stale!.TodayTokens.Should().Be(5_000);
        stale.IsStale.Should().BeTrue();
        stale.StaleReason.Should().Be("Scan failed");
        stale.LastSuccessfulFetch.Should().Be(Now.UtcDateTime);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("{\"provider\":\"codex\",\"sessionTokens\":5}")]
    [DataRow("{\"provider\":\"claude\",\"error\":\"boom\",\"sessionTokens\":5}")]
    public async Task GetCostAsync_WhenNativeReportIsMissingOrNotForTheProvider_ReturnsNothing(string? report)
    {
        var cost = await Create(NoWsl(), new FakeNativeCost(_ => report)).GetCostAsync("claude");

        cost.Should().BeNull();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenNativeScanSucceeds_RecordsItInTheLedger()
    {
        var ledger = new Mock<ICostLedger>();
        IReadOnlyList<CostData>? recorded = null;
        ledger.Setup(l => l.RecordScansAsync(It.IsAny<IReadOnlyList<CostData>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<CostData>, DateTime?, CancellationToken>((scans, _, _) => recorded = scans)
            .Returns(Task.CompletedTask);

        await Create(NoWsl(), AllProviders(), ledger.Object).GetCostAsync("claude");

        recorded.Should().ContainSingle();
        recorded![0].Provider.Should().Be("claude");
        recorded[0].Daily.Single().TotalCost.Should().Be(1.25m);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task GetCostAsync_WhenCostsAreHiddenOrDeveloperModeIsOn_DoesNotScan(bool showCosts, bool developerMode)
    {
        _settings.Settings.ShowCostTotals = showCosts;
        _settings.Settings.DeveloperModeEnabled = developerMode;
        var native = AllProviders();

        var cost = await Create(NoWsl(), native).GetCostAsync("claude");

        cost.Should().BeNull();
        native.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenProviderIsNotAllowed_DoesNotScan()
    {
        var native = AllProviders();

        var cost = await Create(NoWsl(), native).GetCostAsync("claude && whoami");

        cost.Should().BeNull();
        native.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCostAndCostsAsync_WithTypedCostService_CallsFetchCostDtosAsyncWithoutJsonRoundtrip()
    {
        var typed = new Mock<INativeTypedCostService>(MockBehavior.Strict);
        var claudeDto = new CostDataDto
        {
            Provider = "claude", CurrencyCode = "USD", Source = "local", Provenance = "native-jsonl",
            SessionTokens = 1234, SessionCostUSD = 0.75m, Last30DaysTokens = 2000, Last30DaysCostUSD = 1.5m
        };
        typed.Setup(n => n.FetchCostDtosAsync(It.IsAny<IReadOnlyList<string>>(), Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, CostDataDto?>(StringComparer.Ordinal) { ["claude"] = claudeDto });
        var service = Create(NoWsl(), typed.Object);

        var single = await service.GetCostAsync("claude");
        var streamed = new List<CostData?>();
        await foreach (var (_, cost) in service.GetCostsAsync(["claude"], forceRefresh: true))
        {
            streamed.Add(cost);
        }

        single!.TodayTokens.Should().Be(1234);
        streamed.Should().ContainSingle().Which!.TodayCostUSD.Should().Be(0.75m);
        typed.Verify(n => n.FetchCostDtosAsync(It.IsAny<IReadOnlyList<string>>(), Now, It.IsAny<CancellationToken>()), Times.Exactly(2));
        typed.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenNativeScanHasNoGeminiHistory_ReturnsNothingAndDropsTheLastGoodReading()
    {
        string? gemini = Report("antigravity", 0, 0);
        var fail = false;
        var native = new FakeNativeCost(provider => fail ? throw new IOException("index locked")
            : provider == "gemini" ? gemini : Report(provider, 1, 0.1m));
        var service = Create(NoWsl(), native);
        (await service.GetCostAsync("gemini")).Should().NotBeNull("earlier builds reported $0 for Gemini");

        gemini = null;
        var withoutHistory = await service.GetCostAsync("gemini", forceRefresh: true);
        fail = true;
        var afterFailure = await service.GetCostAsync("gemini", forceRefresh: true);

        withoutHistory.Should().BeNull("no local history is no cost to show, not a stale reading");
        afterFailure.Should().BeNull("the $0 reading was dropped, so a later failure has nothing stale to serve");
    }

    [TestMethod]
    public async Task GetCostsAsync_WithTypedCostServiceAndNoGeminiHistory_HidesOnlyGemini()
    {
        var typed = new Mock<INativeTypedCostService>(MockBehavior.Strict);
        var claudeDto = new CostDataDto
        {
            Provider = "claude", CurrencyCode = "USD", Source = "local", Provenance = "native-jsonl",
            SessionTokens = 1234, SessionCostUSD = 0.75m, Last30DaysTokens = 2000, Last30DaysCostUSD = 1.5m
        };
        typed.Setup(n => n.FetchCostDtosAsync(It.IsAny<IReadOnlyList<string>>(), Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, CostDataDto?>(StringComparer.Ordinal) { ["claude"] = claudeDto, ["gemini"] = null });
        var results = new Dictionary<string, CostData?>();

        await foreach (var (provider, cost) in Create(NoWsl(), typed.Object).GetCostsAsync(["claude", "gemini"]))
            results[provider] = cost;

        results["claude"]!.TodayTokens.Should().Be(1234);
        results["claude"]!.IsStale.Should().BeFalse();
        results.GetValueOrDefault("gemini").Should().BeNull();
    }

    private sealed class FakeNativeCost(Func<string, string?> report) : INativeCostService
    {
        public List<string[]> Calls { get; } = [];

        public Task<IReadOnlyDictionary<string, string?>> FetchCostJsonAsync(IReadOnlyList<string> providers,
            DateTimeOffset asOf, CancellationToken ct = default)
        {
            Calls.Add([.. providers]);
            IReadOnlyDictionary<string, string?> reports = providers.ToDictionary(p => p, report, StringComparer.Ordinal);
            return Task.FromResult(reports);
        }
    }

    private sealed class FakeNativeUsage : INativeUsageService
    {
        public Task<string> FetchUsageJsonAsync(string provider, CancellationToken ct = default) => Task.FromResult(
            "[{\"provider\":\"" + provider + "\",\"source\":\"oauth\",\"usage\":{\"primary\":{\"usedPercent\":10,\"windowMinutes\":300," +
            "\"resetsAt\":\"2026-11-25T08:00:00Z\",\"resetDescription\":\"in 2h\"},\"updatedAt\":\"2026-11-25T06:00:00Z\"}}]");

        public async Task<IReadOnlyDictionary<string, string>> FetchAllUsageJsonAsync(IReadOnlyList<string> providers,
            CancellationToken ct = default)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var provider in providers) result[provider] = await FetchUsageJsonAsync(provider, ct);
            return result;
        }
    }
}
