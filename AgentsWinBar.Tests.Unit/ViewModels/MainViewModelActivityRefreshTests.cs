using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Services.Native;
using AgentsWinBar.Tests.Unit.Services.Native;
using AgentsWinBar.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentsWinBar.Tests.Unit.ViewModels;

/// <summary>The per-provider schedule ("Refresh faster while a tool is in use") as MainViewModel runs it.</summary>
[TestClass]
public class MainViewModelActivityRefreshTests
{
    private readonly Mock<ICodexBarService> _usage = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ISetupMemoService> _memo = new();
    private readonly FakeSession _session = new();
    private readonly FakeActivity _activity = new();
    private readonly ListLogger<RefreshScheduleLog> _scheduleLog = new();
    private readonly List<string[]> _subsetReads = [];
    private readonly HashSet<string> _failing = [];
    private int _allReads;
    private int _reading;
    private AppSettings _values = null!;
    private MainViewModel _viewModel = null!;
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "AgentsWinBar_ActivityRefresh_" + Guid.NewGuid());
        _values = AppSettings.GetDefaults();
        _values.RefreshIntervalSeconds = 300;
        _values.AlertsEnabled = false;
        _values.ShowCostTotals = false;
        _values.Providers = [new() { Id = "claude", Order = 0 }, new() { Id = "codex", Order = 1 }];
        _settings.SetupGet(s => s.Settings).Returns(_values);
        _settings.SetupGet(s => s.SettingsFilePath).Returns(Path.Combine(_directory, "settings.json"));
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => { _allReads++; return Read(["claude", "codex"], ct); });
        _usage.Setup(s => s.GetUsageStreamAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyCollection<string> providers, CancellationToken ct) =>
            {
                _subsetReads.Add([.. providers]);
                return Read(providers, ct);
            });
        var history = new UsageHistoryStore();
        var persistence = new UsageHistoryPersistence(history, _settings.Object, NullLogger<UsageHistoryPersistence>.Instance);
        _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), new Mock<IToastNotifier>().Object,
            NullLogger<MainViewModel>.Instance, history, persistence, new ActivityLog(), _session, _cache.Object, _memo.Object,
            toolActivity: _activity, scheduleLog: _scheduleLog);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _viewModel.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private async IAsyncEnumerable<UsageData> Read(IEnumerable<string> providers, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var provider in providers.ToArray())
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return _failing.Contains(provider)
                ? new UsageData { Provider = provider, FetchedAt = DateTime.UtcNow, Error = "Usage fetch unavailable", UsageFetchOutcome = FetchOutcome.Failed }
                : new UsageData
                {
                    Provider = provider, FetchedAt = DateTime.UtcNow,
                    Session = new UsageWindow { ReportedPercent = 10 + ++_reading, WindowMinutes = 300, ResetAt = DateTime.UtcNow.AddHours(2) }
                };
        }
    }

    private Dictionary<string, DateTime> Stamps(string field) => (Dictionary<string, DateTime>)typeof(MainViewModel)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_viewModel)!;

    /// <summary>Pretends the provider was last read <paramref name="ago"/> earlier.</summary>
    private void FetchedAgo(string provider, TimeSpan ago)
    {
        Stamps("_lastFetchUtc")[provider] = DateTime.UtcNow - ago;
        Stamps("_lastResultUtc")[provider] = DateTime.UtcNow - ago;
    }

    private Task TimerFiresAsync(ResetRefreshSchedule? schedule = null) => (Task)typeof(MainViewModel)
        .GetMethod("RunPeriodicRefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_viewModel, [schedule ?? new ResetRefreshSchedule(TimeSpan.Zero), CancellationToken.None])!;

    private void Replan() => typeof(MainViewModel).GetMethod("RecreateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_viewModel, [null]);

    private UsageData Shown(string provider) => _viewModel.Providers.Single(p => p.Provider == provider);

    [TestMethod]
    public async Task StartAsync_ToolInUse_SchedulesTwoMinutesAndShowsWhichProvider()
    {
        _activity.Touch("codex");

        await _viewModel.StartAsync();

        _allReads.Should().Be(1);
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(2), TimeSpan.FromSeconds(5));
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(2));
        _viewModel.RefreshCadence.ActiveProviders.Should().Equal("codex");
        _scheduleLog.Messages.Should().Contain("Startup refresh of claude: First, back-off step 0, interval 0 s, rate limited False");
    }

    [TestMethod]
    public async Task TimerFires_OnlyActiveProviderDue_ReadsOnlyThatProviderAndKeepsTheOthersReading()
    {
        _activity.Touch("codex");
        await _viewModel.StartAsync();
        var claudeBefore = Shown("claude");
        FetchedAgo("codex", TimeSpan.FromMinutes(2.5));

        await TimerFiresAsync();

        _subsetReads.Should().ContainSingle().Which.Should().Equal("codex");
        _allReads.Should().Be(1);
        Shown("claude").Should().BeSameAs(claudeBefore);
        Shown("codex").FetchedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        Shown("codex").IsLoading.Should().BeFalse();
        _scheduleLog.Messages.Should().Contain(m => m.StartsWith("Scheduled refresh of codex: Active, back-off step 0, interval 120 s"));
        _scheduleLog.Messages.Should().NotContain(m => m.StartsWith("Scheduled refresh of claude"));
    }

    [TestMethod]
    public async Task TimerFires_BeforeAnythingIsDue_OnlyReplans()
    {
        await _viewModel.StartAsync();

        await TimerFiresAsync();

        _allReads.Should().Be(1);
        _subsetReads.Should().BeEmpty();
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ActivityStarted_IdleProvider_ReplansToItsActiveInterval()
    {
        await _viewModel.StartAsync();
        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(5));
        _viewModel.RefreshCadence.IsFaster.Should().BeFalse();

        _activity.Start("claude");

        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(2), TimeSpan.FromSeconds(5));
        _viewModel.RefreshCadence.ActiveProviders.Should().Equal("claude");
    }

    [TestMethod]
    public async Task ActivityStarted_SessionLocked_KeepsTheLockedWait()
    {
        await _viewModel.StartAsync();
        _session.Lock();
        var lockedNext = _viewModel.NextRefreshUtc;
        lockedNext.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(5));

        _activity.Start("claude");

        _viewModel.NextRefreshUtc.Should().Be(lockedNext);
        _viewModel.RefreshCadence.IsFaster.Should().BeFalse();
    }

    [TestMethod]
    public async Task TimerFires_NearLimitIdleProvider_ReadsItEveryFiveMinutes()
    {
        _values.RefreshIntervalSeconds = 600;
        await _viewModel.StartAsync();
        _viewModel.Providers[1] = Shown("codex") with { Weekly = new UsageWindow { ReportedPercent = 90, WindowMinutes = 10080 } };

        Replan();

        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(5));
        _viewModel.RefreshCadence.NearLimit.Should().BeTrue();
        FetchedAgo("codex", TimeSpan.FromMinutes(5));
        await TimerFiresAsync();
        _subsetReads.Should().ContainSingle().Which.Should().Equal("codex");
        _scheduleLog.Messages.Should().Contain(m => m.StartsWith("Scheduled refresh of codex: NearLimit"));
    }

    [TestMethod]
    public async Task RefreshCommand_ReadsEveryProvider()
    {
        _activity.Touch("codex");
        await _viewModel.StartAsync();

        await _viewModel.RefreshCommand.ExecuteAsync(null);

        _allReads.Should().Be(2);
        _subsetReads.Should().BeEmpty();
        _scheduleLog.Messages.Should().Contain(m => m.StartsWith("Manual refresh of claude: Manual"));
        _scheduleLog.Messages.Should().Contain(m => m.StartsWith("Manual refresh of codex: Manual"));
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_OneProviderOld_ReadsOnlyThatProvider()
    {
        await _viewModel.StartAsync();
        FetchedAgo("codex", TimeSpan.FromMinutes(12));

        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));

        _subsetReads.Should().ContainSingle().Which.Should().Equal("codex");
        _scheduleLog.Messages.Should().Contain(m => m.StartsWith("OnOpen refresh of codex: OnOpen"));
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_AllFresh_ReadsNothing()
    {
        await _viewModel.StartAsync();

        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));

        _allReads.Should().Be(1);
        _subsetReads.Should().BeEmpty();
    }

    [TestMethod]
    public async Task StartAsync_FreshCacheRestored_ShowsTheNewestReading()
    {
        var claudeAt = DateTime.UtcNow.AddSeconds(-20);
        _cache.Setup(c => c.Get("claude")).Returns(Cached("claude", claudeAt));
        _cache.Setup(c => c.Get("codex")).Returns(Cached("codex", DateTime.UtcNow.AddMinutes(-4)));

        await _viewModel.StartAsync();

        _allReads.Should().Be(0);
        _viewModel.LastUpdated.Should().Be(claudeAt.ToLocalTime());

        static UsageData Cached(string provider, DateTime fetchedAt) => new()
        {
            Provider = provider, FetchedAt = fetchedAt, Session = new UsageWindow { ReportedPercent = 10, WindowMinutes = 300 }
        };
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_LastUpdatedOldOrAhead_GoesByEachProvidersOwnStamps()
    {
        await _viewModel.StartAsync();

        _viewModel.LastUpdated = DateTime.Now.AddHours(-1);
        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));
        // A DST or time zone change moves local times, not the UTC stamps
        _viewModel.LastUpdated = DateTime.Now.AddHours(1);
        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));

        _allReads.Should().Be(1);
        _subsetReads.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_ClaudeWithinItsFloor_ReadsOnlyTheOthers()
    {
        // A 1-minute interval and an unlock 95 s after the last read: older than the interval, within Claude's floor
        _values.RefreshIntervalSeconds = 60;
        await _viewModel.StartAsync();
        FetchedAgo("claude", TimeSpan.FromSeconds(95));
        FetchedAgo("codex", TimeSpan.FromSeconds(95));

        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromSeconds(60));

        _subsetReads.Should().ContainSingle().Which.Should().Equal("codex");
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_ProviderInCooldown_IsNotRead()
    {
        await _viewModel.StartAsync();
        FetchedAgo("claude", TimeSpan.FromMinutes(6));
        FetchedAgo("codex", TimeSpan.FromMinutes(6));
        _usage.Setup(s => s.GetRateLimitedUntil("claude")).Returns(DateTime.UtcNow.AddMinutes(4));

        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));

        _subsetReads.Should().ContainSingle().Which.Should().Equal("codex");
        _scheduleLog.Messages.Should().NotContain(m => m.StartsWith("OnOpen refresh of claude"));
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_ClockMovedBack_ReadsCodexAndHoldsClaudeToItsFloor()
    {
        await _viewModel.StartAsync();
        // Both stamps an hour ahead of the clock
        FetchedAgo("claude", TimeSpan.FromHours(-1));
        FetchedAgo("codex", TimeSpan.FromHours(-1));

        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));

        _subsetReads.Should().ContainSingle().Which.Should().Equal("codex");
    }

    [TestMethod]
    public async Task RefreshIfStaleAsync_LastReadFailed_TriesItAgain()
    {
        _failing.Add("codex");
        await _viewModel.StartAsync();
        Shown("codex").HasError.Should().BeTrue();
        _failing.Clear();

        await _viewModel.RefreshIfStaleAsync(TimeSpan.FromMinutes(2));

        _subsetReads.Should().ContainSingle().Which.Should().Equal("codex");
        Shown("codex").HasError.Should().BeFalse();
    }

    [TestMethod]
    public async Task TimerFires_ReadFails_WaitsAnIntervalInsteadOfRetryingAtOnce()
    {
        _activity.Touch("codex");
        await _viewModel.StartAsync();
        _usage.Setup(s => s.GetUsageStreamAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyCollection<string> providers, CancellationToken ct) => Failing(ct));
        FetchedAgo("codex", TimeSpan.FromMinutes(3));

        await TimerFiresAsync();

        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(2), TimeSpan.FromSeconds(5));

        static async IAsyncEnumerable<UsageData> Failing([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            throw new HttpRequestException("synthetic failure");
#pragma warning disable CS0162 // An iterator needs a yield
            yield break;
#pragma warning restore CS0162
        }
    }

    [TestMethod]
    public async Task TimerFires_ProviderRateLimited_WaitsForTheCooldownEnd()
    {
        var until = DateTime.UtcNow.AddMinutes(9);
        _usage.Setup(s => s.GetRateLimitedUntil("claude")).Returns(until);
        _activity.Touch("claude");
        await _viewModel.StartAsync();
        FetchedAgo("claude", TimeSpan.FromMinutes(3));

        Replan();

        _viewModel.NextRefreshUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(5),
            "Codex is due at its idle time before Claude's cooldown ends");
        await TimerFiresAsync();
        _subsetReads.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TimerFires_CooldownEndsWithinTheTimerTolerance_WaitsForItsRealEnd()
    {
        // Asked at any time, the cooldown ends half a second later: inside the 1 s timer tolerance, never over
        _usage.Setup(s => s.GetRateLimitedUntil("claude")).Returns(() => DateTime.UtcNow.AddMilliseconds(500));
        _activity.Touch("claude");
        await _viewModel.StartAsync();
        FetchedAgo("claude", TimeSpan.FromMinutes(3));

        await TimerFiresAsync();

        _subsetReads.Should().BeEmpty();
        Stamps("_lastFetchUtc")["claude"].Should().BeCloseTo(DateTime.UtcNow.AddMinutes(-3), TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task TimerFires_WindowResetDuringCooldown_DoesNotReadThatProvider()
    {
        await _viewModel.StartAsync();
        var reset = DateTime.UtcNow.AddSeconds(-3);
        _viewModel.Providers[0] = Shown("claude") with
        {
            Session = new UsageWindow { ReportedPercent = 50, WindowMinutes = 300, ResetAt = reset }
        };
        _usage.Setup(s => s.GetRateLimitedUntil("claude")).Returns(DateTime.UtcNow.AddMinutes(9));

        await TimerFiresAsync(new ResetRefreshSchedule(TimeSpan.Zero, reset));

        _subsetReads.Should().BeEmpty();
        _allReads.Should().Be(1);
        _scheduleLog.Messages.Should().NotContain(m => m.StartsWith("Scheduled refresh of claude"));
    }

    [TestMethod]
    public async Task TimerFires_WindowResetWithoutCooldown_ReadsThatProvider()
    {
        await _viewModel.StartAsync();
        var reset = DateTime.UtcNow.AddSeconds(-3);
        _viewModel.Providers[0] = Shown("claude") with
        {
            Session = new UsageWindow { ReportedPercent = 50, WindowMinutes = 300, ResetAt = reset }
        };

        await TimerFiresAsync(new ResetRefreshSchedule(TimeSpan.Zero, reset));

        _subsetReads.Should().ContainSingle().Which.Should().Equal("claude");
        _scheduleLog.Messages.Should().Contain(m => m.StartsWith("Scheduled refresh of claude: Reset"));
    }

    [TestMethod]
    public async Task RefreshCommand_ProviderInCooldown_DoesNotCountItsHeldAnswerAsAFetch()
    {
        await _viewModel.StartAsync();
        FetchedAgo("claude", TimeSpan.FromMinutes(1));
        _usage.Setup(s => s.GetRateLimitedUntil("claude")).Returns(DateTime.UtcNow.AddMinutes(5));

        await _viewModel.RefreshCommand.ExecuteAsync(null);

        _allReads.Should().Be(2);
        Stamps("_lastFetchUtc")["claude"].Should().BeCloseTo(DateTime.UtcNow.AddMinutes(-1), TimeSpan.FromSeconds(5),
            "the answer came from the hold, so the attempt after the cooldown is not pushed back");
        Stamps("_lastFetchUtc")["codex"].Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task RefreshCommand_CooldownChangedDuringTheRead_CountsAsAFetch()
    {
        // A new 429 (or a new sign-in) changes the cooldown: a call was made
        await _viewModel.StartAsync();
        FetchedAgo("claude", TimeSpan.FromMinutes(1));
        var until = DateTime.UtcNow.AddMinutes(5);
        _usage.Setup(s => s.GetRateLimitedUntil("claude")).Returns(() => until);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => { _allReads++; until = until.AddMinutes(10); return Read(["claude", "codex"], ct); });

        await _viewModel.RefreshCommand.ExecuteAsync(null);

        Stamps("_lastFetchUtc")["claude"].Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ProviderDisabled_IsNeverReadByTheSchedule()
    {
        _activity.Touch("codex");
        await _viewModel.StartAsync();
        _values.Providers[1] = _values.Providers[1] with { IsEnabled = false };
        FetchedAgo("codex", TimeSpan.FromMinutes(10));
        FetchedAgo("claude", TimeSpan.FromMinutes(10));

        await TimerFiresAsync();

        // Only Claude is left, so the read is a full one
        _allReads.Should().Be(2);
        _subsetReads.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SettingOff_ReadsEveryProviderOnEachTimerAndShowsNoCadence()
    {
        _values.ActivityRefreshEnabled = false;
        _activity.Touch("codex");
        await _viewModel.StartAsync();

        await TimerFiresAsync();

        _allReads.Should().Be(2);
        _subsetReads.Should().BeEmpty();
        _viewModel.RefreshCadence.Should().Be(RefreshCadence.None);
        _viewModel.EffectiveRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));
        _scheduleLog.Messages.Should().Contain(m => m.StartsWith("Scheduled refresh of codex: Idle, back-off step 0, interval 300 s"));
    }

    [TestMethod]
    public async Task ScheduleLog_NeverContainsPathsOrUsageValues()
    {
        _activity.Touch("codex");
        await _viewModel.StartAsync();
        FetchedAgo("codex", TimeSpan.FromMinutes(3));
        await TimerFiresAsync();

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _scheduleLog.Messages.Should().NotBeEmpty();
        _scheduleLog.Messages.Should().OnlyContain(m => !m.Contains(profile, StringComparison.OrdinalIgnoreCase) &&
            !m.Contains('\\') && !m.Contains('%'));
    }

    private sealed class FakeActivity : IProviderActivitySource
    {
        private readonly Dictionary<string, DateTime> _stamps = new(StringComparer.OrdinalIgnoreCase);
        public event EventHandler<string>? ActivityStarted;
        public DateTime? LastActivityUtc(string provider) => _stamps.TryGetValue(provider, out var stamp) ? stamp : null;
        public void EnsureWatching() { }
        public void Touch(string provider) => _stamps[provider] = DateTime.UtcNow;
        public void Start(string provider)
        {
            Touch(provider);
            ActivityStarted?.Invoke(this, provider);
        }
    }

    private sealed class FakeSession : ISessionStateSource
    {
        public bool IsLocked { get; private set; }
        public event EventHandler? Locked;
        public event EventHandler? Unlocked;
        public event EventHandler? Resumed;
        public void Lock() { IsLocked = true; Locked?.Invoke(this, EventArgs.Empty); }
        public void Unlock() { IsLocked = false; Unlocked?.Invoke(this, EventArgs.Empty); Resumed?.Invoke(this, EventArgs.Empty); }
    }
}
