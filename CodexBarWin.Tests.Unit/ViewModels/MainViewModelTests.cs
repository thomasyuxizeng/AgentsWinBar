using System.Runtime.CompilerServices;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.ViewModels;

[TestClass]
public class MainViewModelTests
{
    private readonly Mock<ICodexBarService> _usage = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly UsageHistoryStore _history = new();
    private readonly ActivityLog _activity = new();
    private readonly Mock<IToastNotifier> _toast = new();
    private AppSettings _values = null!;
    private UsageHistoryPersistence _persistence = null!;
    private MainViewModel _viewModel = null!;
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "CodexBarWin_Refresh_" + Guid.NewGuid());
        _values = AppSettings.GetDefaults();
        _values.RefreshIntervalSeconds = 86400;
        _values.AlertsEnabled = false;
        _values.Providers = [new() { Id = "claude" }];
        _settings.SetupGet(s => s.Settings).Returns(_values);
        _settings.SetupGet(s => s.SettingsFilePath).Returns(Path.Combine(_directory, "settings.json"));
        _persistence = new(_history, _settings.Object, NullLogger<UsageHistoryPersistence>.Instance);
        _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
            NullLogger<MainViewModel>.Instance, _history, _persistence, _activity);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _viewModel.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static UsageData Data(double percent) => new() { Provider = "claude", FetchedAt = DateTime.UtcNow,
        Session = new UsageWindow { ReportedPercent = percent, WindowMinutes = 300, ResetAt = DateTime.UtcNow.AddHours(2) } };

    private static async IAsyncEnumerable<UsageData> Stream(Task gate, UsageData data,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Deliberately uncooperative: the consumer must discard stale results even if a backend ignores cancellation.
        await gate;
        yield return data;
    }

    private async Task WaitForRefreshAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (_viewModel.IsLoading) await Task.Delay(10, timeout.Token);
    }

    [TestMethod]
    public async Task StartAsync_ProviderResultsArrive_RecordsSuccessErrorAndElapsedDuration()
    {
        _values.Providers = [new() { Id = "claude" }, new() { Id = "codex" }];
        var gate = Signal();
        async IAsyncEnumerable<UsageData> Results()
        {
            yield return Data(20);
            await gate.Task;
            yield return new() { Provider = "codex", Error = "fetch failed" };
        }
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>())).Returns(() => Results());
        var before = DateTime.UtcNow;
        var refresh = _viewModel.StartAsync();
        var first = _activity.RecentRefreshes.Should().ContainSingle().Which;
        first.ProviderId.Should().Be("claude"); first.Success.Should().BeTrue(); first.Message.Should().BeNull();
        first.TimestampUtc.Should().BeOnOrAfter(before); first.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        await Task.Delay(30);
        gate.SetResult(); await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        var second = _activity.RecentRefreshes[0];
        second.ProviderId.Should().Be("codex"); second.Success.Should().BeFalse(); second.Message.Should().Be("fetch failed");
        second.DurationMs.Should().BeGreaterThan(first.DurationMs);
        _activity.RecentRefreshes.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task StartAsync_AlertShown_RecordsProviderTitleMessageAndTimestamp()
    {
        _values.AlertsEnabled = true;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(100), ct));
        var before = DateTime.UtcNow;
        await _viewModel.StartAsync();
        var alert = _activity.RecentAlerts.Should().ContainSingle().Which;
        alert.ProviderId.Should().Be("claude"); alert.Title.Should().Be("Claude session limit reached");
        alert.Message.Should().NotBeNullOrWhiteSpace(); alert.TimestampUtc.Should().BeOnOrAfter(before);
        _toast.Verify(t => t.Show(alert.Title, alert.Message), Times.Once);
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        _activity.RecentAlerts.Should().ContainSingle();
    }

    [TestMethod]
    public async Task StartAsync_ActivityLogThrows_StillCompletesRefreshAndShowsAllAlerts()
    {
        var log = new Mock<IActivityLog>();
        log.Setup(l => l.RecordRefresh(It.IsAny<DashboardRefreshEntry>())).Throws(new InvalidOperationException("log failed"));
        log.Setup(l => l.RecordAlert(It.IsAny<DashboardAlertEntry>())).Throws(new InvalidOperationException("log failed"));
        _viewModel.Dispose();
        _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
            NullLogger<MainViewModel>.Instance, _history, _persistence, log.Object);
        _values.AlertsEnabled = true;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(100) with { Weekly = new() { ReportedPercent = 100 } }, ct));
        await _viewModel.StartAsync();
        _viewModel.ErrorMessage.Should().BeNull(); _viewModel.Providers.Single().Session!.Percent.Should().Be(100);
        _toast.Verify(t => t.Show(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
        log.Verify(l => l.RecordRefresh(It.IsAny<DashboardRefreshEntry>()), Times.Once);
        log.Verify(l => l.RecordAlert(It.IsAny<DashboardAlertEntry>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task Stop_WhileResultPending_DiscardsCanceledActivity()
    {
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(100), ct));
        _values.AlertsEnabled = true;
        var refresh = _viewModel.StartAsync(); _viewModel.Stop(); gate.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        _activity.RecentRefreshes.Should().BeEmpty(); _activity.RecentAlerts.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SettingsChanged_DuringUsageRefresh_DiscardsOldResultsAndStartsLatestRefresh()
    {
        var old = Signal();
        var calls = 0;
        CancellationToken oldToken = default;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) =>
        {
            if (++calls == 1) { oldToken = ct; return Stream(old.Task, Data(90), ct); }
            return Stream(Task.CompletedTask, Data(20), ct);
        });
        var start = _viewModel.StartAsync();
        _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
        oldToken.IsCancellationRequested.Should().BeTrue();
        old.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRefreshAsync();
        calls.Should().Be(2);
        _viewModel.Providers.Single().Session!.Percent.Should().Be(20);
        _history.Samples.Should().ContainSingle().Which.UsedPercent.Should().Be(20);
        _viewModel.ErrorMessage.Should().BeNull();
    }

    [TestMethod]
    public async Task SettingsChanged_CostDisabledDuringScan_DiscardsLateCostAndKeepsUsage()
    {
        var scan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));
        _usage.Setup(s => s.GetCostAsync("claude", It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (!_values.ShowCostTotals) return Task.FromResult<CostData?>(null);
            started.TrySetResult();
            return scan.Task;
        });
        var start = _viewModel.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _values.ShowCostTotals = false;
        _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
        scan.SetResult(new CostData { Provider = "claude", Last30DaysCostUSD = 100 });
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRefreshAsync();
        _viewModel.Providers.Single().Cost.Should().BeNull();
        _viewModel.Providers.Single().Session!.Percent.Should().Be(20);
        _viewModel.ErrorMessage.Should().BeNull();
    }

    [TestMethod]
    public async Task SettingsChanged_DeveloperModeSwitchedOff_DoesNotRecordLateMockHistory()
    {
        var mock = Signal();
        var calls = 0;
        _values.DeveloperModeEnabled = true;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) =>
            ++calls == 1 ? Stream(mock.Task, Data(98), ct) : Stream(Task.CompletedTask, Data(10), ct));
        var start = _viewModel.StartAsync();
        _values.DeveloperModeEnabled = false;
        _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
        mock.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRefreshAsync();
        _history.Samples.Should().ContainSingle().Which.UsedPercent.Should().Be(10);
    }

    [TestMethod]
    public async Task Exit_DuringRefresh_CancelsAndWaitsThenFlushesHistoryBeforeExitEvent()
    {
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(90), ct));
        var now = DateTime.UtcNow;
        _history.Add(new UsageHistorySample { Provider = "claude", WindowKey = "session", Timestamp = now,
            UsedPercent = 20, ResetsAt = now.AddHours(2) }, now);
        var exited = false;
        _viewModel.ExitRequested += (_, _) =>
        {
            File.Exists(_persistence.FilePath).Should().BeTrue();
            exited = true;
        };
        var refresh = _viewModel.StartAsync();
        var exit = _viewModel.ExitCommand.ExecuteAsync(null);
        exited.Should().BeFalse();
        gate.SetResult();
        await Task.WhenAll(refresh, exit).WaitAsync(TimeSpan.FromSeconds(5));
        exited.Should().BeTrue();
        _history.Samples.Should().ContainSingle().Which.UsedPercent.Should().Be(20);
        (await File.ReadAllTextAsync(_persistence.FilePath)).Should().Contain("20");
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task StartAsync_RestartDuringRefresh_WaitsForCanceledRefreshThenFetchesAgain()
    {
        var old = Signal();
        var calls = 0;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) =>
            ++calls == 1 ? Stream(old.Task, Data(90), ct) : Stream(Task.CompletedTask, Data(20), ct));
        var first = _viewModel.StartAsync();
        var restart = _viewModel.StartAsync();
        old.SetResult();
        await Task.WhenAll(first, restart).WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Providers.Single().Session!.Percent.Should().Be(20);
        _history.Samples.Should().ContainSingle().Which.UsedPercent.Should().Be(20);
        calls.Should().Be(2);
    }

    [TestMethod]
    public async Task Stop_DuringRefresh_DiscardsLateResultsAndSettingsDoNotRestartIt()
    {
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(90), ct));
        var start = _viewModel.StartAsync();
        _viewModel.Stop();
        _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
        gate.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        _history.Samples.Should().BeEmpty();
        _viewModel.Providers.Should().AllSatisfy(p => p.IsLoading.Should().BeFalse());
        _viewModel.IsLoading.Should().BeFalse();
        _viewModel.ErrorMessage.Should().BeNull();
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
