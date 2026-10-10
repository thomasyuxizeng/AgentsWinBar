using System.Runtime.CompilerServices;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services.Stale;

[TestClass]
public class StaleMainViewModelStartupTests
{
    private readonly Mock<ICodexBarService> _usage = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly UsageHistoryStore _history = new();
    private readonly ActivityLog _activity = new();
    private readonly Mock<IToastNotifier> _toast = new();
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ISetupMemoService> _memo = new();
    private readonly Mock<ISessionStateSource> _session = new();
    private AppSettings _values = null!;
    private UsageHistoryPersistence _persistence = null!;
    private MainViewModel _viewModel = null!;
    private string _directory = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-stale-mvm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _values = AppSettings.GetDefaults();
        _values.RefreshIntervalSeconds = 86400; // prevent periodic timer firing
        _values.AlertsEnabled = false;
        _values.Providers = [
            new() { Id = "claude", IsEnabled = true, Order = 0 },
            new() { Id = "codex", IsEnabled = true, Order = 1 }
        ];
        _settings.SetupGet(s => s.Settings).Returns(_values);
        _settings.SetupGet(s => s.SettingsFilePath).Returns(Path.Combine(_directory, "settings.json"));
        _clock.Setup(c => c.GetUtcNow()).Returns(() => DateTimeOffset.UtcNow);
        _session.SetupGet(s => s.IsLocked).Returns(false);
        _persistence = new(_history, _settings.Object, NullLogger<UsageHistoryPersistence>.Instance, _clock.Object);
        _usage.Setup(s => s.GetCostsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<string> _, bool _, CancellationToken _) => EmptyCostStream());

        _viewModel = new(_usage.Object, _settings.Object, new UsageAlertEvaluator(), _toast.Object,
            NullLogger<MainViewModel>.Instance, _history, _persistence, _activity, _session.Object, _cache.Object, _memo.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _viewModel.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static async IAsyncEnumerable<(string Provider, CostData? Cost)> EmptyCostStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<UsageData> DelayedUsageStream(Task gate, IEnumerable<UsageData> items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await gate;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static UsageData SampleData(string provider, double percent, DateTime fetchedAt) => new()
    {
        Provider = provider,
        FetchedAt = fetchedAt,
        Session = new UsageWindow { ReportedPercent = percent, WindowMinutes = 300, ResetAt = fetchedAt.AddHours(2) }
    };

    [TestMethod]
    public async Task StartAsync_WhenCacheHasUsage_RestoresCacheBeforeFirstFetchFinishes()
    {
        var pastTime = DateTime.UtcNow.AddHours(-1);
        _cache.Setup(c => c.GetLastKnown("claude")).Returns(SampleData("claude", 55, pastTime));
        _cache.Setup(c => c.GetLastKnown("codex")).Returns(SampleData("codex", 75, pastTime));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => DelayedUsageStream(gate.Task, [SampleData("claude", 60, DateTime.UtcNow)], ct));

        var startTask = _viewModel.StartAsync();
        try
        {
            // Before gate completes, ViewModel should have restored the stale cache
            _viewModel.Providers.Should().HaveCount(2);
            var claude = _viewModel.Providers.First(p => p.Provider == "claude");
            claude.Session!.Percent.Should().Be(55);
            claude.UsageIsStale.Should().BeTrue();
            claude.LastSuccessfulFetch.Should().Be(pastTime);

            var codex = _viewModel.Providers.First(p => p.Provider == "codex");
            codex.Session!.Percent.Should().Be(75);
            codex.UsageIsStale.Should().BeTrue();
        }
        finally
        {
            gate.SetResult();
            await startTask;
        }

        // After fetch completes, claude is updated
        _viewModel.Providers.First(p => p.Provider == "claude").Session!.Percent.Should().Be(60);
    }

    [TestMethod]
    public async Task StartAsync_WhenCacheHasNoUsageButHistoryExists_RestoresFromHistory()
    {
        var pastTime = DateTime.UtcNow.AddMinutes(-30);
        // Cache has no usage reading
        _cache.Setup(c => c.GetLastKnown("claude")).Returns((UsageData?)null);
        _cache.Setup(c => c.GetLastKnown("codex")).Returns((UsageData?)null);

        // History has observations
        _history.Add(new() { Provider = "claude", WindowKey = "session", UsedPercent = 44, Timestamp = pastTime }, DateTime.UtcNow);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => DelayedUsageStream(gate.Task, [], ct));

        var startTask = _viewModel.StartAsync();
        try
        {
            var claude = _viewModel.Providers.First(p => p.Provider == "claude");
            claude.Session!.Percent.Should().Be(44);
            claude.UsageIsStale.Should().BeTrue();
            claude.LastSuccessfulFetch.Should().Be(pastTime);
        }
        finally
        {
            gate.SetResult();
            await startTask;
        }
    }

    [TestMethod]
    public async Task StartAsync_WhenBothCacheAndHistoryExist_CacheTakesPrecedenceOverHistory()
    {
        var cacheTime = DateTime.UtcNow.AddHours(-1);
        var historyTime = DateTime.UtcNow.AddHours(-3);

        // Cache has 80%
        _cache.Setup(c => c.GetLastKnown("claude")).Returns(SampleData("claude", 80, cacheTime));
        // History has 20%
        _history.Add(new() { Provider = "claude", WindowKey = "session", UsedPercent = 20, Timestamp = historyTime }, DateTime.UtcNow);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => DelayedUsageStream(gate.Task, [], ct));

        var startTask = _viewModel.StartAsync();
        try
        {
            var claude = _viewModel.Providers.First(p => p.Provider == "claude");
            // Cache value 80% wins over history value 20%
            claude.Session!.Percent.Should().Be(80);
            claude.UsageIsStale.Should().BeTrue();
            claude.LastSuccessfulFetch.Should().Be(cacheTime);
        }
        finally
        {
            gate.SetResult();
            await startTask;
        }
    }

    [TestMethod]
    public async Task StartAsync_WhenNothingCachedAndNoHistory_ProvidersRemainEmptyUntilFetchCompletes()
    {
        _cache.Setup(c => c.GetLastKnown(It.IsAny<string>())).Returns((UsageData?)null);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => DelayedUsageStream(gate.Task, [], ct));

        var startTask = _viewModel.StartAsync();
        try
        {
            // Initial check: Providers has not loaded any stale readings (all empty/loading placeholders)
            _viewModel.Providers.Should().AllSatisfy(p => p.HasUsage.Should().BeFalse());
        }
        finally
        {
            gate.SetResult();
            await startTask;
        }
    }

    [TestMethod]
    public async Task StartAsync_WhenProviderIsDisabled_DoesNotRestoreDisabledProvider()
    {
        // Disable codex
        _values.Providers = [
            new() { Id = "claude", IsEnabled = true, Order = 0 },
            new() { Id = "codex", IsEnabled = false, Order = 1 }
        ];

        var pastTime = DateTime.UtcNow.AddHours(-1);
        _cache.Setup(c => c.GetLastKnown("claude")).Returns(SampleData("claude", 50, pastTime));
        _cache.Setup(c => c.GetLastKnown("codex")).Returns(SampleData("codex", 90, pastTime));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => DelayedUsageStream(gate.Task, [], ct));

        var startTask = _viewModel.StartAsync();
        try
        {
            _viewModel.Providers.Should().ContainSingle(p => p.Provider == "claude");
            _viewModel.Providers.Any(p => p.Provider == "codex").Should().BeFalse();
        }
        finally
        {
            gate.SetResult();
            await startTask;
        }
    }

    [TestMethod]
    public async Task Refresh_WhenProviderRemovedAndReAdded_RetainsAndRestoresCache()
    {
        var cacheTime = DateTime.UtcNow.AddHours(-1);
        _cache.Setup(c => c.GetLastKnown("claude")).Returns(SampleData("claude", 60, cacheTime));
        _cache.Setup(c => c.GetLastKnown("codex")).Returns(SampleData("codex", 70, cacheTime));

        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => DelayedUsageStream(Task.CompletedTask, [
                SampleData("claude", 60, cacheTime) with { UsageIsStale = true }
            ], ct));

        await _viewModel.StartAsync();
        _viewModel.Providers.Should().HaveCount(2);

        // Remove codex
        _values.Providers = [new() { Id = "claude", IsEnabled = true, Order = 0 }];
        await _viewModel.RefreshCommand.ExecuteAsync(null);
        _viewModel.Providers.Should().ContainSingle(p => p.Provider == "claude");

        // Re-add codex
        _values.Providers = [
            new() { Id = "claude", IsEnabled = true, Order = 0 },
            new() { Id = "codex", IsEnabled = true, Order = 1 }
        ];
        _usage.Setup(s => s.GetAllUsageStreamAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => DelayedUsageStream(Task.CompletedTask, [
                SampleData("claude", 60, cacheTime) with { UsageIsStale = true },
                SampleData("codex", 70, cacheTime) with { UsageIsStale = true }
            ], ct));

        await _viewModel.RefreshCommand.ExecuteAsync(null);
        _viewModel.Providers.Should().HaveCount(2);
        _viewModel.Providers.First(p => p.Provider == "codex").Session!.Percent.Should().Be(70);
    }
}
