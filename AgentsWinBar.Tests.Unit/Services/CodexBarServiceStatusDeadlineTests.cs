using System.Diagnostics;
using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Services.Native;
using AgentsWinBar.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

/// <summary>
/// A prefetched provider status may take up to <see cref="CodexBarService.StatusDeadline"/> from the start of its
/// prefetch, on both routes, and a slower usage read adds no extra wait.
/// </summary>
[TestClass]
public class CodexBarServiceStatusDeadlineTests
{
    private const string ClaudeCliCommand = "codexbar --provider claude --format json --source cli --status";
    private const string ClaudeCliJson = """[{"provider":"claude","usage":{"primary":{"usedPercent":45,"windowMinutes":300}}}]""";

    private MockSettingsService _settings = null!;
    private Mock<IProviderIncidentFetcher> _fetcher = null!;
    private Mock<INativeUsageService> _native = null!;

    [TestInitialize]
    public void Setup()
    {
        _settings = new();
        _settings.SetProviders([new() { Id = "claude", IsEnabled = true }]);
        _settings.Settings.Normalize();
        _fetcher = new(MockBehavior.Strict);
        _native = new();
        _native.Setup(n => n.FetchUsageJsonAsync("claude", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NativeUsageService.Serialize(new NativeUsageResult
            {
                Provider = "claude", Status = NativeUsageStatus.Ok, Primary = new(45, 300, null, null), UpdatedAt = DateTimeOffset.UtcNow
            }));
    }

    private CodexBarService Create(IWslService wsl) =>
        new(wsl, new MockCacheService(), _settings, new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance,
            incidentFetcher: _fetcher.Object, nativeUsage: _native.Object);

    private void StatusAfter(TimeSpan delay) =>
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await Task.Delay(delay, ct);
                return new ProviderStatus { Indicator = ProviderStatusIndicator.Major, Description = "Outage" };
            });

    [TestMethod]
    public void StatusDeadline_IsFourSeconds()
    {
        CodexBarService.StatusDeadline.Should().Be(TimeSpan.FromSeconds(4));
    }

    [TestMethod]
    public async Task GetAllUsageAsync_NativeUsageInstantStatusAfterOneAndAHalfSeconds_AttachesStatus()
    {
        // Under the old 500 ms budget after the usage call, this status was always missed on the native route.
        StatusAfter(TimeSpan.FromMilliseconds(1500));

        var result = (await Create(new Mock<IWslService>(MockBehavior.Strict).Object).GetAllUsageAsync()).Single();

        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Major);
        result.StatusFetchOutcome.Should().Be(FetchOutcome.Success);
    }

    [TestMethod]
    public async Task GetUsageAsync_NativeUsageStatusHangs_GivesUpAtDeadline()
    {
        StatusAfter(Timeout.InfiniteTimeSpan);

        var stopwatch = Stopwatch.StartNew();
        var result = await Create(new Mock<IWslService>(MockBehavior.Strict).Object).GetUsageAsync("claude");
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(CodexBarService.StatusDeadline - TimeSpan.FromMilliseconds(100));
        stopwatch.Elapsed.Should().BeLessThan(CodexBarService.StatusDeadline + TimeSpan.FromSeconds(2));
        result!.Session!.Percent.Should().Be(45);
        result.ServiceStatus.Should().BeNull();
    }

    [TestMethod]
    public async Task GetUsageAsync_WslUsageSlowerThanDeadline_AddsNoExtraWait()
    {
        _settings.Settings.UsageSources["claude"] = UsageSource.Wsl;
        StatusAfter(Timeout.InfiniteTimeSpan);
        var usageTime = CodexBarService.StatusDeadline + TimeSpan.FromMilliseconds(300);
        var wsl = new MockWslService
        {
            CommandHandler = async (_, ct) =>
            {
                await Task.Delay(usageTime, ct);
                return new WslResult { Success = true, Output = ClaudeCliJson };
            }
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await Create(wsl).GetUsageAsync("claude");
        stopwatch.Stop();

        // The old code waited another 500 ms here; the deadline has passed, so there is no wait at all.
        stopwatch.Elapsed.Should().BeLessThan(usageTime + TimeSpan.FromMilliseconds(450));
        result!.Session!.Percent.Should().Be(45);
        result.ServiceStatus.Should().BeNull();
    }

    [TestMethod]
    public async Task GetUsageAsync_WslUsageSlowStatusFinishedEarlier_AttachesStatus()
    {
        _settings.Settings.UsageSources["claude"] = UsageSource.Wsl;
        StatusAfter(TimeSpan.FromMilliseconds(100));
        var wsl = new MockWslService
        {
            CommandHandler = async (_, ct) =>
            {
                await Task.Delay(CodexBarService.StatusDeadline + TimeSpan.FromMilliseconds(200), ct);
                return new WslResult { Success = true, Output = ClaudeCliJson };
            }
        };

        var result = await Create(wsl).GetUsageAsync("claude");

        wsl.ExecutedCommands.Should().Equal(ClaudeCliCommand);
        result!.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Major);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WslUsageTwoSecondsStatusThreeSeconds_AttachesStatus()
    {
        // The status may finish after the usage call, as long as it is within the deadline from its own start.
        _settings.Settings.UsageSources["claude"] = UsageSource.Wsl;
        StatusAfter(TimeSpan.FromSeconds(3));
        var wsl = new MockWslService
        {
            CommandHandler = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                return new WslResult { Success = true, Output = ClaudeCliJson };
            }
        };

        var result = (await Create(wsl).GetAllUsageAsync()).Single();

        wsl.ExecutedBatches.Should().ContainSingle();
        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Major);
    }
}
