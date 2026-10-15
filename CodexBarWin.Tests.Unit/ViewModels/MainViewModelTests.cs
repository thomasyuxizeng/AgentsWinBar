using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Helpers;
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
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ISetupMemoService> _memo = new();
    private readonly Mock<IEarlyResetStore> _earlyResets = new();
    private readonly Mock<IBatterySaverMonitor> _batterySaver = new();
    private readonly Mock<Microsoft.Extensions.Logging.ILogger<MainViewModel>> _logger = new();
    private readonly FakeSessionStateSource _session = new();
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
        _clock.Setup(c => c.GetUtcNow()).Returns(() => DateTimeOffset.UtcNow);
        _persistence = new(_history, _settings.Object, NullLogger<UsageHistoryPersistence>.Instance, _clock.Object);
        _usage.Setup(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<string> providers, bool force, CancellationToken ct) => CostResults(providers, force, ct));
        _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
            _logger.Object, _history, _persistence, _activity, _session, _cache.Object, _memo.Object, _earlyResets.Object, _batterySaver.Object);
        _earlyResets.Setup(s => s.FlushAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _viewModel.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void UseDashboard(IDashboardService dashboard)
    {
        _viewModel.Dispose();
        _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
            _logger.Object, _history, _persistence, _activity, _session, _cache.Object, _memo.Object,
            dashboardService: dashboard);
        SetupImmediateUsage();
    }

    [TestMethod]
    [DataRow("stop")]
    [DataRow("dispose")]
    [DataRow("settings")]
    public async Task RefreshApiCredits_LifetimeEnds_CancelsBillingToken(string action)
    {
        var pending = Signal();
        var token = CancellationToken.None;
        var dashboard = new Mock<IDashboardService>();
        dashboard.Setup(s => s.RefreshApiCreditsAsync(true, It.IsAny<CancellationToken>()))
            .Returns((bool _, CancellationToken ct) => { token = ct; return pending.Task; });
        UseDashboard(dashboard.Object);
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        try
        {
            token.CanBeCanceled.Should().BeTrue();
            if (action == "stop") _viewModel.Stop();
            else if (action == "dispose") _viewModel.Dispose();
            else { _values.DeveloperModeEnabled = true; _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty); }
            token.IsCancellationRequested.Should().BeTrue();
        }
        finally { pending.TrySetResult(); }
    }

    [TestMethod]
    public async Task RefreshApiCredits_SettingsChangeDuringUsageCancellation_CancelsBillingImmediately()
    {
        var pending = Signal(); var usageGate = Signal(); var token = CancellationToken.None;
        var dashboard = new Mock<IDashboardService>();
        dashboard.Setup(s => s.RefreshApiCreditsAsync(true, It.IsAny<CancellationToken>()))
            .Returns((bool _, CancellationToken ct) => { token = ct; return pending.Task; });
        UseDashboard(dashboard.Object);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>())).Returns(Stream(usageGate.Task, Data(20)));
        var refresh = _viewModel.RefreshCommand.ExecuteAsync(null);
        try
        {
            _viewModel.IsLoading.Should().BeTrue();
            _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
            token.IsCancellationRequested.Should().BeTrue();
            usageGate.Task.IsCompleted.Should().BeFalse();
        }
        finally
        {
            _viewModel.Stop(); pending.TrySetResult(); usageGate.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task RefreshApiCredits_RepeatedForcedRequests_CoalescesPendingCalls()
    {
        var pending = Signal(); var second = Signal(); var calls = 0;
        var dashboard = new Mock<IDashboardService>();
        dashboard.Setup(s => s.RefreshApiCreditsAsync(true, It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref calls) == 1 ? pending.Task : CompleteSecond());
        Task CompleteSecond() { second.TrySetResult(); return Task.CompletedTask; }
        UseDashboard(dashboard.Object);
        try
        {
            for (var i = 0; i < 4; i++) await _viewModel.RefreshCommand.ExecuteAsync(null);
            calls.Should().Be(1);
        }
        finally { pending.TrySetResult(); }
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        calls.Should().Be(2);
    }

    [TestMethod]
    public async Task ExitAsync_CompletedScanLedgerWritePending_WaitsBeforeExitRequested()
    {
        _viewModel.Dispose();
        _values.ShowCostTotals = true;
        var writing = Signal(); var written = Signal(); var exited = false;
        var ledger = new Mock<ICostLedger>();
        ledger.Setup(s => s.RecordScansAsync(It.IsAny<IReadOnlyList<CostData>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(() => { writing.TrySetResult(); return written.Task; });
        var wsl = new CodexBarWin.Tests.Unit.Mocks.MockWslService();
        wsl.BatchHandler = (commands, callback, _) =>
        {
            var result = new WslResult { Success = true, Output = commands[0].Command.Contains(" cost ")
                ? """{"provider":"claude","sessionCostUSD":1,"sessionTokens":100,"last30DaysCostUSD":1,"last30DaysTokens":100,"daily":[{"date":"2026-10-15","totalCost":1,"totalTokens":100}]}"""
                : """[{"provider":"claude","usage":{"primary":{"usedPercent":20}}}]""" };
            callback!(0, result); return Task.FromResult<IReadOnlyList<WslResult>>([result]);
        };
        var service = new CodexBarService(wsl, _cache.Object, _settings.Object, new CodexBarWin.Tests.Unit.Mocks.MockSampleDataLoader(),
            NullLogger<CodexBarService>.Instance, costLedger: ledger.Object);
        _viewModel = new(service, _settings.Object, new UsageAlertEvaluator(), _toast.Object, _logger.Object,
            _history, _persistence, _activity, _session, _cache.Object, _memo.Object);
        _viewModel.ExitRequested += (_, _) => exited = true;
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        Task? exit = null;
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            exit = _viewModel.ExitCommand.ExecuteAsync(null);
            await Task.WhenAny(exit, Task.Delay(150));
            exited.Should().BeFalse(); exit.IsCompleted.Should().BeFalse();
        }
        finally { written.TrySetResult(); if (exit != null) await exit.WaitAsync(TimeSpan.FromSeconds(5)); }
        exited.Should().BeTrue();
    }
    private async IAsyncEnumerable<(string Provider, CostData? Cost)> CostResults(IReadOnlyList<string> providers,
        bool force, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var provider in providers)
            yield return (provider, await _usage.Object.GetCostAsync(provider, force, ct));
    }

    private sealed class FakeSessionStateSource : ISessionStateSource
    {
        public bool QueryLocked { get; set; }
        public bool ThrowOnQuery { get; set; }
        public bool IsLocked => ThrowOnQuery ? throw new InvalidOperationException("Query failed") : QueryLocked;
        public event EventHandler? Locked;
        public event EventHandler? Unlocked;
        public event EventHandler? Resumed;
        public void Lock() { QueryLocked = true; Locked?.Invoke(this, EventArgs.Empty); }
        public void Unlock() { QueryLocked = false; Unlocked?.Invoke(this, EventArgs.Empty); }
        public void Resume() { QueryLocked = false; Resumed?.Invoke(this, EventArgs.Empty); }
    }
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

    private Task PeriodicRefreshAsync() => (Task)typeof(MainViewModel)
        .GetMethod("RefreshCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_viewModel, [false, null])!;

    private Task CostScanCompletion(bool background = true) => ((TaskCompletionSource)typeof(MainViewModel)
        .GetField(background ? "_backgroundCostRefreshCompletion" : "_costRefreshCompletion",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_viewModel)!).Task;

    private Task RunResetScheduleAsync(DateTime reset, bool retry = false, CancellationToken ct = default) =>
        (Task)typeof(MainViewModel).GetMethod("RunPeriodicRefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_viewModel, [new ResetRefreshSchedule(TimeSpan.Zero, reset, retry), ct])!;

    [TestMethod]
    public async Task RefreshAsync_SlowBillingAndHiddenCostTotals_CompletesUsageAndForcesBilling()
    {
        _viewModel.Dispose();
        _values.ShowCostTotals = false;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dashboard = new Mock<IDashboardService>();
        dashboard.Setup(s => s.RefreshApiCreditsAsync(true, It.IsAny<CancellationToken>())).Returns(pending.Task);
        _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
            _logger.Object, _history, _persistence, _activity, _session, _cache.Object, _memo.Object,
            dashboardService: dashboard.Object);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(Stream(Task.CompletedTask, Data(40)));
        await _viewModel.RefreshCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Providers.Single().Session!.Percent.Should().Be(40);
        _viewModel.IsLoading.Should().BeFalse(); pending.Task.IsCompleted.Should().BeFalse();
        dashboard.Verify(s => s.RefreshApiCreditsAsync(true, It.IsAny<CancellationToken>()), Times.Once);
        pending.SetResult();
    }

    [TestMethod]
    public void ShouldPlayResetAnimation_BatteryAndSettingsChanges_NotifiesAndRecomputes()
    {
        var notifications = 0;
        _viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ShouldPlayResetAnimation)) notifications++; };
        _viewModel.ShouldPlayResetAnimation.Should().BeTrue();
        _batterySaver.SetupGet(m => m.BatterySaverActive).Returns(true);
        _batterySaver.Raise(m => m.BatterySaverChanged += null, EventArgs.Empty);
        _viewModel.ShouldPlayResetAnimation.Should().BeFalse();
        _values.ResetAnimationMode = ResetAnimationMode.Always;
        _viewModel.Stop(); // The preference still updates when refresh work is stopped.
        _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
        _viewModel.ShouldPlayResetAnimation.Should().BeTrue();
        _values.ResetAnimationMode = ResetAnimationMode.Never;
        _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
        _viewModel.ShouldPlayResetAnimation.Should().BeFalse();
        notifications.Should().Be(3);
        _viewModel.Dispose();
        _batterySaver.Raise(m => m.BatterySaverChanged += null, EventArgs.Empty);
        notifications.Should().Be(3);
        _batterySaver.VerifyRemove(m => m.BatterySaverChanged -= It.IsAny<EventHandler>(), Times.Once);
    }

    [TestMethod]
    public async Task RunPeriodicRefreshAsync_TargetedResult_LogsEachWindowOnceAtInformationAndActivity()
    {
        var reset = DateTime.UtcNow.AddSeconds(-2);
        var before = Data(90) with
        {
            FetchedAt = reset.AddMinutes(-1), UsageFetchOutcome = FetchOutcome.Success,
            Session = new() { ReportedPercent = 90, ResetAt = reset },
            Weekly = new() { ReportedPercent = 90, ResetAt = reset },
            Tertiary = new() { ReportedPercent = 90, ResetAt = reset.AddHours(1) }
        };
        var after = before with { FetchedAt = DateTime.UtcNow,
            Session = before.Session! with { ReportedPercent = 0, ResetAt = reset.AddHours(5) } };
        _usage.SetupSequence(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(Stream(Task.CompletedTask, before)).Returns(Stream(Task.CompletedTask, after));
        await _viewModel.StartAsync();
        await RunResetScheduleAsync(reset);
        _activity.RecentRefreshes.Should().HaveCount(2);
        var entry = _activity.RecentRefreshes.Single(e => e.Message?.StartsWith("Reset refresh:") == true);
        entry.Message!.Should().Contain("session NEW +").And.Contain("weekly OLD +").And.NotContain("tertiary");
        _logger.Verify(l => l.Log(Microsoft.Extensions.Logging.LogLevel.Information,
            It.IsAny<Microsoft.Extensions.Logging.EventId>(),
            It.Is<It.IsAnyType>((state, _) => state!.ToString()!.Contains("Reset refresh claude: session NEW +") &&
                state!.ToString()!.Contains("weekly OLD +")), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [TestMethod]
    public async Task RunPeriodicRefreshAsync_FirstAttemptStillRunning_RetryWaitsAndRemainsAvailable()
    {
        var reset = DateTime.UtcNow.AddSeconds(-2);
        var before = Data(90) with { UsageFetchOutcome = FetchOutcome.Success,
            Session = new() { ReportedPercent = 90, ResetAt = reset } };
        var gate = Signal();
        _usage.SetupSequence(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(Stream(Task.CompletedTask, before)).Returns(Stream(gate.Task, before))
            .Returns(Stream(Task.CompletedTask, before));
        await _viewModel.StartAsync();
        var first = RunResetScheduleAsync(reset);
        try
        {
            _viewModel.IsLoading.Should().BeTrue();
            _viewModel.NextRefreshUtc.Should().BeNull();
            // Simulate a 16-second first fetch without a real timer wait.
            var attempts = (Dictionary<DateTime, ResetRefreshAttempt>)typeof(MainViewModel)
                .GetField("_resetRefreshAttempts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_viewModel)!;
            attempts[reset] = new(DateTime.UtcNow.AddSeconds(-16));
            await RunResetScheduleAsync(reset, retry: true);
            attempts[reset].RetryTriggered.Should().BeFalse();
            _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally { gate.SetResult(); await first; }
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromSeconds(2));
        await RunResetScheduleAsync(reset, retry: true);
        await RunResetScheduleAsync(reset, retry: true);
        await RunResetScheduleAsync(reset);
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
        _viewModel.EffectiveRefreshInterval.Should().BeGreaterThan(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartAsync_ZeroIntervals_ClampsTimerToTwoSeconds(bool locked)
    {
        _values.RefreshIntervalSeconds = 0;
        _values.LockedRefreshIntervalMinutes = 0;
        _values.AdaptiveRefreshEnabled = false;
        _session.QueryLocked = locked;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task RunPeriodicRefreshAsync_AlreadyLoading_DoesNotFetchOrConsumeAttempt()
    {
        var reset = DateTime.UtcNow.AddSeconds(30);
        var data = Data(90) with { Session = new() { ResetAt = reset } };
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, data, ct));
        var starting = _viewModel.StartAsync();
        try
        {
            await RunResetScheduleAsync(reset);
            _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            gate.SetResult();
            await starting;
        }
        _viewModel.NextRefreshUtc.Should().Be(reset.AddSeconds(2));
    }

    [TestMethod]
    public async Task StartAsync_NearestResetInExtraWindow_PublishesActualNextRefresh()
    {
        var reset = DateTime.UtcNow.AddSeconds(30);
        var data = Data(80) with { ExtraWindows = [new() { Key = "extra", Window = new() { ResetAt = reset } }] };
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, data, ct));
        await _viewModel.StartAsync();
        _viewModel.NextRefreshUtc.Should().Be(reset.AddSeconds(2));
        _viewModel.EffectiveRefreshInterval.Should().BeLessThan(TimeSpan.FromSeconds(33));
        _viewModel.Stop();
        _viewModel.NextRefreshUtc.Should().BeNull();
    }

    [TestMethod]
    public async Task StartAsync_DisabledProviderReset_DoesNotShortenEnabledProviderSchedule()
    {
        _values.RefreshIntervalSeconds = 300;
        _values.AdaptiveRefreshEnabled = false;
        var reset = DateTime.UtcNow.AddSeconds(30);
        _values.Providers.Add(new() { Id = "codex", IsEnabled = false });
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers.Add(Data(90) with { Provider = "codex", Session = new() { ResetAt = reset } });
        typeof(MainViewModel).GetMethod("RecreateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_viewModel, [null]);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task RunPeriodicRefreshAsync_StaleReset_PerformsOneRetryThenNormalCadence()
    {
        _values.AdaptiveRefreshEnabled = false;
        var reset = DateTime.UtcNow.AddSeconds(30);
        var data = Data(90) with { Session = new() { ResetAt = reset, ReportedPercent = 90 } };
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, data, ct));
        await _viewModel.StartAsync();
        await RunResetScheduleAsync(reset);
        _viewModel.EffectiveRefreshInterval.TotalSeconds.Should().BeInRange(2, 15);
        _activity.RecentRefreshes.Should().HaveCount(2);
        await RunResetScheduleAsync(reset, retry: true);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromSeconds(_values.RefreshIntervalSeconds));
        _activity.RecentRefreshes.Should().HaveCount(3);
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [TestMethod]
    public async Task RunPeriodicRefreshAsync_CanceledTimer_DoesNotFetchOrConsumeAttempt()
    {
        var reset = DateTime.UtcNow.AddSeconds(30);
        var data = Data(90) with { Session = new() { ResetAt = reset } };
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, data, ct));
        await _viewModel.StartAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await RunResetScheduleAsync(reset, ct: cts.Token);
        _viewModel.NextRefreshUtc.Should().Be(reset.AddSeconds(2));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RunPeriodicRefreshAsync_LockWithoutEvent_UsesLockedCadenceWithoutConsumingAttempt()
    {
        var reset = DateTime.UtcNow.AddSeconds(30);
        var data = Data(90) with { Session = new() { ResetAt = reset } };
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, data, ct));
        await _viewModel.StartAsync();
        _session.QueryLocked = true;
        await RunResetScheduleAsync(reset);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromSeconds(_values.RefreshIntervalSeconds));
        _session.QueryLocked = false;
        typeof(MainViewModel).GetMethod("RecreateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_viewModel, [null]);
        _viewModel.NextRefreshUtc.Should().Be(reset.AddSeconds(2));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RunPeriodicRefreshAsync_Stopped_DoesNotFetch()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Stop();
        await RunResetScheduleAsync(DateTime.UtcNow);
        _viewModel.NextRefreshUtc.Should().BeNull();
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RefreshCoreAsync_NaturalReset_UpdatesSessionSignalAndActivityThroughNormalPath()
    {
        var now = DateTime.UtcNow;
        var previous = Data(90) with { FetchedAt = now.AddMinutes(-1), DataUpdatedAt = now.AddMinutes(-1),
            UsageFetchOutcome = FetchOutcome.Success, Session = new() { ResetAt = now.AddSeconds(-5), ReportedPercent = 90 } };
        var current = previous with { FetchedAt = now, DataUpdatedAt = now,
            Session = previous.Session! with { ResetAt = now.AddHours(5), ReportedPercent = 0 } };
        _usage.SetupSequence(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(Stream(Task.CompletedTask, previous)).Returns(Stream(Task.CompletedTask, current));
        await _viewModel.StartAsync();
        _viewModel.Providers.Single().Session!.ResetDetectedAtUtc.Should().BeNull();
        await PeriodicRefreshAsync();
        _viewModel.Providers.Single().Session!.ResetDetectedAtUtc.Should().NotBeNull();
        _activity.RecentRefreshes.Should().HaveCount(2);
    }

    private MainViewModel ViewModelWith(ResetAnimationVisibility visibility) => new(_usage.Object, _settings.Object,
        new UsageAlertEvaluator(), _toast.Object, _logger.Object, _history, _persistence, _activity, _session, _cache.Object,
        _memo.Object, _earlyResets.Object, _batterySaver.Object, resetVisibility: visibility);

    private async Task<DateTime> NaturalResetWhileAsync(ResetAnimationVisibility visibility, bool locked)
    {
        _viewModel.Dispose();
        _viewModel = ViewModelWith(visibility);
        var now = DateTime.UtcNow;
        var previous = Data(90) with { FetchedAt = now.AddMinutes(-1), DataUpdatedAt = now.AddMinutes(-1),
            UsageFetchOutcome = FetchOutcome.Success, Session = new() { ResetAt = now.AddSeconds(-5), ReportedPercent = 90 } };
        var current = previous with { FetchedAt = now, DataUpdatedAt = now,
            Session = previous.Session! with { ResetAt = now.AddHours(5), ReportedPercent = 0 } };
        _usage.SetupSequence(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(Stream(Task.CompletedTask, previous)).Returns(Stream(Task.CompletedTask, current))
            .Returns(Stream(Task.CompletedTask, current));
        await _viewModel.StartAsync();
        _session.QueryLocked = locked;
        await PeriodicRefreshAsync();
        return _viewModel.Providers.Single().Session!.ResetDetectedAtUtc!.Value;
    }

    [TestMethod]
    public async Task RefreshCoreAsync_ResetDetectedWhileLocked_ReplaysWhenTheSessionIsUnlocked()
    {
        var visibility = new ResetAnimationVisibility();
        var raised = 0;
        visibility.Resumed += (_, _) => raised++;
        var stamp = await NaturalResetWhileAsync(visibility, locked: true);
        visibility.VisibleSince(stamp).Should().BeNull();
        raised.Should().Be(0);

        _session.Unlock();

        visibility.VisibleSince(stamp).Should().NotBeNull();
        raised.Should().Be(1);
    }

    [TestMethod]
    public async Task RefreshCoreAsync_ResetDetectedWhileUnlocked_IsNotQueuedForReplay()
    {
        var visibility = new ResetAnimationVisibility();
        var raised = 0;
        visibility.Resumed += (_, _) => raised++;
        var stamp = await NaturalResetWhileAsync(visibility, locked: false);

        _session.Lock();
        _session.Unlock();

        visibility.VisibleSince(stamp).Should().BeNull();
        raised.Should().Be(0);
    }

    [TestMethod]
    public async Task HandleSessionActivation_ResumeLandsOnTheLockScreen_WaitsForTheUnlock()
    {
        var visibility = new ResetAnimationVisibility();
        var stamp = await NaturalResetWhileAsync(visibility, locked: true);

        _session.QueryLocked = true;
        typeof(MainViewModel).GetMethod("OnSessionActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_viewModel, [null, EventArgs.Empty]);
        visibility.VisibleSince(stamp).Should().BeNull();

        _session.Unlock();
        visibility.VisibleSince(stamp).Should().NotBeNull();
    }

    [TestMethod]
    public async Task StartAsync_OverQuotaHistory_RestoresLatest105WhileFetchIsPending()
    {
        var now = DateTime.UtcNow;
        _cache.Setup(c => c.GetLastKnown("claude")).Returns(Data(105) with { FetchedAt = now.AddMinutes(-10) });
        _history.Add(new() { Provider = "claude", WindowKey = "session", UsedPercent = 99, Timestamp = now.AddMinutes(-20) }, now);
        _history.Add(new() { Provider = "claude", WindowKey = "session", UsedPercent = 105, Timestamp = now.AddMinutes(-10) }, now);
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(106), ct));
        var start = _viewModel.StartAsync();
        try
        {
            _viewModel.Providers.Single().Session!.Percent.Should().Be(105);
            _viewModel.Providers.Single().UsageIsStale.Should().BeTrue();
        }
        finally
        {
            gate.SetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartAsync_WhenOnlyStaleCacheOrHistoryExists_ShowsReadingWhileFetchIsPending(bool historyOnly)
    {
        var fetchedAt = DateTime.UtcNow.AddHours(-2);
        if (historyOnly)
            _history.Add(new() { Provider = "claude", WindowKey = "session", UsedPercent = 42, Timestamp = fetchedAt }, DateTime.UtcNow);
        else
            _cache.Setup(c => c.GetLastKnown("claude")).Returns(Data(42) with { FetchedAt = fetchedAt });
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(50), ct));
        var starting = _viewModel.StartAsync();
        try
        {
            if (historyOnly)
            {
                _viewModel.Providers.Single().HasUsage.Should().BeFalse();
            }
            else
            {
                _viewModel.Providers.Single().Session!.Percent.Should().Be(42);
                _viewModel.Providers.Single().UsageIsStale.Should().BeTrue();
                _viewModel.Providers.Single().LastSuccessfulFetch.Should().Be(fetchedAt);
            }
        }
        finally { gate.TrySetResult(); await starting; }
        _viewModel.Providers.Single().Session!.Percent.Should().Be(50);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshCommand_PeriodicRefreshLoading_CancelsExistingScanAndForcesCostsAfterUsageCompletes(bool existingBackground)
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var oldScan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcedScan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcedStarted = Signal();
        var usageGate = Signal();
        CancellationToken oldToken = default;
        var forcedCalls = 0;
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) => { oldToken = ct; return oldScan.Task; });
        _usage.Setup(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) =>
            {
                if (existingBackground && ++forcedCalls == 1) { oldToken = ct; return oldScan.Task; }
                forcedStarted.TrySetResult();
                return forcedScan.Task;
            });
        using var demand = existingBackground ? null : _viewModel.AcquireCostDemand();
        if (existingBackground) await _viewModel.RefreshCommand.ExecuteAsync(null);
        var oldCompletion = CostScanCompletion(background: existingBackground);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(usageGate.Task, Data(30), ct));
        var periodic = PeriodicRefreshAsync();

        await _viewModel.RefreshCommand.ExecuteAsync(null);

        _viewModel.IsLoading.Should().BeTrue();
        oldToken.IsCancellationRequested.Should().BeTrue();
        forcedStarted.Task.IsCompleted.Should().BeFalse();
        oldScan.SetResult(new CostData { Provider = "claude", Last30DaysCostUSD = 100 });
        await oldCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Providers.Single().Cost.Should().BeNull();
        _usage.Verify(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()),
            existingBackground ? Times.Never() : Times.Once());

        usageGate.SetResult();
        await periodic.WaitAsync(TimeSpan.FromSeconds(5));
        await forcedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forcedCompletion = CostScanCompletion();
        var current = new CostData { Provider = "claude", Last30DaysCostUSD = 10 };
        forcedScan.SetResult(current);
        await forcedCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.IsLoading.Should().BeFalse();
        _viewModel.Providers.Single().Cost.Should().BeSameAs(current);
        _usage.Verify(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()),
            Times.Exactly(existingBackground ? 2 : 1));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(existingBackground ? 3 : 2));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshCommand_ForcedRequestDuringLoadingThenStopped_DoesNotStartCostScan(bool dispose)
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var usageGate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(usageGate.Task, Data(30), ct));
        var periodic = PeriodicRefreshAsync();
        await _viewModel.RefreshCommand.ExecuteAsync(null);

        if (dispose) _viewModel.Dispose();
        else _viewModel.Stop();
        usageGate.SetResult();
        await periodic.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.NextRefreshUtc.Should().BeNull();
        _usage.Verify(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RefreshCommand_BlockedForcedCosts_CompletesUsageAndRestartsTimer()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var scan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Signal();
        _usage.Setup(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) =>
            {
                started.TrySetResult();
                return scan.Task;
            });

        var refresh = _viewModel.RefreshCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completion = CostScanCompletion();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        scan.Task.IsCompleted.Should().BeFalse();
        completion.IsCompleted.Should().BeFalse();
        _viewModel.IsLoading.Should().BeFalse();
        _viewModel.Providers.Single().IsLoading.Should().BeFalse();
        _viewModel.Providers.Single().Cost.Should().BeNull();
        _viewModel.NextRefreshUtc.Should().NotBeNull();
        var current = new CostData { Provider = "claude", Last30DaysCostUSD = 10 };
        scan.SetResult(current);
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Providers.Single().Cost.Should().BeSameAs(current);
    }

    [TestMethod]
    public async Task RefreshCommand_ForcedCostsPublishedBeforeLateDemand_DiscardLateDemand()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var demandScan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcedScan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcedStarted = Signal();
        CancellationToken demandToken = default;
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) =>
            {
                demandToken = ct;
                return demandScan.Task;
            });
        var current = new CostData { Provider = "claude", Last30DaysCostUSD = 10 };
        var obsolete = new CostData { Provider = "claude", Last30DaysCostUSD = 100 };
        _usage.Setup(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()))
            .Returns(() => { forcedStarted.TrySetResult(); return forcedScan.Task; });
        using var demand = _viewModel.AcquireCostDemand();
        var demandCompletion = CostScanCompletion(background: false);
        var published = new List<CostData?>();
        void ObserveCollection() => _viewModel.Providers.CollectionChanged +=
            (_, _) => published.Add(_viewModel.Providers.Single().Cost);
        ObserveCollection();
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Providers)) ObserveCollection();
        };
        await _viewModel.RefreshCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        demandToken.IsCancellationRequested.Should().BeTrue();
        _viewModel.IsLoading.Should().BeFalse();
        // The bounded handoff must allow a forced scan to proceed while an uncooperative demand scan is blocked.
        await forcedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forcedCompletion = CostScanCompletion();
        forcedScan.SetResult(current);
        await forcedCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        demandScan.Task.IsCompleted.Should().BeFalse();
        _viewModel.Providers.Single().Cost.Should().BeSameAs(current);
        demandScan.SetResult(obsolete);
        await demandCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Providers.Single().Cost.Should().BeSameAs(current);
        published.Should().Contain(current).And.NotContain(obsolete);
    }

    [TestMethod]
    public async Task RefreshCommand_SecondForcedScanSupersedesFirst_DiscardsLateFirstResult()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var firstScan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondScan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = Signal();
        var secondStarted = Signal();
        CancellationToken firstToken = default;
        CancellationToken secondToken = default;
        var calls = 0;
        _usage.Setup(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) =>
            {
                if (++calls == 1)
                {
                    firstToken = ct;
                    firstStarted.TrySetResult();
                    return firstScan.Task;
                }
                secondToken = ct;
                secondStarted.TrySetResult();
                return secondScan.Task;
            });
        await _viewModel.RefreshCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstCompletion = CostScanCompletion();
        firstScan.Task.IsCompleted.Should().BeFalse();
        await _viewModel.RefreshCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondCompletion = CostScanCompletion();
        firstToken.IsCancellationRequested.Should().BeTrue();
        secondToken.IsCancellationRequested.Should().BeFalse();
        _viewModel.IsLoading.Should().BeFalse();
        var published = new List<CostData?>();
        _viewModel.Providers.CollectionChanged += (_, _) => published.Add(_viewModel.Providers.Single().Cost);
        var current = new CostData { Provider = "claude", Last30DaysCostUSD = 20 };
        var obsolete = new CostData { Provider = "claude", Last30DaysCostUSD = 100 };
        secondScan.SetResult(current);
        await secondCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Providers.Single().Cost.Should().BeSameAs(current);
        firstScan.SetResult(obsolete);
        await firstCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Providers.Single().Cost.Should().BeSameAs(current);
        published.Should().Contain(current).And.NotContain(obsolete);
        _usage.Verify(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private void SetupImmediateUsage() => _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
        .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));

    [TestMethod]
    [DataRow(30, 30)]
    [DataRow(0, 5)]
    public async Task IsLocked_WithoutLockEvent_UsesConfiguredMinimum(int lockedMinutes, int expectedMinutes)
    {
        _values.RefreshIntervalSeconds = 300;
        _values.AdaptiveRefreshEnabled = false;
        _values.LockedRefreshIntervalMinutes = lockedMinutes;
        _session.QueryLocked = true;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(expectedMinutes), TimeSpan.FromSeconds(2));
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [TestMethod]
    public async Task RecreateTimer_FirstDelayWhileQueryLocked_StretchesDelayWithoutLockEvent()
    {
        _values.RefreshIntervalSeconds = 300;
        _session.QueryLocked = true;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        typeof(MainViewModel).GetMethod("RecreateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_viewModel, [TimeSpan.FromMinutes(2)]);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(30));
    }

    [TestMethod]
    public async Task LockedSession_UnlockedWithoutEvent_RefreshesStaleDataAndRestoresNormalDelay()
    {
        _values.RefreshIntervalSeconds = 300;
        _values.AdaptiveRefreshEnabled = false;
        _session.QueryLocked = true;
        _viewModel.UnlockPollInterval = TimeSpan.FromMilliseconds(30);
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(30));
        _viewModel.LastUpdated = DateTime.Now.AddMinutes(-10);
        _session.QueryLocked = false; // no Unlocked event is raised
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_viewModel.EffectiveRefreshInterval != TimeSpan.FromMinutes(5) && DateTime.UtcNow < deadline) await Task.Delay(20);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [TestMethod]
    public async Task PeriodicTimer_MissedUnlockEvent_ReturnsToNormalDelayAndClearsFlag()
    {
        _values.RefreshIntervalSeconds = 300;
        _values.AdaptiveRefreshEnabled = false;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _session.QueryLocked = true;
        _session.Lock();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(30));
        _session.QueryLocked = false; // Windows unlocked, but no Unlocked event arrived.
        await (Task)typeof(MainViewModel).GetMethod("RunPeriodicRefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_viewModel, [new ResetRefreshSchedule(TimeSpan.Zero), CancellationToken.None])!;
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        typeof(MainViewModel).GetField("_sessionLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_viewModel).Should().Be(false);
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IsLocked_UnknownOrThrowingQuery_DoesNotStretch(bool throws)
    {
        _values.RefreshIntervalSeconds = 300;
        _values.AdaptiveRefreshEnabled = false;
        _session.ThrowOnQuery = throws;
        // ISessionStateSource represents unknown native flags as false.
        _session.QueryLocked = false;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    [DataRow(30, 30)]
    [DataRow(0, 5)]
    public async Task SessionLocked_RefreshDelay_UsesConfiguredMinimum(int lockedMinutes, int expectedMinutes)
    {
        _values.RefreshIntervalSeconds = 300;
        _values.LockedRefreshIntervalMinutes = lockedMinutes;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _session.Lock();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(expectedMinutes), TimeSpan.FromSeconds(2));
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [TestMethod]
    public async Task SessionLocked_RefreshInProgress_PreservesFetchAndSchedulesLockedDelay()
    {
        _values.RefreshIntervalSeconds = 300;
        var gate = Signal();
        CancellationToken token = default;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) =>
        { token = ct; return Stream(gate.Task, Data(20), ct); });
        var start = _viewModel.StartAsync();
        _session.Lock();
        token.IsCancellationRequested.Should().BeFalse();
        gate.SetResult();
        await start;
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(30));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SessionUnlocked_FreshOrStaleData_RefreshesOnlyStale(bool stale)
    {
        _values.RefreshIntervalSeconds = 300;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        await PeriodicRefreshAsync();
        await PeriodicRefreshAsync();
        _session.Lock();
        if (stale) _viewModel.LastUpdated = DateTime.Now.AddMinutes(-6);
        _session.Unlock();
        await WaitForRefreshAsync();
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Exactly(stale ? 4 : 3));
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task SessionResumed_FreshData_CancelsAndRearmsPendingTimer()
    {
        _values.RefreshIntervalSeconds = 300;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _session.Lock();
        var oldToken = ((CancellationTokenSource)typeof(MainViewModel).GetField("_timerCts",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_viewModel)!).Token;
        _session.Resume();
        oldToken.IsCancellationRequested.Should().BeTrue();
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(2));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _actions = new();
        public override void Post(SendOrPostCallback d, object? state) => _actions.Enqueue(() => d(state));
        public void Drain()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { while (_actions.TryDequeue(out var action)) action(); }
            finally { SetSynchronizationContext(previous); }
        }
    }

    [TestMethod]
    public async Task SessionEvents_BackgroundThread_ApplyOnlyOnCapturedUiContext()
    {
        _viewModel.Dispose();
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
                NullLogger<MainViewModel>.Instance, _history, _persistence, _activity, _session, _cache.Object, _memo.Object);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        _values.RefreshIntervalSeconds = 300;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        await Task.Run(_session.Lock);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        context.Drain();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(30));
        await Task.Run(_session.Unlock);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(30));
        context.Drain();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        var oldNext = _viewModel.NextRefreshUtc;
        await Task.Run(_session.Resume);
        _viewModel.NextRefreshUtc.Should().Be(oldNext);
        context.Drain();
        _viewModel.NextRefreshUtc.Should().BeOnOrAfter(oldNext!.Value);
        _viewModel.Dispose();
        await Task.Run(_session.Lock);
        context.Drain();
        _viewModel.NextRefreshUtc.Should().BeNull();
    }

    [TestMethod]
    public async Task StartAsync_AllEnabledCachesFresh_HydratesAndSchedulesOldestRemainingDelay()
    {
        _values.RefreshIntervalSeconds = 300;
        _values.Providers = [new() { Id = "claude", Order = 1 }, new() { Id = "codex", Order = 0 }];
        var oldest = DateTime.UtcNow.AddMinutes(-2);
        _cache.Setup(c => c.Get("claude")).Returns(Data(20) with { FetchedAt = oldest });
        _cache.Setup(c => c.Get("codex")).Returns(new UsageData { Provider = "codex", FetchedAt = DateTime.UtcNow.AddMinutes(-1) });
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers.Select(p => p.Provider).Should().Equal("codex", "claude");
        _viewModel.LastUpdated.Should().Be(oldest.ToLocalTime());
        _viewModel.EffectiveRefreshInterval.Should().BeCloseTo(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2));
        _viewModel.NextRefreshUtc.Should().BeCloseTo(oldest.AddMinutes(5), TimeSpan.FromSeconds(2));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Never);
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("partial")]
    [DataRow("old")]
    [DataRow("developer")]
    [DataRow("future")]
    public async Task StartAsync_CacheIncompleteOldOrDeveloper_FetchesImmediately(string mode)
    {
        _values.RefreshIntervalSeconds = 300;
        _values.Providers = [new() { Id = "claude" }, new() { Id = "codex" }];
        _values.DeveloperModeEnabled = mode == "developer";
        _cache.Setup(c => c.Get("claude")).Returns(Data(20));
        if (mode != "partial") _cache.Setup(c => c.Get("codex")).Returns(new UsageData
        { Provider = "codex", FetchedAt = DateTime.UtcNow.AddMinutes(mode == "old" ? -5 : mode == "future" ? 1 : -1) });
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RefreshCoreAsync_UsageStreamCompleted_LeavesCachePersistenceToTheService()
    {
        SetupImmediateUsage();
        _cache.Setup(c => c.SaveAsync()).ThrowsAsync(new IOException("cache unavailable"));
        await _viewModel.StartAsync();
        // CodexBarService saves the cache when its stream ends; saving the same data again is a wasted disk write
        _cache.Verify(c => c.SaveAsync(), Times.Never);
        _viewModel.ErrorMessage.Should().BeNull();
        _viewModel.LastUpdated.Should().NotBe(default);
    }

    [TestMethod]
    public async Task RefreshCoreAsync_ProviderCommandNotFound_InvalidatesSetupMemo()
    {
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask,
                Data(20) with { Error = "command not found", CommandNotFound = true }, ct));
        await _viewModel.StartAsync();
        _memo.Verify(m => m.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task AcquireCostDemand_RetainedCostAlreadyPresent_WaitsForUsageRefresh()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers[0] = _viewModel.Providers[0] with
        { Cost = new CostData { Provider = "claude", FetchedAt = DateTime.UtcNow.AddHours(-2) } };
        using var demand = _viewModel.AcquireCostDemand();
        _usage.Verify(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), false, It.IsAny<CancellationToken>()), Times.Never);
        await PeriodicRefreshAsync();
        _usage.Verify(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task PeriodicRefresh_SessionLockedWithCostDemand_SkipsCostScanUntilUnlocked()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers[0] = _viewModel.Providers[0] with
        { Cost = new CostData { Provider = "claude", FetchedAt = DateTime.UtcNow.AddHours(-2) } };
        using var demand = _viewModel.AcquireCostDemand();
        _session.QueryLocked = true;
        await PeriodicRefreshAsync();
        // A window left open behind the lock screen must not boot the WSL VM for a cost scan
        _usage.Verify(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), false, It.IsAny<CancellationToken>()), Times.Never);
        _session.QueryLocked = false;
        await PeriodicRefreshAsync();
        _usage.Verify(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RefreshAsync_BillingWithoutVisibleDashboard_PollsOnlyOnceTheDashboardBecomesVisible()
    {
        _viewModel.Dispose();
        var dashboard = new Mock<IDashboardService>();
        dashboard.Setup(s => s.RefreshApiCreditsAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
            _logger.Object, _history, _persistence, _activity, _session, _cache.Object, _memo.Object,
            dashboardService: dashboard.Object);
        SetupImmediateUsage();
        await PeriodicRefreshAsync();
        dashboard.Verify(s => s.RefreshApiCreditsAsync(false, It.IsAny<CancellationToken>()), Times.Never);

        using var visibility = _viewModel.AcquireDashboardVisibility();
        dashboard.Verify(s => s.RefreshApiCreditsAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        await PeriodicRefreshAsync();
        dashboard.Verify(s => s.RefreshApiCreditsAsync(false, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task RefreshAsync_BillingWhileSessionLocked_PollsAgainOnlyAfterTheUnlock()
    {
        _viewModel.Dispose();
        var dashboard = new Mock<IDashboardService>();
        dashboard.Setup(s => s.RefreshApiCreditsAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
            _logger.Object, _history, _persistence, _activity, _session, _cache.Object, _memo.Object,
            dashboardService: dashboard.Object);
        SetupImmediateUsage();
        using var visibility = _viewModel.AcquireDashboardVisibility();
        dashboard.Verify(s => s.RefreshApiCreditsAsync(false, It.IsAny<CancellationToken>()), Times.Once);

        _session.QueryLocked = true;
        await PeriodicRefreshAsync();
        dashboard.Verify(s => s.RefreshApiCreditsAsync(false, It.IsAny<CancellationToken>()), Times.Once);

        _session.QueryLocked = false;
        await PeriodicRefreshAsync();
        dashboard.Verify(s => s.RefreshApiCreditsAsync(false, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public void BatterySaverChanged_RefreshIntervalBelowCap_StretchesAndRestoresTheEffectiveInterval()
    {
        _values.RefreshIntervalSeconds = 300;
        _batterySaver.SetupGet(m => m.BatterySaverActive).Returns(true);
        _batterySaver.Raise(m => m.BatterySaverChanged += null, EventArgs.Empty);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromSeconds(600));
        _batterySaver.SetupGet(m => m.BatterySaverActive).Returns(false);
        _batterySaver.Raise(m => m.BatterySaverChanged += null, EventArgs.Empty);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromSeconds(300));
    }

    [TestMethod]
    public async Task BatterySaverChanged_RefreshAlreadyPending_KeepsTheTimeAlreadyWaited()
    {
        _values.RefreshIntervalSeconds = 300;
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var scheduled = _viewModel.NextRefreshUtc!.Value;
        await Task.Delay(30);

        _batterySaver.SetupGet(m => m.BatterySaverActive).Returns(true);
        _batterySaver.Raise(m => m.BatterySaverChanged += null, EventArgs.Empty);

        // The interval doubles, but the pending refresh counts from when it was scheduled, not from the toggle
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromSeconds(600));
        _viewModel.NextRefreshUtc.Should().Be(scheduled + TimeSpan.FromSeconds(300));
    }

    [TestMethod]
    public async Task BatterySaverChanged_LiftedWhileRefreshPending_PullsTheRefreshBackWithoutRestartingTheWait()
    {
        _values.RefreshIntervalSeconds = 300;
        _batterySaver.SetupGet(m => m.BatterySaverActive).Returns(true);
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var scheduled = _viewModel.NextRefreshUtc!.Value;
        await Task.Delay(30);

        _batterySaver.SetupGet(m => m.BatterySaverActive).Returns(false);
        _batterySaver.Raise(m => m.BatterySaverChanged += null, EventArgs.Empty);

        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromSeconds(300));
        _viewModel.NextRefreshUtc.Should().Be(scheduled - TimeSpan.FromSeconds(300));
    }

    [TestMethod]
    public async Task AcquireCostDemand_RetainedCostPresent_DoesNotRequestScanOnReopen()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers[0] = _viewModel.Providers[0] with
        { Cost = new CostData { Provider = "claude", TodayCostUSD = 10, FetchedAt = DateTime.UtcNow,
            LastSuccessfulFetch = DateTime.UtcNow.AddDays(-8) } };
        using var demand = _viewModel.AcquireCostDemand();
        _usage.Verify(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), false, It.IsAny<CancellationToken>()), Times.Never);
        _viewModel.Providers.Single().Cost!.TodayCostUSD.Should().Be(10);
    }

    [TestMethod]
    public async Task RefreshCoreAsync_OldAttachedCost_RetainsItDuringUsageRefresh()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers[0] = _viewModel.Providers[0] with
        { Cost = new CostData { Provider = "claude", TodayCostUSD = 10, FetchedAt = DateTime.UtcNow.AddDays(-8) } };
        await PeriodicRefreshAsync();
        _viewModel.Providers.Single().Cost.Should().NotBeNull();
        _viewModel.Providers.Single().Cost!.TodayCostUSD.Should().Be(10);
        _usage.Verify(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), false, It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task AcquireCostDemand_MixedCosts_ScansOnlyProviderWithNoCost()
    {
        _values.Providers = [new() { Id = "claude" }, new() { Id = "codex" }];
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers[0] = _viewModel.Providers[0] with { Cost = new CostData { Provider = "claude" } };
        using var demand = _viewModel.AcquireCostDemand();
        _usage.Verify(s => s.GetCostsAsync(It.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == "codex"),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task StartAsync_WithoutCostDemand_SkipsCostLoop()
    {
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));
        await _viewModel.StartAsync();
        await PeriodicRefreshAsync();
        _usage.Verify(s => s.GetCostAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RefreshCoreAsync_WithCostDemand_RunsCostLoop()
    {
        var data = Data(20);
        var cost = new CostData { Provider = "claude", Last30DaysCostUSD = 5 };
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, data, ct));
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>())).ReturnsAsync(cost);
        await _viewModel.StartAsync();
        using var demand = _viewModel.AcquireCostDemand();
        await PeriodicRefreshAsync();
        _usage.Verify(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _usage.Verify(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), false, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _viewModel.Providers.Single().Cost.Should().Be(cost);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RefreshCoreAsync_DemandReleasedDuringCostScan_CompletesUsageAndPreservesCosts(bool honorsCancellation)
    {
        var historyNow = DateTimeOffset.UtcNow;
        _clock.Setup(c => c.GetUtcNow()).Returns(() => historyNow);
        _values.RefreshIntervalSeconds = 300;
        var data = Data(20);
        var previous = new CostData { Provider = "claude", Last30DaysCostUSD = 5 };
        var scan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Signal();
        CancellationToken usageToken = default;
        CancellationToken scanToken = default;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => { usageToken = ct; return Stream(Task.CompletedTask, data, ct); });
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) =>
            {
                scanToken = ct;
                started.TrySetResult();
                return honorsCancellation ? scan.Task.WaitAsync(ct) : scan.Task;
            });
        await _viewModel.StartAsync();
        historyNow += TimeSpan.FromMinutes(2);
        _viewModel.Providers[0] = _viewModel.Providers[0] with { Cost = previous };
        using var demand = _viewModel.AcquireCostDemand();
        data = data with { Session = data.Session! with { ReportedPercent = 30 } };
        _viewModel.LastUpdated = DateTime.Now.AddHours(-1);
        var before = DateTime.Now;
        var refresh = PeriodicRefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var costCompletion = CostScanCompletion();

        demand.Dispose();
        scanToken.IsCancellationRequested.Should().BeTrue();
        usageToken.IsCancellationRequested.Should().BeFalse();
        scan.TrySetResult(new CostData { Provider = "claude", Last30DaysCostUSD = 100 });
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        await costCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.Providers.Single().Cost.Should().Be(previous);
        _viewModel.Providers.Single().Session!.Percent.Should().Be(30);
        _viewModel.Providers.Single().IsLoading.Should().BeFalse();
        _viewModel.IsLoading.Should().BeFalse();
        _viewModel.ErrorMessage.Should().BeNull();
        _viewModel.LastUpdated.Should().BeOnOrAfter(before);
        _history.Samples.Should().Contain(s => s.UsedPercent == 30);
        var savedSamples = JsonSerializer.Deserialize(await File.ReadAllTextAsync(_persistence.FilePath),
            AppJsonSerializerContext.Default.ListUsageHistorySample);
        savedSamples.Should().Contain(s => s.UsedPercent == 30);
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(2));
        // Canceled costs must still publish the usage fingerprint for subsequent adaptive refreshes.
        await PeriodicRefreshAsync();
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(10));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopOrDispose_DuringPeriodicCostScan_CancelsBackgroundScanAndDiscardsLateCosts(bool dispose)
    {
        var scan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Signal();
        var previous = new CostData { Provider = "claude", Last30DaysCostUSD = 5 };
        CancellationToken scanToken = default;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) =>
            {
                scanToken = ct;
                started.TrySetResult();
                return scan.Task;
            });
        await _viewModel.StartAsync();
        _viewModel.Providers[0] = _viewModel.Providers[0] with { Cost = previous };
        using var demand = _viewModel.AcquireCostDemand();
        var refresh = PeriodicRefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        var costCompletion = CostScanCompletion();
        var lastUpdated = _viewModel.LastUpdated;
        _viewModel.NextRefreshUtc.Should().NotBeNull();
        _viewModel.IsLoading.Should().BeFalse();
        var historyCount = _history.Samples.Count;
        var activityCount = _activity.RecentRefreshes.Count;
        var publicationsAfterStop = 0;
        _viewModel.Providers.CollectionChanged += (_, _) => publicationsAfterStop++;

        if (dispose) _viewModel.Dispose();
        else _viewModel.Stop();
        scanToken.IsCancellationRequested.Should().BeTrue();
        scan.SetResult(new CostData { Provider = "claude", Last30DaysCostUSD = 100 });
        await costCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        publicationsAfterStop.Should().Be(0);
        _viewModel.Providers.Single().Cost.Should().Be(previous);
        _viewModel.LastUpdated.Should().Be(lastUpdated);
        _history.Samples.Count.Should().Be(historyCount);
        _activity.RecentRefreshes.Count.Should().Be(activityCount);
        _viewModel.NextRefreshUtc.Should().BeNull();
        _viewModel.IsLoading.Should().BeFalse();
        _viewModel.ErrorMessage.Should().BeNull();
    }

    [TestMethod]
    public async Task RefreshCommand_DemandReleasedDuringSeparateScan_OnlyCancelsDemandedCosts()
    {
        var demandedScan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcedScan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcedStarted = Signal();
        var current = new CostData { Provider = "claude", Last30DaysCostUSD = 10 };
        CancellationToken demandedToken = default;
        CancellationToken forcedToken = default;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) =>
            {
                demandedToken = ct;
                return demandedScan.Task;
            });
        _usage.Setup(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) =>
            {
                forcedToken = ct;
                forcedStarted.TrySetResult();
                return forcedScan.Task;
            });
        await _viewModel.StartAsync();
        using var demand = _viewModel.AcquireCostDemand();
        var demandedCompletion = CostScanCompletion(background: false);
        var refresh = _viewModel.RefreshCommand.ExecuteAsync(null);
        await forcedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forcedCompletion = CostScanCompletion();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.IsLoading.Should().BeFalse();

        demand.Dispose();
        demandedToken.IsCancellationRequested.Should().BeTrue();
        forcedToken.IsCancellationRequested.Should().BeFalse();
        demandedScan.SetResult(new CostData { Provider = "claude", Last30DaysCostUSD = 100 });
        forcedScan.SetResult(current);
        await Task.WhenAll(demandedCompletion, forcedCompletion).WaitAsync(TimeSpan.FromSeconds(5));

        _viewModel.Providers.Single().Cost.Should().Be(current);
        _viewModel.IsLoading.Should().BeFalse();
        _viewModel.ErrorMessage.Should().BeNull();
    }

    [TestMethod]
    public async Task RefreshCommand_WithoutCostDemand_ForcesCostLoop()
    {
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));
        await _viewModel.StartAsync();
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        _usage.Verify(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RefreshCoreAsync_UnchangedUsage_LengthensActualScheduledDelayAndResetsOnChange()
    {
        _values.RefreshIntervalSeconds = 300;
        var data = Data(20);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, data, ct));
        await _viewModel.StartAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(10));
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(2));
        await PeriodicRefreshAsync();
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(15));
        data = data with { Session = data.Session! with { ReportedPercent = 30 } };
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task RefreshCommand_UnchangedUsage_ResetsAdaptiveDelay()
    {
        _values.RefreshIntervalSeconds = 300;
        var data = Data(20);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, data, ct));
        await _viewModel.StartAsync();
        await PeriodicRefreshAsync();
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(10));
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_RefreshAlreadyRunning_ReusesRefreshAndDoesNotForceCost()
    {
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(20), ct));
        var start = _viewModel.StartAsync();
        var visible = _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));
        gate.SetResult();
        await Task.WhenAll(start, visible).WaitAsync(TimeSpan.FromSeconds(5));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
        _usage.Verify(s => s.GetCostAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_FreshData_SkipsRefresh()
    {
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));
        await _viewModel.StartAsync();
        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_DataNewerThanBaseInterval_SkipsRefresh()
    {
        _values.RefreshIntervalSeconds = 300;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));
        await _viewModel.StartAsync();
        _viewModel.LastUpdated = DateTime.Now.AddMinutes(-3);
        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_StaleData_ResetsBackoffWithoutForcingCosts()
    {
        _values.RefreshIntervalSeconds = 300;
        var data = Data(20);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, data, ct));
        await _viewModel.StartAsync();
        await PeriodicRefreshAsync();
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(10));
        _viewModel.LastUpdated = DateTime.Now.AddMinutes(-6);
        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(2));
        _usage.Verify(s => s.GetCostAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task SettingsChanged_UnchangedUsage_ResetsAdaptiveDelay()
    {
        _values.RefreshIntervalSeconds = 300;
        var data = Data(20);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, data, ct));
        await _viewModel.StartAsync();
        await PeriodicRefreshAsync();
        await PeriodicRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(10));
        _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
        await WaitForRefreshAsync();
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task AcquireCostDemand_SlowCostScan_DoesNotHoldUsageRefresh()
    {
        var scan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>())).Returns(scan.Task);
        await _viewModel.StartAsync();
        using var demand = _viewModel.AcquireCostDemand();
        await PeriodicRefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.IsLoading.Should().BeFalse();
        scan.SetResult(new CostData { Provider = "claude", Last30DaysCostUSD = 10 });
    }

    [TestMethod]
    public async Task AcquireCostDemand_ReopenedDuringCanceledScan_RetriesWithoutPublishingCanceledTotals()
    {
        var firstScan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = Signal();
        var calls = 0;
        var current = new CostData { Provider = "claude", Last30DaysCostUSD = 10 };
        CancellationToken firstToken = default;
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(Task.CompletedTask, Data(20), ct));
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) =>
            {
                if (++calls == 1) { firstToken = ct; return firstScan.Task; }
                _viewModel.Providers.Single().Cost.Should().BeNull();
                retry.TrySetResult();
                return Task.FromResult<CostData?>(current);
            });
        await _viewModel.StartAsync();
        var firstDemand = _viewModel.AcquireCostDemand();
        firstDemand.Dispose();
        firstDemand.Dispose();
        firstToken.IsCancellationRequested.Should().BeTrue();
        using var reopened = _viewModel.AcquireCostDemand();
        firstScan.SetResult(new CostData { Provider = "claude", Last30DaysCostUSD = 100 });
        await retry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (_viewModel.Providers.Single().Cost != current) await Task.Delay(10, timeout.Token);
        calls.Should().Be(2);
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
            NullLogger<MainViewModel>.Instance, _history, _persistence, log.Object, _session, _cache.Object, _memo.Object);
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
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        using var demand = _viewModel.AcquireCostDemand();
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
    public async Task Exit_DuringRefresh_WaitsForHistoryAndEarlyResetFlushBeforeExitEvent()
    {
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(90), ct));
        var now = DateTime.UtcNow;
        _history.Add(new UsageHistorySample { Provider = "claude", WindowKey = "session", Timestamp = now,
            UsedPercent = 20, ResetsAt = now.AddHours(2) }, now);
        var exited = false;
        var resetFlush = Signal();
        _earlyResets.Setup(s => s.FlushAsync(It.IsAny<CancellationToken>())).Returns(resetFlush.Task);
        _viewModel.ExitRequested += (_, _) =>
        {
            File.Exists(_persistence.FilePath).Should().BeTrue();
            exited = true;
        };
        var refresh = _viewModel.StartAsync();
        var exit = _viewModel.ExitCommand.ExecuteAsync(null);
        exited.Should().BeFalse();
        gate.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        exited.Should().BeFalse();
        resetFlush.SetResult();
        await Task.WhenAll(refresh, exit).WaitAsync(TimeSpan.FromSeconds(5));
        exited.Should().BeTrue();
        _history.Samples.Should().ContainSingle().Which.UsedPercent.Should().Be(20);
        (await File.ReadAllTextAsync(_persistence.FilePath)).Should().Contain("20");
        _earlyResets.Verify(s => s.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
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

    [TestMethod]
    public async Task StartAsync_StopWhileRestartAwaitsOldRefresh_DoesNotRestartScheduler()
    {
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(90), ct));
        var first = _viewModel.StartAsync();
        var restart = _viewModel.StartAsync();
        _viewModel.Stop();
        gate.SetResult();
        await Task.WhenAll(first, restart).WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.NextRefreshUtc.Should().BeNull();
        _history.Samples.Should().BeEmpty();
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SettingsChanged_BackgroundThread_AppliesOnlyOnCapturedUiContext()
    {
        _viewModel.Dispose();
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
                NullLogger<MainViewModel>.Instance, _history, _persistence, _activity, _session, _cache.Object, _memo.Object);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var cost = new CostData { Provider = "claude", Last30DaysCostUSD = 10 };
        _viewModel.Providers[0] = _viewModel.Providers[0] with { Cost = cost };
        _values.ShowCostTotals = false;
        await Task.Run(() => _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty));
        _viewModel.Providers[0].Cost.Should().BeSameAs(cost);
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
        context.Drain();
        _viewModel.Providers[0].Cost.Should().BeNull();
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _viewModel.Stop();
        // Cancellation unwinds awaited cache persistence on the captured context.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (_viewModel.IsLoading) { context.Drain(); await Task.Delay(10, timeout.Token); }
    }

    [TestMethod]
    public async Task StartAsync_FreshCacheAfterFailedLoad_ClearsPreviousError()
    {
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Throws(new IOException("fetch failed"));
        await _viewModel.StartAsync();
        _viewModel.ErrorMessage.Should().NotBeNull();
        _cache.Setup(c => c.Get("claude")).Returns(Data(20));
        await _viewModel.StartAsync();
        _viewModel.ErrorMessage.Should().BeNull();
        _viewModel.Providers.Should().ContainSingle().Which.HasError.Should().BeFalse();
        _viewModel.NextRefreshUtc.Should().NotBeNull();
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task StartAsync_FreshCacheWithExistingCostDemand_LoadsMissingCosts()
    {
        _viewModel.Stop();
        _cache.Setup(c => c.Get("claude")).Returns(Data(20));
        var cost = new CostData { Provider = "claude", Last30DaysCostUSD = 10 };
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cost);
        using var demand = _viewModel.AcquireCostDemand();
        await _viewModel.StartAsync();
        _viewModel.Providers.Single().Cost.Should().BeSameAs(cost);
        _usage.Verify(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()), Times.Once);
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RefreshCommand_DemandScanStillRunning_WaitsBeforeForcingAndDiscardsOldTotals()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var scan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken demandToken = default;
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) => { demandToken = ct; return scan.Task; });
        var current = new CostData { Provider = "claude", Last30DaysCostUSD = 10 };
        _usage.Setup(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>())).ReturnsAsync(current);
        using var demand = _viewModel.AcquireCostDemand();
        var demandedCompletion = CostScanCompletion(background: false);
        var refresh = _viewModel.RefreshCommand.ExecuteAsync(null);
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        var forcedCompletion = CostScanCompletion();
        demandToken.IsCancellationRequested.Should().BeTrue();
        _viewModel.IsLoading.Should().BeFalse();
        _usage.Verify(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()), Times.Never);
        scan.SetResult(new CostData { Provider = "claude", Last30DaysCostUSD = 100 });
        await Task.WhenAll(demandedCompletion, forcedCompletion).WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Providers.Single().Cost.Should().BeSameAs(current);
        _usage.Verify(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()), Times.Once);
        _usage.Verify(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RefreshCommand_StopWhileWaitingForDemandScan_CompletesWithoutStartingForcedScan()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        var scan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken demandToken = default;
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>()))
            .Returns((string provider, bool force, CancellationToken ct) => { demandToken = ct; return scan.Task; });
        using var demand = _viewModel.AcquireCostDemand();
        var refresh = _viewModel.RefreshCommand.ExecuteAsync(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!demandToken.IsCancellationRequested) await Task.Delay(10, timeout.Token);
        _viewModel.Stop();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.NextRefreshUtc.Should().BeNull();
        _usage.Verify(s => s.GetCostAsync("claude", true, It.IsAny<CancellationToken>()), Times.Never);
        scan.SetResult(null);
    }

    [TestMethod]
    public async Task SessionResumed_UsageRefreshInProgress_KeepsNextRefreshUnsetUntilCompletion()
    {
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(20), ct));
        var refresh = _viewModel.StartAsync();
        _session.Resume();
        _viewModel.IsLoading.Should().BeTrue();
        _viewModel.NextRefreshUtc.Should().BeNull();
        gate.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.NextRefreshUtc.Should().NotBeNull();
    }

    [TestMethod]
    public async Task SettingsChanged_UsageRefreshInProgress_KeepsNextRefreshUnsetUntilCompletion()
    {
        var gate = Signal();
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Stream(gate.Task, Data(20), ct));
        var refresh = _viewModel.StartAsync();
        _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
        _viewModel.NextRefreshUtc.Should().BeNull();
        _viewModel.Stop();
        gate.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.NextRefreshUtc.Should().BeNull();
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_LastUpdatedInFuture_RefreshesAfterClockMovesBackward()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.LastUpdated = DateTime.Now.AddHours(1);
        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task StartAsync_StreamContainsDisabledProvider_KeepsOnlyEnabledProviders()
    {
        async IAsyncEnumerable<UsageData> Results()
        {
            yield return Data(20);
            await Task.Yield();
            yield return Data(80) with { Provider = "codex" };
        }
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>())).Returns(() => Results());
        await _viewModel.StartAsync();
        _viewModel.Providers.Select(p => p.Provider).Should().Equal("claude");
    }

    [TestMethod]
    [DataRow(DateTimeKind.Utc)]
    [DataRow(DateTimeKind.Local)]
    [DataRow(DateTimeKind.Unspecified)]
    public async Task StartAsync_FreshCacheTimestampKinds_UsesUtcAge(DateTimeKind kind)
    {
        _values.RefreshIntervalSeconds = 300;
        var fetchedUtc = DateTime.UtcNow.AddMinutes(-2);
        var fetched = kind == DateTimeKind.Local ? fetchedUtc.ToLocalTime() : DateTime.SpecifyKind(fetchedUtc, kind);
        _cache.Setup(c => c.Get("claude")).Returns(Data(20) with { FetchedAt = fetched });
        await _viewModel.StartAsync();
        _viewModel.LastUpdated.Should().Be(fetchedUtc.ToLocalTime());
        _viewModel.NextRefreshUtc.Should().BeCloseTo(fetchedUtc.AddMinutes(5), TimeSpan.FromSeconds(2));
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task StartAsync_FreshCacheContainsError_FetchesImmediately()
    {
        _cache.Setup(c => c.Get("claude")).Returns(Data(20) with { Error = "fetch failed" });
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers.Should().ContainSingle().Which.HasError.Should().BeFalse();
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task StartAsync_DisabledProviderCacheMissing_RestoresEnabledProvidersOnly()
    {
        _values.Providers = [new() { Id = "codex", IsEnabled = false }, new() { Id = "claude" }];
        _cache.Setup(c => c.Get("claude")).Returns(Data(20));
        await _viewModel.StartAsync();
        _viewModel.Providers.Select(p => p.Provider).Should().Equal("claude");
        _cache.Verify(c => c.Get("codex"), Times.Never);
        _usage.Verify(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task StartAndExit_WhenDeveloperModeEnabled_PreservesExistingHistoryFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_persistence.FilePath)!);
        var originalJson = "[{\"provider\":\"claude\",\"windowKey\":\"session\",\"usedPercent\":42.0}]";
        await File.WriteAllTextAsync(_persistence.FilePath, originalJson);

        _values.DeveloperModeEnabled = true;
        SetupImmediateUsage();

        await _viewModel.StartAsync();
        await _viewModel.ExitCommand.ExecuteAsync(null);

        (await File.ReadAllTextAsync(_persistence.FilePath)).Should().Be(originalJson);
        _history.Samples.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Exit_WhenPersistenceNotLoaded_DoesNotOverwriteExistingHistoryFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_persistence.FilePath)!);
        var originalJson = "[{\"provider\":\"claude\",\"windowKey\":\"session\",\"usedPercent\":42.0}]";
        await File.WriteAllTextAsync(_persistence.FilePath, originalJson);

        _persistence.IsLoaded.Should().BeFalse();
        await _viewModel.ExitCommand.ExecuteAsync(null);

        (await File.ReadAllTextAsync(_persistence.FilePath)).Should().Be(originalJson);
    }

    [TestMethod]
    public async Task RefreshAndExit_WhenLiveMode_SavesHistoryToFile()
    {
        _values.DeveloperModeEnabled = false;
        SetupImmediateUsage();

        await _viewModel.StartAsync();
        await _viewModel.ExitCommand.ExecuteAsync(null);

        File.Exists(_persistence.FilePath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(_persistence.FilePath);
        content.Should().Contain("20");
        _history.Samples.Should().ContainSingle().Which.UsedPercent.Should().Be(20);
    }

    [TestMethod]
    public async Task SettingsChanged_DeveloperModeSwitchedOff_LoadsHistoryBeforeFirstWriteAndSaves()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_persistence.FilePath)!);
        var originalSample = new UsageHistorySample
        {
            Provider = "claude",
            WindowKey = "session",
            UsedPercent = 42.0,
            Timestamp = DateTime.UtcNow.AddMinutes(-30)
        };
        var json = JsonSerializer.Serialize(new List<UsageHistorySample> { originalSample },
            AppJsonSerializerContext.Default.ListUsageHistorySample);
        await File.WriteAllTextAsync(_persistence.FilePath, json);

        _values.DeveloperModeEnabled = true;
        SetupImmediateUsage();
        await _viewModel.StartAsync();

        _persistence.IsLoaded.Should().BeFalse();
        _history.Samples.Should().BeEmpty();

        _values.DeveloperModeEnabled = false;
        _settings.Raise(s => s.SettingsChanged += null, EventArgs.Empty);
        await WaitForRefreshAsync();

        _persistence.IsLoaded.Should().BeTrue();
        _history.Samples.Should().Contain(s => s.UsedPercent == 42.0);
        _history.Samples.Should().Contain(s => s.UsedPercent == 20.0);

        var savedContent = await File.ReadAllTextAsync(_persistence.FilePath);
        savedContent.Should().Contain("42");
        savedContent.Should().Contain("20");
    }
}
