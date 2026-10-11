using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class CodexBarServiceEarlyResetTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset CurrentTime { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => CurrentTime;
    }

    private DateTimeOffset _now;
    private string _tempDir = null!;
    private MockWslService _wsl = null!;
    private CacheService _cacheService = null!;
    private MockSettingsService _settings = null!;
    private MockSampleDataLoader _sampleLoader = null!;
    private ActivityLog _activityLog = null!;
    private EarlyResetStore _earlyResetStore = null!;
    private Mock<IToastNotifier> _mockToastNotifier = null!;
    private FixedClock _clock = null!;
    private CodexBarService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cbw-svc-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _now = DateTimeOffset.UtcNow;
        _clock = new FixedClock(_now);
        _wsl = new MockWslService();
        _settings = new MockSettingsService();
        _settings.Settings.AlertsEnabled = true;

        var cachePath = Path.Combine(_tempDir, "cache.json");
        _cacheService = new CacheService(NullLogger<CacheService>.Instance, _settings, cachePath);

        _sampleLoader = new MockSampleDataLoader();
        _activityLog = new ActivityLog();

        var resetPath = Path.Combine(_tempDir, "early-resets.json");
        _earlyResetStore = new EarlyResetStore(_settings, NullLogger<EarlyResetStore>.Instance, _clock, resetPath);
        _mockToastNotifier = new Mock<IToastNotifier>();

        _service = new CodexBarService(
            wslService: _wsl,
            cacheService: _cacheService,
            settingsService: _settings,
            sampleDataLoader: _sampleLoader,
            logger: NullLogger<CodexBarService>.Instance,
            timeProvider: _clock,
            activityLog: _activityLog,
            earlyResetStore: _earlyResetStore,
            toastNotifier: _mockToastNotifier.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string BuildUsageJson(string provider, int percent, string resetsAt, bool cached = false, string source = "cli") =>
        $"[{{\"provider\":\"{provider}\",\"source\":\"{source}\",\"cached\":{(cached ? "true" : "false")},\"usage\":{{\"loginMethod\":\"test\",\"primary\":{{\"usedPercent\":{percent},\"windowMinutes\":300,\"resetsAt\":\"{resetsAt}\"}}}}}}]";

    [TestMethod]
    public async Task FreshSuccess_AfterHigherPreviousWithFutureResetAt_RaisesOneEventOneToastOneActivityEntry()
    {
        // 1. Seed last-known reading in cache
        var previous = new UsageData
        {
            Provider = "codex",
            Session = new UsageWindow
            {
                ReportedPercent = 79,
                Limit = 100,
                WindowMinutes = 300,
                ResetAt = _now.UtcDateTime.AddHours(2)
            },
            FetchedAt = _now.UtcDateTime.AddMinutes(-5),
            UsageFetchOutcome = FetchOutcome.Success
        };
        _cacheService.Set("codex", previous);

        // 2. WSL executes and returns fresh success reading with sharp drop (3%) and later reset (+5h)
        var nextReset = _now.UtcDateTime.AddHours(5).ToString("O");
        var freshJson = BuildUsageJson("codex", 3, nextReset);
        _wsl.SetCommandResult("codexbar --provider codex --format json --source cli --status",
            new WslResult { Success = true, Output = freshJson });

        // Act
        var result = await _service.GetUsageAsync("codex");

        // Assert
        result.Should().NotBeNull();
        result!.Session!.Percent.Should().Be(3);

        // One event recorded in store
        var active = _earlyResetStore.GetActive(_now.UtcDateTime);
        active.Should().ContainSingle();
        var ev = active[0];
        ev.Provider.Should().Be("codex");
        ev.WindowKey.Should().Be("session");
        ev.PreviousPercent.Should().Be(79);
        ev.CurrentPercent.Should().Be(3);
        _earlyResetStore.IsNotified(ev).Should().BeTrue();

        // One toast notification raised
        _mockToastNotifier.Verify(t => t.Show(
            It.Is<string>(title => title.Contains("Codex") && title.Contains("reset early")),
            It.Is<string>(body => body.Contains("79%") && body.Contains("3%"))),
            Times.Once);

        // One activity alert entry logged
        _activityLog.RecentAlerts.Should().ContainSingle();
        var alert = _activityLog.RecentAlerts[0];
        alert.ProviderId.Should().Be("codex");
        alert.Title.Should().Contain("Codex Session was reset early");
        alert.Message.Should().Contain("Usage dropped from 79% to 3%");
    }

    [TestMethod]
    public async Task ReplayOrFailedFetch_DoesNotTriggerDetectionOrToast()
    {
        var previous = new UsageData
        {
            Provider = "codex",
            Session = new UsageWindow
            {
                ReportedPercent = 80,
                ResetAt = _now.UtcDateTime.AddHours(2)
            },
            FetchedAt = _now.UtcDateTime.AddMinutes(-2),
            UsageFetchOutcome = FetchOutcome.Success
        };
        _cacheService.Set("codex", previous);

        // Scenario A: Failed fetch
        _wsl.SetCommandResult("codexbar --provider codex --format json --source cli --status",
            new WslResult { Success = false, ExitCode = 1, Error = "Connection timed out" });

        var failedResult = await _service.GetUsageAsync("codex");
        failedResult.Should().NotBeNull();
        _earlyResetStore.GetActive(_now.UtcDateTime).Should().BeEmpty();
        _mockToastNotifier.Verify(t => t.Show(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _activityLog.RecentAlerts.Should().BeEmpty();

        // Scenario B: Cached replay from WSL shim (cached: true)
        var replayReset = _now.UtcDateTime.AddHours(5).ToString("O");
        var replayJson = BuildUsageJson("codex", 10, replayReset, cached: true, source: "cache");
        _wsl.SetCommandResult("codexbar --provider codex --format json --source cli --status",
            new WslResult { Success = true, Output = replayJson });

        var replayResult = await _service.GetUsageAsync("codex");
        replayResult.Should().NotBeNull();
        _earlyResetStore.GetActive(_now.UtcDateTime).Should().BeEmpty();
        _mockToastNotifier.Verify(t => t.Show(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _activityLog.RecentAlerts.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AppRestart_ThenFreshRead_DetectedOnce()
    {
        // 1. Session 1: App records usage 75% to cache.json and persists
        var previous = new UsageData
        {
            Provider = "claude",
            Session = new UsageWindow
            {
                ReportedPercent = 75,
                Limit = 100,
                WindowMinutes = 300,
                ResetAt = _now.UtcDateTime.AddHours(3)
            },
            FetchedAt = _now.UtcDateTime.AddMinutes(-10),
            UsageFetchOutcome = FetchOutcome.Success
        };
        _cacheService.Set("claude", previous);
        await _cacheService.SaveAsync();

        // 2. Session 2 (restart): Create fresh services pointing to same files
        var newCache = new CacheService(NullLogger<CacheService>.Instance, _settings, Path.Combine(_tempDir, "cache.json"));
        await newCache.LoadAsync();

        var newStore = new EarlyResetStore(_settings, NullLogger<EarlyResetStore>.Instance, _clock, Path.Combine(_tempDir, "early-resets.json"));
        await newStore.LoadAsync();

        var newToastNotifier = new Mock<IToastNotifier>();
        var newActivityLog = new ActivityLog();

        var newService = new CodexBarService(
            wslService: _wsl,
            cacheService: newCache,
            settingsService: _settings,
            sampleDataLoader: _sampleLoader,
            logger: NullLogger<CodexBarService>.Instance,
            timeProvider: _clock,
            activityLog: newActivityLog,
            earlyResetStore: newStore,
            toastNotifier: newToastNotifier.Object);

        // First fresh read in Session 2 drops to 12%
        var nextReset = _now.UtcDateTime.AddHours(6).ToString("O");
        var freshJson = BuildUsageJson("claude", 12, nextReset);
        _wsl.SetCommandResult("codexbar --provider claude --format json --source cli --status",
            new WslResult { Success = true, Output = freshJson });

        var result1 = await newService.GetUsageAsync("claude");
        result1.Should().NotBeNull();

        // Detected once!
        newStore.GetActive(_now.UtcDateTime).Should().ContainSingle();
        newToastNotifier.Verify(t => t.Show(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        newActivityLog.RecentAlerts.Should().ContainSingle();

        // Second fresh read in Session 2 (same 12% reading)
        var result2 = await newService.GetUsageAsync("claude");
        result2.Should().NotBeNull();

        // Still only one toast and one event
        newStore.GetActive(_now.UtcDateTime).Should().ContainSingle();
        newToastNotifier.Verify(t => t.Show(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        newActivityLog.RecentAlerts.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ToastsDisabled_EventRecordedInStore_NoToastOrAlertEntryRaised()
    {
        _settings.Settings.AlertsEnabled = false;

        var previous = new UsageData
        {
            Provider = "codex",
            Session = new UsageWindow
            {
                ReportedPercent = 85,
                ResetAt = _now.UtcDateTime.AddHours(2)
            },
            FetchedAt = _now.UtcDateTime.AddMinutes(-5),
            UsageFetchOutcome = FetchOutcome.Success
        };
        _cacheService.Set("codex", previous);

        var nextReset = _now.UtcDateTime.AddHours(5).ToString("O");
        var freshJson = BuildUsageJson("codex", 10, nextReset);
        _wsl.SetCommandResult("codexbar --provider codex --format json --source cli --status",
            new WslResult { Success = true, Output = freshJson });

        var result = await _service.GetUsageAsync("codex");
        result.Should().NotBeNull();

        // Event IS recorded in the store
        _earlyResetStore.GetActive(_now.UtcDateTime).Should().ContainSingle();

        // But NO toast notification is shown
        _mockToastNotifier.Verify(t => t.Show(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        // And NO activity alert is added
        _activityLog.RecentAlerts.Should().BeEmpty();
    }
}
