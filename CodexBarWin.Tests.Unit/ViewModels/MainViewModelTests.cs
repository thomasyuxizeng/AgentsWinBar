using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
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
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ISetupMemoService> _memo = new();
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
            NullLogger<MainViewModel>.Instance, _history, _persistence, _activity, _session, _cache.Object, _memo.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _viewModel.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        .Invoke(_viewModel, [false])!;

    private Task CostScanCompletion(bool background = true) => ((TaskCompletionSource)typeof(MainViewModel)
        .GetField(background ? "_backgroundCostRefreshCompletion" : "_costRefreshCompletion",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_viewModel)!).Task;

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
            .Invoke(_viewModel, [TimeSpan.Zero, CancellationToken.None])!;
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
    public async Task RefreshCoreAsync_CacheSaveFails_StillCompletesRefresh()
    {
        SetupImmediateUsage();
        _cache.Setup(c => c.SaveAsync()).ThrowsAsync(new IOException("cache unavailable"));
        await _viewModel.StartAsync();
        _cache.Verify(c => c.SaveAsync(), Times.Once);
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
    public async Task AcquireCostDemand_ExpiredAttachedCost_RequestsScanOnReopen()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers[0] = _viewModel.Providers[0] with
        { Cost = new CostData { Provider = "claude", TodayCostUSD = 10, FetchedAt = DateTime.UtcNow,
            LastSuccessfulFetch = DateTime.UtcNow.AddDays(-8) } };
        var scan = new TaskCompletionSource<CostData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _usage.Setup(s => s.GetCostAsync("claude", false, It.IsAny<CancellationToken>())).Returns(scan.Task);
        using var demand = _viewModel.AcquireCostDemand();
        var completion = CostScanCompletion(background: false);
        _usage.Verify(s => s.GetCostsAsync(It.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == "claude"),
            false, It.IsAny<CancellationToken>()), Times.Once);
        scan.SetResult(new CostData { Provider = "claude", TodayCostUSD = 2 });
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        _viewModel.Providers.Single().Cost!.TodayCostUSD.Should().Be(2);
    }

    [TestMethod]
    public async Task RefreshCoreAsync_ExpiredAttachedCost_DropsItDuringUsageRefresh()
    {
        SetupImmediateUsage();
        await _viewModel.StartAsync();
        _viewModel.Providers[0] = _viewModel.Providers[0] with
        { Cost = new CostData { Provider = "claude", TodayCostUSD = 10, FetchedAt = DateTime.UtcNow.AddDays(-8) } };
        await PeriodicRefreshAsync();
        _viewModel.Providers.Single().Cost.Should().BeNull();
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
}
