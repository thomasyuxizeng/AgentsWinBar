using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class DashboardServiceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();
    private readonly Mock<ICodexBarService> _cli = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly UsageHistoryStore _history = new();
    private readonly ActivityLog _activity = new();
    private DashboardService _service = null!;
    private UsageHistoryPersistence _persistence = null!;
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Environment.CurrentDirectory, "CodexBarWin.Tests.Unit", "obj", "dashboard-tests-" + Guid.NewGuid());
        _settings.SetupGet(s => s.Settings).Returns(new AppSettings());
        _settings.SetupGet(s => s.SettingsFilePath).Returns(Path.Combine(_directory, "settings.json"));
        _cli.Setup(s => s.GetCostCacheInfo()).Returns(Array.Empty<DashboardCostCacheEntry>());
        _cli.Setup(s => s.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync("1.2.3");
        _cli.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _persistence = new(_history, _settings.Object, NullLogger<UsageHistoryPersistence>.Instance, _clock);
        _service = new(_settings.Object, _history, _persistence, _activity, _cli.Object, NullLogger<DashboardService>.Instance, _clock);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public void GetSnapshot_CapturedState_PassesHistoryActivityCostCacheAndClockWithoutCallingCli()
    {
        var now = _clock.Now.UtcDateTime;
        var cache = new DashboardCostCacheEntry { ProviderId = "claude", LastAttemptUtc = now, HasData = true };
        _cli.Setup(s => s.GetCostCacheInfo()).Returns(new[] { cache });
        _history.Add(new() { Timestamp = now, Provider = "claude", WindowKey = "session", UsedPercent = 20 }, now);
        var refresh = new DashboardRefreshEntry { TimestampUtc = now, ProviderId = "claude", Success = true };
        var alert = new DashboardAlertEntry { TimestampUtc = now, ProviderId = "claude", Title = "Title", Message = "Message" };
        _activity.RecordRefresh(refresh); _activity.RecordAlert(alert);
        var snapshot = _service.GetSnapshot([new() { Provider = "claude", FetchedAt = now }], now);
        snapshot.GeneratedAtUtc.Should().Be(now); snapshot.Summary.LastRefreshUtc.Should().Be(now);
        snapshot.Diagnostics.CostCache.Should().Equal(cache);
        snapshot.Diagnostics.HistorySampleCount.Should().Be(1);
        snapshot.Diagnostics.RecentRefreshes.Should().Equal(refresh); snapshot.Diagnostics.RecentAlerts.Should().Equal(alert);
        snapshot.Diagnostics.HistoryFileBytes.Should().Be(0); snapshot.Diagnostics.SettingsPath.Should().Be(_settings.Object.SettingsFilePath);
        snapshot.Diagnostics.AppVersion.Should().NotBeNullOrWhiteSpace(); snapshot.Diagnostics.WslDistro.Should().BeNull();
        snapshot.Diagnostics.CliVersion.Should().BeNull(); snapshot.Diagnostics.CliAvailable.Should().BeNull();
        _cli.Verify(s => s.GetVersionAsync(It.IsAny<CancellationToken>()), Times.Never);
        _cli.Verify(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public void GetSnapshot_HistoryFileExists_ReturnsFileSize()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(_persistence.FilePath, new byte[123]);
        _service.GetSnapshot([], null).Diagnostics.HistoryFileBytes.Should().Be(123);
    }

    [TestMethod]
    public void GetSnapshot_AdaptiveRefreshSchedule_PassesActualScheduleAndEffectiveInterval()
    {
        var now = _clock.Now.UtcDateTime;
        var next = now.AddMinutes(9);
        var snapshot = _service.GetSnapshot([], now.AddMinutes(-1), next, TimeSpan.FromMinutes(10));
        snapshot.Summary.NextRefreshUtc.Should().Be(next);
        snapshot.Summary.RefreshIntervalSeconds.Should().Be(_settings.Object.Settings.RefreshIntervalSeconds);
        snapshot.Summary.EffectiveRefreshIntervalSeconds.Should().Be(600);
        snapshot.Diagnostics.EffectiveRefreshIntervalSeconds.Should().Be(600);
    }

    [TestMethod]
    public void GetSnapshot_NoScheduledRefresh_ReturnsNullNextRefresh()
    {
        _service.GetSnapshot([], _clock.Now.UtcDateTime).Summary.NextRefreshUtc.Should().BeNull();
    }

    [TestMethod]
    public async Task RefreshDiagnosticsAsync_WithinTenMinutes_CachesFactsThenRefreshesAtExpiry()
    {
        await _service.RefreshDiagnosticsAsync();
        var snapshot = _service.GetSnapshot([], null);
        snapshot.Diagnostics.CliVersion.Should().Be("1.2.3"); snapshot.Diagnostics.CliAvailable.Should().BeTrue();
        _clock.Now = _clock.Now.AddMinutes(9).AddSeconds(59);
        await _service.RefreshDiagnosticsAsync();
        _cli.Verify(s => s.GetVersionAsync(It.IsAny<CancellationToken>()), Times.Once);
        _cli.Verify(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
        _cli.Setup(s => s.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync("2.0");
        _cli.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _clock.Now = _clock.Now.AddSeconds(1);
        await _service.RefreshDiagnosticsAsync();
        snapshot = _service.GetSnapshot([], null);
        snapshot.Diagnostics.CliVersion.Should().Be("2.0"); snapshot.Diagnostics.CliAvailable.Should().BeFalse();
        _cli.Verify(s => s.GetVersionAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _cli.Verify(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task RefreshDiagnosticsAsync_ConcurrentRequests_SharesOneFlightAndSnapshotDoesNotWait()
    {
        var version = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cli.Setup(s => s.GetVersionAsync(It.IsAny<CancellationToken>())).Returns(version.Task);
        var first = _service.RefreshDiagnosticsAsync();
        var other = _service.RefreshDiagnosticsAsync();
        other.Should().BeSameAs(first);
        _clock.Now = _clock.Now.AddMinutes(20);
        var third = _service.RefreshDiagnosticsAsync(); third.Should().BeSameAs(first);
        var snapshot = await Task.Run(() => _service.GetSnapshot([], null)).WaitAsync(TimeSpan.FromSeconds(5));
        snapshot.Diagnostics.CliVersion.Should().BeNull();
        version.SetResult("delayed");
        await Task.WhenAll(first, other, third).WaitAsync(TimeSpan.FromSeconds(5));
        await _service.RefreshDiagnosticsAsync();
        _cli.Verify(s => s.GetVersionAsync(It.IsAny<CancellationToken>()), Times.Once);
        _cli.Verify(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
        _service.GetSnapshot([], null).Diagnostics.CliVersion.Should().Be("delayed");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RefreshDiagnosticsAsync_CliThrows_CachesUnknownAndRetriesAfterBackoff(bool versionFails)
    {
        await _service.RefreshDiagnosticsAsync();
        _clock.Now = _clock.Now.AddMinutes(10);
        if (versionFails) _cli.Setup(s => s.GetVersionAsync(It.IsAny<CancellationToken>())).Throws(new InvalidOperationException("CLI failed"));
        else _cli.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("CLI failed"));
        Func<Task> refresh = () => _service.RefreshDiagnosticsAsync();
        await refresh.Should().NotThrowAsync();
        var snapshot = _service.GetSnapshot([], null);
        snapshot.Diagnostics.CliVersion.Should().BeNull(); snapshot.Diagnostics.CliAvailable.Should().BeNull();
        await _service.RefreshDiagnosticsAsync();
        _cli.Verify(s => s.GetVersionAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _clock.Now = _clock.Now.AddMinutes(10);
        await refresh.Should().NotThrowAsync();
        _cli.Verify(s => s.GetVersionAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [TestMethod]
    public async Task RefreshDiagnosticsAsync_CanceledToken_ReturnsUnknownWithoutThrowing()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Func<Task> refresh = () => _service.RefreshDiagnosticsAsync(cts.Token);
        await refresh.Should().NotThrowAsync();
        _service.GetSnapshot([], null).Diagnostics.CliAvailable.Should().BeNull();
        _cli.Verify(s => s.GetVersionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RefreshDiagnosticsAsync_CanceledAttempt_PreservesFactsAndAllowsImmediateRetry()
    {
        await _service.RefreshDiagnosticsAsync();
        _clock.Now = _clock.Now.AddMinutes(10);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await _service.RefreshDiagnosticsAsync(cancellation.Token);
        _service.GetSnapshot([], null).Diagnostics.CliVersion.Should().Be("1.2.3");
        _cli.Setup(s => s.GetVersionAsync(It.IsAny<CancellationToken>())).ReturnsAsync("retried");
        await _service.RefreshDiagnosticsAsync();
        _service.GetSnapshot([], null).Diagnostics.CliVersion.Should().Be("retried");
    }

    [TestMethod]
    public async Task GetSnapshot_ConcurrentHistoryActivityAndDiagnosticsWrites_ReturnsStableSnapshots()
    {
        var now = _clock.Now.UtcDateTime;
        var captured = _service.GetSnapshot([], null);
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                _history.Add(new() { Timestamp = now.AddSeconds(-2000 + i), Provider = "claude",
                    WindowKey = "session", UsedPercent = i % 100 }, now);
                _activity.RecordRefresh(new() { TimestampUtc = now.AddSeconds(i), DurationMs = i });
                _activity.RecordAlert(new() { TimestampUtc = now.AddSeconds(i), Title = i.ToString() });
            }
        });
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                await _service.RefreshDiagnosticsAsync();
                var snapshot = _service.GetSnapshot([], null);
                snapshot.Diagnostics.RecentRefreshes.Count.Should().BeLessThanOrEqualTo(40);
                snapshot.Diagnostics.RecentAlerts.Count.Should().BeLessThanOrEqualTo(20);
                snapshot.Diagnostics.HistorySampleCount.Should().BeGreaterThanOrEqualTo(0);
            }
        });
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(15));
        captured.Diagnostics.RecentRefreshes.Should().BeEmpty();
        captured.Diagnostics.HistorySampleCount.Should().Be(0);
        _service.GetSnapshot([], null).Diagnostics.HistorySampleCount.Should().BeGreaterThan(1000);
    }
}
