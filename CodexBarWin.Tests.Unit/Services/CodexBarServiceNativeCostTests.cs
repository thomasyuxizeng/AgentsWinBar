using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Services.Native;
using CodexBarWin.Tests.Unit.Mocks;
using CodexBarWin.Tests.Unit.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

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
