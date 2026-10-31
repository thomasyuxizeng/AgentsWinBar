using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class CodexBarServiceTests
{
    private MockWslService _mockWslService = null!;
    private MockCacheService _mockCacheService = null!;
    private MockSettingsService _mockSettingsService = null!;
    private MockSampleDataLoader _mockSampleDataLoader = null!;
    private Mock<ILogger<CodexBarService>> _mockLogger = null!;
    private CodexBarService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockWslService = new MockWslService();
        _mockCacheService = new MockCacheService();
        _mockSettingsService = new MockSettingsService();
        _mockSampleDataLoader = new MockSampleDataLoader();
        _mockLogger = new Mock<ILogger<CodexBarService>>();

        _service = new CodexBarService(
            _mockWslService,
            _mockCacheService,
            _mockSettingsService,
            _mockSampleDataLoader,
            _mockLogger.Object);
    }

    [TestMethod]
    public async Task GetUsageAsync_ValidProvider_ExecutesCommand()
    {
        // Arrange
        // Single-object responses remain supported alongside CLI arrays.
        var jsonResponse = """{"provider":"claude","usage":{"loginMethod":"test","primary":{"usedPercent":50,"windowMinutes":60}}}""";
        _mockWslService.SetCommandResult(
            "codexbar --provider claude --format json --source cli --status",
            new WslResult { Success = true, Output = jsonResponse, ExitCode = 0 });

        // Act
        var result = await _service.GetUsageAsync("claude");

        // Assert
        result.Should().NotBeNull();
        _mockWslService.ExecutedCommands.Should().ContainSingle()
            .Which.Should().Contain("--provider claude");
        _mockWslService.ExecutedCommands[0].Should().Contain("--status");
    }

    private sealed class CostClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
        public TimeZoneInfo? Zone { get; init; }
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone ?? base.LocalTimeZone;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetUsageAsync_ObjectOrArrayResponse_CachesRequestedProvider(bool array)
    {
        const string requested = """{"provider":"claude","usage":{"primary":{"usedPercent":50,"windowMinutes":60}}}""";
        var response = array ? "[{\"provider\":\"codex\"}," + requested + "]" : requested;
        _mockWslService.SetCommandResult("codexbar --provider claude --format json --source cli --status",
            new WslResult { Success = true, Output = response });

        var result = await _service.GetUsageAsync("claude");

        result!.Provider.Should().Be("claude");
        result.Session!.Percent.Should().Be(50);
        _mockCacheService.Get("claude").Should().BeSameAs(result);
        _mockCacheService.Get("codex").Should().BeNull();
    }

    [TestMethod]
    public async Task GetUsageAsync_ArrayMissingRequestedProvider_UsesCacheWithoutCachingOtherProvider()
    {
        var cached = new UsageData { Provider = "claude", Plan = "Cached", Session = new() { ReportedPercent = 42 } };
        _mockCacheService.Set("claude", cached);
        _mockWslService.SetCommandResult("codexbar --provider claude --format json --source cli --status",
            new WslResult { Success = true, Output = "[{\"provider\":\"codex\"}]" });

        var result = await _service.GetUsageAsync("claude");
        result!.Session.Should().BeEquivalentTo(cached.Session);
        result.UsageIsStale.Should().BeTrue();
        _mockCacheService.Get("codex").Should().BeNull();
    }

    [TestMethod]
    [DataRow(9)]
    [DataRow(-7)]
    public async Task GetCostAsync_CacheHitCrossesLocalMidnight_RetainsTodayAsStaleWithoutMutatingCachedResult(int offsetHours)
    {
        var offset = TimeSpan.FromHours(offsetHours);
        var clock = new CostClock
        {
            Now = new DateTimeOffset(2026, 10, 8, 23, 55, 0, offset).ToUniversalTime(),
            Zone = TimeZoneInfo.CreateCustomTimeZone("TestLocal", offset, "TestLocal", "TestLocal")
        };
        var service = CostService(clock);
        SetCostResponse(json: """{"provider":"claude","sessionCostUSD":1.5,"sessionTokens":20,"last30DaysCostUSD":5,"last30DaysTokens":123456,"daily":[{"date":"2026-10-08","totalCost":1.5,"totalTokens":20}]}""");
        var original = await service.GetCostAsync("claude");
        (await service.GetCostAsync("claude")).Should().BeSameAs(original);

        clock.Now += TimeSpan.FromMinutes(10);
        var normalized = await service.GetCostAsync("claude");

        normalized.Should().BeEquivalentTo(original! with { IsStale = true });
        original!.TodayCostUSD.Should().Be(1.5m);
        original.TodayTokens.Should().Be(20);
        normalized!.Daily.Should().BeSameAs(original.Daily);
        clock.Now -= TimeSpan.FromMinutes(10);
        (await service.GetCostAsync("claude")).Should().BeSameAs(original);
        _mockWslService.ExecutedCommands.Should().ContainSingle();
    }

    [TestMethod]
    public async Task GetCostCacheInfo_ScanInFlight_ReturnsAttemptWithoutWaitingThenPublishesData()
    {
        var wsl = new Mock<IWslService>();
        var clock = new CostClock();
        var scan = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        wsl.Setup(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>())).Returns(scan.Task);
        var service = new CodexBarService(wsl.Object, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
            _mockLogger.Object, clock, userProfile: () => "C:\\Users\\Test", directoryExists: _ => true);
        service.GetCostCacheInfo().Should().BeEmpty();
        var fetching = service.GetCostAsync("claude");
        var entries = await Task.Run(service.GetCostCacheInfo).WaitAsync(TimeSpan.FromSeconds(5));
        entries.Should().ContainSingle().Which.Should().BeEquivalentTo(new DashboardCostCacheEntry
        { ProviderId = "claude", LastAttemptUtc = clock.Now.UtcDateTime, HasData = false });
        scan.SetResult(new() { Success = true, Output = """{"provider":"claude","last30DaysTokens":10}""" });
        await fetching.WaitAsync(TimeSpan.FromSeconds(5));
        service.GetCostCacheInfo().Single().HasData.Should().BeTrue();
        entries.Single().HasData.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCostCacheInfo_FailedAndCanceledScans_ReportsBackoffAndRestoresPriorAttempt()
    {
        var wsl = new Mock<IWslService>();
        var clock = new CostClock();
        wsl.Setup(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>())).ReturnsAsync(new WslResult());
        var service = new CodexBarService(wsl.Object, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
            _mockLogger.Object, clock, userProfile: () => "C:\\Users\\Test", directoryExists: _ => true);
        await service.GetCostAsync("claude");
        var initial = service.GetCostCacheInfo().Single();
        initial.HasData.Should().BeFalse(); initial.LastAttemptUtc.Should().Be(clock.Now.UtcDateTime);
        var scan = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        wsl.Setup(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>())).Returns(scan.Task);
        clock.Now = clock.Now.AddMinutes(1);
        using var cts = new CancellationTokenSource();
        var fetching = service.GetCostAsync("claude", forceRefresh: true, ct: cts.Token);
        cts.Cancel(); scan.SetResult(new()); await fetching;
        service.GetCostCacheInfo().Single().Should().BeEquivalentTo(initial);
    }

    [TestMethod]
    public async Task GetCostAsync_DisabledWhileAnotherScanHoldsGate_DoesNotPublishOrRunQueuedScan()
    {
        var wsl = new Mock<IWslService>();
        var scan = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wsl.Setup(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>())).Returns(() =>
        {
            started.TrySetResult();
            return scan.Task;
        });
        var service = new CodexBarService(wsl.Object, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
            _mockLogger.Object, userProfile: () => "C:\\Users\\Test", directoryExists: _ => true);
        var first = service.GetCostAsync("claude");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = service.GetCostAsync("codex");
        _mockSettingsService.Settings.ShowCostTotals = false;
        scan.SetResult(new WslResult { Success = true, Output = """{"provider":"claude","last30DaysTokens":10}""" });
        (await first).Should().BeNull();
        (await queued).Should().BeNull();
        wsl.Verify(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetAllUsageStreamAsync_CancelledButBackendReturns_DoesNotYieldOrCacheLateResult()
    {
        _mockSettingsService.SetProviders([new() { Id = "claude" }]);
        var scan = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wsl = new Mock<IWslService>();
        wsl.Setup(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(scan.Task);
        var service = new CodexBarService(wsl.Object, _mockCacheService, _mockSettingsService, _mockSampleDataLoader, _mockLogger.Object);
        using var cts = new CancellationTokenSource();
        await using var stream = service.GetAllUsageStreamAsync(cts.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        cts.Cancel();
        scan.SetResult(new WslResult { Success = true, Output = """[{"provider":"claude","usage":{"primary":{"usedPercent":90}}}]""" });
        Func<Task> moveNext = async () => await next;
        await moveNext.Should().ThrowAsync<OperationCanceledException>();
        _mockCacheService.SetCallCount.Should().Be(0);
    }

    private CodexBarService CostService(CostClock? clock = null, string profile = "C:\\Users\\Test", bool folderExists = true) =>
        new(_mockWslService, _mockCacheService, _mockSettingsService, _mockSampleDataLoader, _mockLogger.Object,
            clock, () => profile, _ => folderExists);

    [TestMethod]
    public async Task GetCostAsync_DisabledDuringFirstScan_ReenabledWithoutFailureBackoff()
    {
        var scan = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new WslResult { Success = true, Output = """{"provider":"claude","last30DaysTokens":10}""" };
        var wsl = new Mock<IWslService>();
        wsl.SetupSequence(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>()))
            .Returns(scan.Task).ReturnsAsync(result);
        var service = new CodexBarService(wsl.Object, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
            _mockLogger.Object, new CostClock(), () => "C:\\Users\\Test", _ => true);
        var pending = service.GetCostAsync("claude");
        _mockSettingsService.Settings.ShowCostTotals = false;
        scan.SetResult(result);
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();
        _mockSettingsService.Settings.ShowCostTotals = true;
        (await service.GetCostAsync("claude")).Should().NotBeNull();
        wsl.Verify(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task GetCostAsync_CanceledManualScan_PreservesPreviouslyCachedTotals()
    {
        var scan = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new WslResult { Success = true, Output = """{"provider":"claude","last30DaysTokens":10}""" };
        var wsl = new Mock<IWslService>();
        wsl.SetupSequence(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result).Returns(scan.Task);
        var service = new CodexBarService(wsl.Object, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
            _mockLogger.Object, new CostClock(), () => "C:\\Users\\Test", _ => true);
        var cached = await service.GetCostAsync("claude");
        using var cts = new CancellationTokenSource();
        var pending = service.GetCostAsync("claude", forceRefresh: true, ct: cts.Token);
        cts.Cancel();
        scan.SetResult(result);
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();
        (await service.GetCostAsync("claude")).Should().BeSameAs(cached);
        wsl.Verify(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenProviderTimeoutCancelsCommand_ReportsRequestTimedOut()
    {
        var wsl = new Mock<IWslService>();
        wsl.Setup(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException ex) { return new WslResult { Success = false, Error = ex.Message, ExitCode = -1 }; }
            return new WslResult();
        });
        _mockSettingsService.Settings.Timeouts.CliProviderTimeoutSeconds = 1;
        _mockSettingsService.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds = 1;
        var service = new CodexBarService(wsl.Object, _mockCacheService, _mockSettingsService, _mockSampleDataLoader, _mockLogger.Object);

        var results = await service.GetAllUsageAsync();

        results.Should().NotBeEmpty();
        results.Should().OnlyContain(r => r.Error == "Request timed out");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GetAllUsageAsync_BatchCommandNotFound_RetriesOnlyThatProviderWithSameCommand(bool notifyCompletion)
    {
        _mockSettingsService.SetProviders([new() { Id = "claude" }, new() { Id = "gemini" }, new() { Id = "codex" }]);
        _mockSettingsService.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds = 1;
        const string retryCommand = "codexbar usage --provider gemini --format json --source cli --status --verbose";
        _mockWslService.BatchHandler = (commands, completed, _) =>
        {
            commands[1].Command.Should().Be(retryCommand);
            commands[1].Timeout.Should().Be(TimeSpan.FromSeconds(1));
            IReadOnlyList<WslResult> batch = [
                new() { Success = true, Output = """[{"provider":"claude","usage":{"primary":{"usedPercent":20}}}]""" },
                new() { ExitCode = 127, Error = "codexbar: command not found" },
                new() { Success = true, Output = """[{"provider":"codex","usage":{"primary":{"usedPercent":30}}}]""" }
            ];
            if (notifyCompletion)
                for (var i = 0; i < batch.Count; i++) completed!(i, batch[i]);
            return Task.FromResult(batch);
        };
        _mockWslService.CommandHandler = (command, token) =>
        {
            command.Should().Be(retryCommand);
            token.CanBeCanceled.Should().BeTrue();
            return Task.FromResult(new WslResult
            {
                Success = true, Output = """[{"provider":"gemini","usage":{"primary":{"usedPercent":75}}}]"""
            });
        };

        var results = await _service.GetAllUsageAsync();

        results.Select(r => r.Provider).Should().Equal("claude", "gemini", "codex");
        results.Select(r => r.Session!.Percent).Should().Equal(20, 75, 30);
        results.Should().OnlyContain(r => !r.HasError);
        _mockWslService.ExecutedCommands.Should().Equal(retryCommand);
        _mockWslService.ExecutedBatches.Should().ContainSingle();
        _mockCacheService.SetCallCount.Should().Be(3);
        _mockLogger.Verify(logger => logger.Log(LogLevel.Information, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state != null && state.ToString()!.Contains("gemini")),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_BatchCommandNotFound_IndividualRetryUsesProviderTimeout()
    {
        _mockSettingsService.SetProviders([new() { Id = "gemini" }]);
        _mockSettingsService.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds = 1;
        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            var missing = new WslResult { ExitCode = 127 };
            completed!(0, missing);
            return Task.FromResult<IReadOnlyList<WslResult>>([missing]);
        };
        _mockWslService.CommandHandler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new WslResult();
        };

        var result = await _service.GetAllUsageAsync().WaitAsync(TimeSpan.FromSeconds(5));

        result.Should().ContainSingle().Which.Error.Should().Be("Request timed out");
        _mockWslService.ExecutedCommands.Should().Equal(
            "codexbar usage --provider gemini --format json --source cli --status --verbose");
        _mockWslService.ExecutedBatches.Single().Single().Timeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    private void SetCostResponse(string provider = "claude", bool success = true, string? json = null)
    {
        var command = CodexBarWin.Helpers.CostCommandBuilder.Build(provider, "C:\\Users\\Test", _ => true);
        var cliProvider = provider == "gemini" ? "antigravity" : provider;
        _mockWslService.SetCommandResult(command, new WslResult { Success = success, ExitCode = success ? 0 : 3,
            Output = json ?? $$$"""[{"provider":"{{{cliProvider}}}","sessionCostUSD":1.5,"last30DaysCostUSD":5,"last30DaysTokens":123456,"daily":[]}]""" });
    }

    [TestMethod]
    public async Task GetCostAsync_UncachedProvider_UsesCostScanTimeoutOverload()
    {
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        wsl.Setup(s => s.ExecuteAsync(It.Is<string>(command => command.Contains("--provider codex")),
            CodexBarService.CostScanTimeout, It.IsAny<CancellationToken>())).ReturnsAsync(CostResult("codex"));
        var service = new CodexBarService(wsl.Object, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
            _mockLogger.Object, userProfile: () => "C:\\Users\\Test", directoryExists: _ => true);

        (await service.GetCostAsync("codex"))!.Last30DaysTokens.Should().Be(10);

        wsl.Verify(s => s.ExecuteAsync(It.IsAny<string>(), CodexBarService.CostScanTimeout,
            It.IsAny<CancellationToken>()), Times.Once);
        wsl.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetCostAsync_WhenRepeated_UsesCacheUntilThirtyMinutesAndManualRefreshBypassesIt()
    {
        var clock = new CostClock();
        var service = CostService(clock);
        SetCostResponse();
        var first = await service.GetCostAsync("claude");
        first!.TodayCostUSD.Should().Be(1.5m);
        (await service.GetCostAsync("claude")).Should().BeSameAs(first);
        clock.Now += TimeSpan.FromMinutes(29);
        await service.GetCostAsync("claude");
        _mockWslService.ExecutedCommands.Should().ContainSingle();
        clock.Now += TimeSpan.FromMinutes(1);
        await service.GetCostAsync("claude");
        await service.GetCostAsync("claude", forceRefresh: true);
        _mockWslService.ExecutedCommands.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task GetCostAsync_WhenDifferentProviders_CachesThemIndependently()
    {
        SetCostResponse("claude"); SetCostResponse("codex"); SetCostResponse("gemini");
        var service = CostService();
        foreach (var provider in new[] { "claude", "codex", "gemini" })
            (await service.GetCostAsync(provider))!.Provider.Should().Be(provider);
        _mockWslService.ExecutedCommands.Should().HaveCount(3);
        _mockWslService.ExecutedCommands[2].Should().Be("codexbar cost --provider antigravity --format json --refresh");
    }

    [TestMethod]
    [DataRow(false, "ignored")]
    [DataRow(true, "bad json")]
    [DataRow(true, "[]")]
    [DataRow(true, "[{\"provider\":\"claude\",\"daily\":[]}]")]
    [DataRow(true, "[{\"provider\":\"codex\",\"last30DaysTokens\":15}]")]
    public async Task GetCostAsync_WhenFailedMalformedEmptyOrWrongProvider_HidesSectionAndBacksOff(bool success, string json)
    {
        var clock = new CostClock(); var service = CostService(clock);
        SetCostResponse(success: success, json: json);
        (await service.GetCostAsync("claude")).Should().BeNull();
        (await service.GetCostAsync("claude")).Should().BeNull();
        _mockWslService.ExecutedCommands.Should().ContainSingle();
        clock.Now += TimeSpan.FromMinutes(30); SetCostResponse();
        (await service.GetCostAsync("claude")).Should().NotBeNull();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task GetCostAsync_WhenDisabledOrDeveloperMode_DoesNotScan(bool enabled, bool developer)
    {
        _mockSettingsService.Settings.ShowCostTotals = enabled;
        _mockSettingsService.Settings.DeveloperModeEnabled = developer;
        (await CostService().GetCostAsync("claude")).Should().BeNull();
        _mockWslService.ExecutedCommands.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenCancelled_DoesNotExecuteOrPoisonGate()
    {
        var service = CostService(); var cts = new CancellationTokenSource(); cts.Cancel();
        (await service.GetCostAsync("claude", ct: cts.Token)).Should().BeNull();
        _mockWslService.ExecutedCommands.Should().BeEmpty();
        SetCostResponse();
        (await service.GetCostAsync("claude")).Should().NotBeNull();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenUnsafeProviderOrProfile_DoesNotExecuteShell()
    {
        (await CostService().GetCostAsync("claude; touch x")).Should().BeNull();
        (await CostService(profile: "C:\\Users\\x'bad").GetCostAsync("claude")).Should().BeNull();
        _mockWslService.ExecutedCommands.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCostAsync_WhenSingleObject_ParsesTokenOnlyData()
    {
        SetCostResponse(json: """{"provider":"claude","last30DaysTokens":10}""");
        (await CostService().GetCostAsync("claude"))!.Last30DaysTokens.Should().Be(10);
    }

    private static WslResult CostResult(string provider, long tokens = 10) => new()
    {
        Success = true,
        Output = $$$"""{"provider":"{{{(provider == "gemini" ? "antigravity" : provider)}}}","last30DaysTokens":{{{tokens}}}}"""
    };

    private static async Task<List<(string Provider, CostData? Cost)>> CollectCostsAsync(CodexBarService service,
        IReadOnlyList<string> providers, bool forceRefresh = false, CancellationToken ct = default)
    {
        var results = new List<(string Provider, CostData? Cost)>();
        await foreach (var result in service.GetCostsAsync(providers, forceRefresh, ct)) results.Add(result);
        return results;
    }

    private sealed class MemoryCostStore : ICostCacheStore
    {
        public Dictionary<string, CostData> Items { get; } = [];
        public int LoadCount { get; private set; }
        public List<IReadOnlyDictionary<string, CostData>> Saves { get; } = [];
        public bool SaveSucceeds { get; set; } = true;
        public bool ThrowOnSave { get; set; }
        public Task<IReadOnlyDictionary<string, CostData>> LoadAsync(CancellationToken ct = default)
        {
            LoadCount++;
            return Task.FromResult<IReadOnlyDictionary<string, CostData>>(new Dictionary<string, CostData>(Items));
        }
        public Task<bool> SaveAsync(IReadOnlyDictionary<string, CostData> items, CancellationToken ct = default)
        {
            Saves.Add(new Dictionary<string, CostData>(items));
            if (ThrowOnSave) throw new IOException("Simulated write failure");
            return Task.FromResult(SaveSucceeds);
        }
    }

    private CodexBarService StoredCostService(MemoryCostStore store, CostClock clock) =>
        new(_mockWslService, _mockCacheService, _mockSettingsService, _mockSampleDataLoader, _mockLogger.Object,
            clock, () => "C:\\Users\\Test", _ => true, costStore: store);

    [TestMethod]
    public async Task GetCostAsync_StoreLoadFails_DoesNotOverwriteUnreadCache()
    {
        var store = new Mock<ICostCacheStore>();
        store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Cannot read cache"));
        var service = new CodexBarService(_mockWslService, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
            _mockLogger.Object, costStore: store.Object);
        await service.GetCostAsync("claude");
        store.Verify(s => s.SaveAsync(It.IsAny<IReadOnlyDictionary<string, CostData>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow(124, "")]
    [DataRow(-1, "Command timed out")]
    [DataRow(3, "Command TIMED OUT")]
    public async Task GetCostAsync_ConsecutiveTimeouts_EscalatesToCapAndResetsAfterSuccess(int exitCode, string error)
    {
        var service = CostService(new CostClock { Zone = TimeZoneInfo.Utc });
        _mockWslService.CommandHandler = (_, _) => Task.FromResult(new WslResult { ExitCode = exitCode, Error = error });
        for (var i = 0; i < 5; i++) await service.GetCostAsync("claude", forceRefresh: true);
        _mockWslService.ExecutedCommandTimeouts.Should().Equal(new[] { 5, 10, 20, 30, 30 }.Select(minutes => TimeSpan.FromMinutes(minutes)));
        _mockWslService.CommandHandler = (_, _) => Task.FromResult(CostResult("claude"));
        await service.GetCostAsync("claude", forceRefresh: true);
        await service.GetCostAsync("claude", forceRefresh: true);
        _mockWslService.ExecutedCommandTimeouts.TakeLast(2).Should().Equal(CodexBarService.CostScanTimeoutCap, CodexBarService.CostScanTimeout);
        await service.GetCostAsync("codex", forceRefresh: true);
        _mockWslService.ExecutedCommandTimeouts.Last().Should().Be(CodexBarService.CostScanTimeout);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GetCostAsync_OldLastGood_UsesColdDeadlineForRestoredAndInMemoryReadings(bool restored)
    {
        var clock = new CostClock { Zone = TimeZoneInfo.Utc };
        var store = new MemoryCostStore();
        var old = clock.Now.AddHours(-25).UtcDateTime;
        if (restored) store.Items["claude"] = new CostData { Provider = "claude", TodayTokens = 10, FetchedAt = old, LastSuccessfulFetch = old };
        var service = StoredCostService(store, clock);
        SetCostResponse();
        if (!restored)
        {
            await service.GetCostAsync("claude");
            clock.Now += TimeSpan.FromHours(25);
        }
        await service.GetCostAsync("claude", forceRefresh: true);
        _mockWslService.ExecutedCommandTimeouts.Last().Should().Be(TimeSpan.FromMinutes(20));
        SetCostResponse("codex");
        await service.GetCostAsync("codex");
        _mockWslService.ExecutedCommandTimeouts.Last().Should().Be(CodexBarService.CostScanTimeout);
    }

    [TestMethod]
    [DataRow(124, "", 5)]
    [DataRow(-1, "Command timed out", 5)]
    [DataRow(-1, "Process failed", 30)]
    [DataRow(3, "Scan failed", 30)]
    public async Task GetCostAsync_FailedAttempt_RetriesAfterOutcomeSpecificBackoff(int exitCode, string error, int minutes)
    {
        var clock = new CostClock();
        var service = CostService(clock);
        _mockWslService.CommandHandler = (_, _) => Task.FromResult(new WslResult { ExitCode = exitCode, Error = error });
        await service.GetCostAsync("claude");
        clock.Now += TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1);
        await service.GetCostAsync("claude");
        _mockWslService.ExecutedCommands.Should().ContainSingle();
        clock.Now += TimeSpan.FromSeconds(1);
        await service.GetCostAsync("claude");
        _mockWslService.ExecutedCommands.Should().HaveCount(2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetCostAsync_FailureThenRecovery_PersistsStaleReasonAndOriginalTimestampsThenClearsIt(bool timedOut)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cbw-scan-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new CostClock { Zone = TimeZoneInfo.Utc };
            var store = new CostCacheStore(_mockSettingsService,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CostCacheStore>.Instance, clock, Path.Combine(directory, "cost-cache.json"));
            var service = new CodexBarService(_mockWslService, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
                _mockLogger.Object, clock, () => "C:\\Users\\Test", _ => true, costStore: store);
            SetCostResponse();
            var good = (await service.GetCostAsync("claude"))!;
            clock.Now += TimeSpan.FromMinutes(1);
            _mockWslService.CommandHandler = (_, _) => Task.FromResult(new WslResult { ExitCode = timedOut ? 124 : 3 });
            var failed = (await service.GetCostAsync("claude", forceRefresh: true))!;
            var reason = timedOut ? "Scan timed out after 5 min" : "Scan failed";
            failed.Should().BeEquivalentTo(good with { IsStale = true, StaleReason = reason });
            (await store.LoadAsync())["claude"].Should().BeEquivalentTo(good with { IsStale = true, StaleReason = reason });
            var json = await File.ReadAllTextAsync(store.FilePath);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var entry = document.RootElement.GetProperty("items").GetProperty("claude");
            entry.GetProperty("isStale").GetBoolean().Should().BeTrue();
            entry.GetProperty("staleReason").GetString().Should().Be(reason);
            entry.GetProperty("fetchedAt").GetDateTime().Should().Be(good.FetchedAt);
            entry.GetProperty("lastSuccessfulFetch").GetDateTime().Should().Be(good.LastSuccessfulFetch!.Value);
            _mockWslService.CommandHandler = null;
            var fresh = (await service.GetCostAsync("claude", forceRefresh: true))!;
            fresh.IsStale.Should().BeFalse();
            fresh.StaleReason.Should().BeNull();
            fresh.FetchedAt.Should().Be(clock.Now.UtcDateTime);
            (await store.LoadAsync())["claude"].Should().BeEquivalentTo(fresh);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task GetCostAsync_LocalDayBoundary_StampsAllEntriesOnSaveEvenDuringBackoff()
    {
        var clock = new CostClock
        {
            Zone = TimeZoneInfo.CreateCustomTimeZone("Test +9", TimeSpan.FromHours(9), "Test +9", "Test +9"),
            Now = new DateTimeOffset(2026, 10, 4, 14, 59, 0, TimeSpan.Zero)
        };
        var store = new MemoryCostStore();
        var service = StoredCostService(store, clock);
        SetCostResponse();
        var original = (await service.GetCostAsync("claude"))!;
        clock.Now += TimeSpan.FromMinutes(2);
        await service.GetCostAsync("claude");
        _mockWslService.ExecutedCommands.Should().ContainSingle();
        store.Saves.Should().HaveCount(2);
        store.Saves.Last()["claude"].Should().BeEquivalentTo(original with { IsStale = true });
        SetCostResponse("codex");
        await service.GetCostAsync("codex");
        store.Saves.Last()["claude"].IsStale.Should().BeTrue();
        store.Saves.Last()["codex"].IsStale.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCostsAsync_TimeoutsInBatch_AssignsIndependentProviderDeadlines()
    {
        var service = CostService();
        var pass = 0;
        _mockWslService.BatchHandler = (commands, completed, _) =>
        {
            commands[0].Timeout.Should().Be(TimeSpan.FromMinutes(pass == 0 ? 5 : 10));
            commands[1].Timeout.Should().Be(CodexBarService.CostScanTimeout);
            WslResult[] results = [new() { ExitCode = 124 }, CostResult("codex")];
            for (var i = 0; i < results.Length; i++) completed!(i, results[i]);
            pass++;
            return Task.FromResult<IReadOnlyList<WslResult>>(results);
        };
        await CollectCostsAsync(service, ["claude", "codex"], forceRefresh: true);
        await CollectCostsAsync(service, ["claude", "codex"], forceRefresh: true);
        pass.Should().Be(2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetCostAsync_StoreSaveFails_RetriesCachedSnapshotUntilSaveSucceeds(bool throws)
    {
        var store = new MemoryCostStore { SaveSucceeds = false, ThrowOnSave = throws };
        var service = StoredCostService(store, new CostClock());
        SetCostResponse();
        var cost = await service.GetCostAsync("claude");
        await service.GetCostAsync("claude");
        store.Saves.Should().HaveCount(2);
        store.Saves.Should().OnlyContain(snapshot => ReferenceEquals(snapshot["claude"], cost));

        store.SaveSucceeds = true;
        store.ThrowOnSave = false;
        await service.GetCostAsync("claude");
        await service.GetCostAsync("claude");

        store.Saves.Should().HaveCount(3);
        _mockWslService.ExecutedCommands.Should().ContainSingle();
    }

    [TestMethod]
    public async Task GetCostsAsync_CanceledAfterPartialForcedPass_RetainsCompletedCacheAndRestoresOnlyUnfinishedAttempt()
    {
        var clock = new CostClock();
        var service = CostService(clock);
        SetCostResponse("codex");
        SetCostResponse("claude");
        await service.GetCostAsync("codex");
        var previousClaude = await service.GetCostAsync("claude");
        var oldAttempt = clock.Now.UtcDateTime;
        clock.Now += TimeSpan.FromMinutes(1);
        using var cancellation = new CancellationTokenSource();
        _mockWslService.BatchHandler = (_, completed, ct) =>
        {
            completed!(0, CostResult("codex", 500));
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WslResult>>([]);
        };

        Func<Task> scan = () => CollectCostsAsync(service, ["codex", "claude"], forceRefresh: true, cancellation.Token);
        await scan.Should().ThrowAsync<OperationCanceledException>();

        var entries = service.GetCostCacheInfo().ToDictionary(entry => entry.ProviderId);
        entries["codex"].LastAttemptUtc.Should().Be(clock.Now.UtcDateTime);
        entries["codex"].HasData.Should().BeTrue();
        entries["claude"].LastAttemptUtc.Should().Be(oldAttempt);
        entries["claude"].HasData.Should().BeTrue();
        (await service.GetCostAsync("codex"))!.Last30DaysTokens.Should().Be(500);
        (await service.GetCostAsync("claude")).Should().BeSameAs(previousClaude);
        _mockWslService.ExecutedCommands.Should().HaveCount(2);
        _mockWslService.ExecutedBatches.Should().ContainSingle();
    }

    [TestMethod]
    public async Task GetCostAsync_RescanFailsOrTimesOut_KeepsLastGoodTotals()
    {
        var clock = new CostClock(); var service = CostService(clock);
        SetCostResponse();
        var good = await service.GetCostAsync("claude");
        good!.Last30DaysCostUSD.Should().Be(5m);
        SetCostResponse(success: false, json: "Command timed out");
        (await service.GetCostAsync("claude", forceRefresh: true)).Should().BeEquivalentTo(good! with { IsStale = true, StaleReason = "Scan failed" });
        // The failure still backs off; the cached answer during backoff is the last good result, not nothing.
        clock.Now += TimeSpan.FromMinutes(10);
        (await service.GetCostAsync("claude")).Should().BeEquivalentTo(good! with { IsStale = true, StaleReason = "Scan failed" });
        _mockWslService.ExecutedCommands.Should().HaveCount(2);
        service.GetCostCacheInfo().Single().HasData.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCostAsync_LastGoodFromEarlierDay_RetainsTodayAndThirtyDayTotalsAsStale()
    {
        var clock = new CostClock(); var service = CostService(clock);
        SetCostResponse();
        await service.GetCostAsync("claude");
        clock.Now += TimeSpan.FromDays(1);
        SetCostResponse(success: false, json: "Command timed out");
        var stale = await service.GetCostAsync("claude");
        stale!.TodayCostUSD.Should().Be(1.5m);
        stale.IsStale.Should().BeTrue();
        stale.TodayTokens.Should().Be(0);
        stale.Last30DaysCostUSD.Should().Be(5m);
        stale.Last30DaysTokens.Should().Be(123456);
    }

    [TestMethod]
    public async Task GetCostsAsync_StoredResults_PublishedBeforeScanThenReplacedAndSaved()
    {
        var clock = new CostClock();
        var store = new MemoryCostStore();
        store.Items["codex"] = new CostData { Provider = "codex", Last30DaysCostUSD = 38m, Last30DaysTokens = 99,
            FetchedAt = clock.Now.UtcDateTime.AddHours(-1) };
        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            IReadOnlyList<WslResult> batch = [CostResult("codex", tokens: 500)];
            completed!(0, batch[0]);
            return Task.FromResult(batch);
        };
        var service = StoredCostService(store, clock);

        var results = await CollectCostsAsync(service, ["codex"]);

        results.Select(r => r.Cost!.Last30DaysTokens).Should().Equal(99, 500);
        store.Saves.Should().ContainSingle().Which["codex"].Last30DaysTokens.Should().Be(500);
        // Restored results are shown once; later scans publish only their own outcome.
        clock.Now += TimeSpan.FromMinutes(31);
        (await CollectCostsAsync(service, ["codex"])).Select(r => r.Cost!.Last30DaysTokens).Should().Equal(500);
        store.LoadCount.Should().Be(1);
    }

    [TestMethod]
    public async Task GetCostsAsync_StoredResultAndTimedOutScan_KeepsStoredResultAndSavesStaleReason()
    {
        var clock = new CostClock();
        var store = new MemoryCostStore();
        var stored = new CostData { Provider = "codex", Last30DaysCostUSD = 38m, Last30DaysTokens = 99, FetchedAt = clock.Now.UtcDateTime };
        store.Items["codex"] = stored;
        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            IReadOnlyList<WslResult> batch = [new() { ExitCode = 124, Error = "Command timed out" }];
            completed!(0, batch[0]);
            return Task.FromResult(batch);
        };

        var results = await CollectCostsAsync(StoredCostService(store, clock), ["codex"]);

        results.Should().HaveCount(2);
        results[0].Cost.Should().BeEquivalentTo(stored with { IsStale = true, LastSuccessfulFetch = stored.FetchedAt });
        results[1].Cost.Should().BeEquivalentTo(stored with
        {
            IsStale = true, StaleReason = "Scan timed out after 5 min", LastSuccessfulFetch = stored.FetchedAt
        });
        store.Saves.Should().ContainSingle().Which["codex"].Should().BeEquivalentTo(stored with
        {
            IsStale = true, StaleReason = "Scan timed out after 5 min"
        });
    }

    [TestMethod]
    public async Task GetCostsAsync_CanceledAfterOneProviderCompleted_StillSavesThatResult()
    {
        var store = new MemoryCostStore();
        _mockWslService.BatchHandler = async (_, completed, ct) =>
        {
            completed!(0, CostResult("codex", tokens: 500));
            await Task.Delay(Timeout.Infinite, ct); // Claude is still scanning when the dashboard closes.
            return [];
        };
        var service = StoredCostService(store, new CostClock());

        await foreach (var (provider, cost) in service.GetCostsAsync(["codex", "claude"]))
        {
            provider.Should().Be("codex");
            cost!.Last30DaysTokens.Should().Be(500);
            break; // Disposing the stream cancels the scan, as closing the dashboard does.
        }

        store.Saves.Should().ContainSingle().Which.Keys.Should().Equal("codex");
        service.GetCostCacheInfo().Should().ContainSingle().Which.HasData.Should().BeTrue();
        (await service.GetCostAsync("codex"))!.Last30DaysTokens.Should().Be(500);
        _mockWslService.ExecutedCommands.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCostAsync_LastGoodOlderThanSevenDays_IsServedIndefinitelyAsStale()
    {
        var clock = new CostClock(); var service = CostService(clock);
        SetCostResponse();
        await service.GetCostAsync("claude");
        clock.Now += TimeSpan.FromDays(8);
        SetCostResponse(success: false, json: "Command timed out");
        var stale = await service.GetCostAsync("claude");
        stale.Should().NotBeNull();
        stale!.Last30DaysTokens.Should().Be(123456);
        stale.IsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCostAsync_LastGoodWithinSevenDays_IsStillServedAsStale()
    {
        var clock = new CostClock(); var service = CostService(clock);
        SetCostResponse();
        await service.GetCostAsync("claude");
        clock.Now += TimeSpan.FromDays(3);
        SetCostResponse(success: false, json: "Command timed out");
        var stale = await service.GetCostAsync("claude");
        stale!.Last30DaysTokens.Should().Be(123456);
        stale.IsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetCostsAsync_DeveloperModeOrDisabled_NeitherLoadsNorSavesStore()
    {
        var store = new MemoryCostStore();
        _mockSettingsService.Settings.DeveloperModeEnabled = true;
        (await CollectCostsAsync(StoredCostService(store, new CostClock()), ["claude"])).Should().BeEmpty();
        store.LoadCount.Should().Be(0);
        store.Saves.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCostsAsync_ThreeProviders_RunsOneBatchAndYieldsInCompletionOrder()
    {
        _mockSettingsService.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds = 75;
        _mockWslService.BatchHandler = (commands, completed, _) =>
        {
            commands.Should().HaveCount(3);
            // Cost scans use their own background deadline, never shorter than the provider fetch timeout.
            commands.Should().OnlyContain(command => command.Timeout == CodexBarService.CostScanTimeout);
            commands[2].Command.Should().Be("codexbar cost --provider antigravity --format json --refresh");
            IReadOnlyList<WslResult> batch = [CostResult("claude"), CostResult("codex"), CostResult("gemini")];
            completed!(2, batch[2]); completed(0, batch[0]); completed(1, batch[1]);
            return Task.FromResult(batch);
        };

        var results = await CollectCostsAsync(CostService(), ["claude", "codex", "gemini"]);

        results.Select(result => result.Provider).Should().Equal("gemini", "claude", "codex");
        results.Should().OnlyContain(result => result.Cost != null && result.Cost.Provider == result.Provider);
        _mockWslService.ExecutedBatches.Should().ContainSingle();
        _mockWslService.ExecutedCommands.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCostsAsync_ProviderCompletesBeforeBatch_YieldsWithoutWaitingForOtherProviders()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<WslResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            completed!(1, CostResult("codex"));
            return pending.Task;
        };
        await using var stream = CostService().GetCostsAsync(["claude", "codex"]).GetAsyncEnumerator();
        (await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        stream.Current.Provider.Should().Be("codex");
        pending.Task.IsCompleted.Should().BeFalse();
        pending.SetResult([CostResult("claude"), CostResult("codex")]);
        (await stream.MoveNextAsync()).Should().BeTrue();
        stream.Current.Provider.Should().Be("claude");
        (await stream.MoveNextAsync()).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetCostsAsync_FreshCacheSkipsBatchUntilThirtyMinuteBoundaryAndForceBypassesIt()
    {
        var clock = new CostClock();
        var service = CostService(clock);
        _mockWslService.BatchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<WslResult>>([CostResult("claude")]);
        var initial = (await CollectCostsAsync(service, ["claude"])).Single().Cost;
        clock.Now += TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1);
        (await CollectCostsAsync(service, ["claude"])).Single().Cost.Should().BeSameAs(initial);
        _mockWslService.ExecutedBatches.Should().ContainSingle();
        clock.Now += TimeSpan.FromTicks(1);
        await CollectCostsAsync(service, ["claude"]);
        await CollectCostsAsync(service, ["claude"], forceRefresh: true);
        _mockWslService.ExecutedBatches.Should().HaveCount(3);
        _mockWslService.ExecutedCommands.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetCostsAsync_PartialCache_BatchesOnlyDueProviders()
    {
        var clock = new CostClock(); var service = CostService(clock);
        SetCostResponse("claude"); await service.GetCostAsync("claude");
        _mockWslService.BatchHandler = (commands, _, _) =>
        {
            commands.Should().ContainSingle().Which.Command.Should().Contain("--provider codex");
            return Task.FromResult<IReadOnlyList<WslResult>>([CostResult("codex")]);
        };
        var results = await CollectCostsAsync(service, ["claude", "codex"]);
        results.Select(result => result.Provider).Should().Equal("claude", "codex");
        _mockWslService.ExecutedBatches.Should().ContainSingle();
        _mockWslService.ExecutedCommands.Should().ContainSingle();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GetCostsAsync_CommandNotFound_RetriesOnlyMissingProviderInSingleShell(bool notifyCompletion)
    {
        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            IReadOnlyList<WslResult> batch = [CostResult("claude"), new() { ExitCode = 127 }];
            if (notifyCompletion) { completed!(0, batch[0]); completed(1, batch[1]); }
            return Task.FromResult(batch);
        };
        _mockWslService.CommandHandler = (command, token) =>
        {
            token.CanBeCanceled.Should().BeTrue();
            command.Should().Be("codexbar cost --provider antigravity --format json --refresh");
            return Task.FromResult(CostResult("gemini"));
        };
        var service = CostService();
        var results = await CollectCostsAsync(service, ["claude", "gemini"]);
        results.Should().HaveCount(2).And.OnlyContain(result => result.Cost != null);
        _mockWslService.ExecutedBatches.Should().ContainSingle();
        _mockWslService.ExecutedCommands.Should().ContainSingle();
        _mockWslService.ExecutedCommandTimeouts.Should().Equal(CodexBarService.CostScanTimeout);
        (await service.GetCostAsync("gemini")).Should().BeSameAs(results.Single(r => r.Provider == "gemini").Cost);
        _mockWslService.ExecutedCommands.Should().ContainSingle();
    }

    [TestMethod]
    public async Task GetCostsAsync_BrokenFraming_RetriesOnlyUnfinishedProviders()
    {
        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            completed!(0, CostResult("claude"));
            return Task.FromException<IReadOnlyList<WslResult>>(new InvalidDataException("Missing frame"));
        };
        _mockWslService.CommandHandler = (_, _) => Task.FromResult(CostResult("codex"));
        var results = await CollectCostsAsync(CostService(), ["claude", "codex"]);
        results.Should().HaveCount(2).And.OnlyContain(result => result.Cost != null);
        _mockWslService.ExecutedCommands.Should().ContainSingle().Which.Should().Contain("--provider codex");
        _mockWslService.ExecutedCommandTimeouts.Should().Equal(CodexBarService.CostScanTimeout);
    }

    [TestMethod]
    public async Task GetCostsAsync_BackoffAfterInvalidResult_SkipsBatchForThirtyMinutes()
    {
        var clock = new CostClock(); var service = CostService(clock);
        _mockWslService.BatchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<WslResult>>([new() { Success = true, Output = "bad json" }]);
        (await CollectCostsAsync(service, ["claude"])).Single().Cost.Should().BeNull();
        clock.Now += TimeSpan.FromMinutes(29);
        (await CollectCostsAsync(service, ["claude"])).Single().Cost.Should().BeNull();
        _mockWslService.ExecutedBatches.Should().ContainSingle();
        clock.Now += TimeSpan.FromMinutes(1);
        await CollectCostsAsync(service, ["claude"]);
        _mockWslService.ExecutedBatches.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task GetCostsAsync_CancelledButBackendReturns_RestoresCacheAndReleasesGate()
    {
        var clock = new CostClock(); var service = CostService(clock);
        SetCostResponse(); var initial = await service.GetCostAsync("claude");
        var initialInfo = service.GetCostCacheInfo().Single();
        var scan = new TaskCompletionSource<IReadOnlyList<WslResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockWslService.BatchHandler = (_, _, _) => scan.Task;
        clock.Now += TimeSpan.FromMinutes(1);
        using var cts = new CancellationTokenSource();
        var pending = CollectCostsAsync(service, ["claude", "codex"], forceRefresh: true, ct: cts.Token);
        cts.Cancel(); scan.SetResult([CostResult("claude", 20), CostResult("codex")]);
        Func<Task> complete = async () => await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await complete.Should().ThrowAsync<OperationCanceledException>();
        service.GetCostCacheInfo().Should().ContainSingle().Which.Should().BeEquivalentTo(initialInfo);
        (await service.GetCostAsync("claude")).Should().BeSameAs(initial);
        SetCostResponse("codex");
        (await service.GetCostAsync("codex")).Should().NotBeNull();
    }

    [TestMethod]
    public async Task GetCostsAsync_DisposedAfterPartialCompletion_CancelsBackendRetainsCompletedCacheAndRetriesUnfinishedProvider()
    {
        var service = CostService();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken backendToken = default;
        _mockWslService.BatchHandler = async (_, completed, token) =>
        {
            backendToken = token;
            completed!(0, CostResult("claude")); started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return [];
        };
        var stream = service.GetCostsAsync(["claude", "codex"]).GetAsyncEnumerator();
        (await stream.MoveNextAsync()).Should().BeTrue();
        var completedCost = stream.Current.Cost;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        backendToken.IsCancellationRequested.Should().BeTrue();
        service.GetCostCacheInfo().Should().ContainSingle().Which.ProviderId.Should().Be("claude");
        (await service.GetCostAsync("claude")).Should().BeSameAs(completedCost);
        _mockWslService.ExecutedCommands.Should().BeEmpty();
        SetCostResponse("codex");
        (await service.GetCostAsync("codex")).Should().NotBeNull();
        _mockWslService.ExecutedCommands.Should().ContainSingle().Which.Should().Contain("--provider codex");
    }

    [TestMethod]
    public async Task GetCostsAsync_DisabledDuringBatch_DoesNotPublishTotalsOrBackOff()
    {
        var service = CostService();
        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            _mockSettingsService.Settings.ShowCostTotals = false;
            var result = CostResult("claude"); completed!(0, result);
            return Task.FromResult<IReadOnlyList<WslResult>>([result]);
        };
        (await CollectCostsAsync(service, ["claude"])).Single().Cost.Should().BeNull();
        service.GetCostCacheInfo().Should().BeEmpty();
        _mockSettingsService.Settings.ShowCostTotals = true;
        SetCostResponse(); (await service.GetCostAsync("claude")).Should().NotBeNull();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task GetCostsAsync_DisabledOrDeveloperMode_DoesNotRunBatch(bool enabled, bool developer)
    {
        _mockSettingsService.Settings.ShowCostTotals = enabled;
        _mockSettingsService.Settings.DeveloperModeEnabled = developer;
        (await CollectCostsAsync(CostService(), ["claude", "codex", "gemini"])).Should().BeEmpty();
        _mockWslService.ExecutedBatches.Should().BeEmpty();
        _mockWslService.ExecutedCommands.Should().BeEmpty();
    }

    private CodexBarService LedgerCostService(ICostLedger ledger, CostClock? clock = null) =>
        new(_mockWslService, _mockCacheService, _mockSettingsService, _mockSampleDataLoader, _mockLogger.Object,
            clock ?? new CostClock(), () => "C:\\Users\\Test", _ => true, costLedger: ledger);

    [TestMethod]
    public async Task GetCostAsync_RotatedHistory_FreshCachedAndFailedScansKeepDateWiseLedgerUnion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cbw-cost-union-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new CostClock { Zone = TimeZoneInfo.Utc };
            var ledger = new CostLedger(_mockSettingsService, Microsoft.Extensions.Logging.Abstractions.NullLogger<CostLedger>.Instance,
                clock, Path.Combine(directory, "cost-ledger.json"), Path.Combine(directory, "cost-cache.json"));
            await ledger.RecordScanAsync(new CostData
            {
                Provider = "claude", FetchOutcome = FetchOutcome.Success,
                Daily = [new() { Date = new(2026, 10, 1), TotalCost = 20m, TotalTokens = 2000 },
                    new() { Date = new(2026, 10, 3), TotalCost = 2m, TotalTokens = 200 }]
            });
            var service = LedgerCostService(ledger, clock);
            SetCostResponse(json: """
                {"provider":"claude","sessionCostUSD":5,"sessionTokens":500,"last30DaysCostUSD":8,"last30DaysTokens":800,
                 "provenance":"live logs","daily":[{"date":"2026-10-03","totalCost":3,"totalTokens":300},
                   {"date":"2026-10-04","totalCost":5,"totalTokens":500}]}
                """);
            var fresh = (await service.GetCostAsync("claude"))!;
            fresh.Last30DaysCostUSD.Should().Be(28m);
            fresh.Last30DaysTokens.Should().Be(2800);
            fresh.TodayCostUSD.Should().Be(5m);
            fresh.Provenance.Should().Be("live logs");
            fresh.IsStale.Should().BeFalse();
            (await service.GetCostAsync("claude"))!.Last30DaysCostUSD.Should().Be(28m);
            SetCostResponse(success: false);
            var stale = (await service.GetCostAsync("claude", forceRefresh: true))!;
            stale.Last30DaysCostUSD.Should().Be(28m);
            stale.IsStale.Should().BeTrue();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task GetCostsAsync_LedgerNotYetMerged_PublishesPureDateWiseUnionAndRecordsOnlyRawScan()
    {
        var ledger = new Mock<ICostLedger>();
        ledger.Setup(l => l.GetLast30Days("claude", It.IsAny<DateOnly?>())).Returns(new CostLedgerLast30DaysResult
        {
            DaysWithDataCount = 2, TotalCostUSD = 22m, TotalTokens = 2200,
            Days = [new() { Date = new(2026, 10, 1), HasLedgerRecord = true, CostUSD = 20m, TotalTokens = 2000 },
                new() { Date = new(2026, 10, 3), HasLedgerRecord = true, CostUSD = 2m, TotalTokens = 200 }]
        });
        ledger.Setup(l => l.RecordScansAsync(It.IsAny<IReadOnlyList<CostData>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            var result = new WslResult { Success = true, Output = """
                {"provider":"claude","sessionCostUSD":5,"sessionTokens":500,"last30DaysCostUSD":8,"last30DaysTokens":800,
                 "daily":[{"date":"2026-10-03","totalCost":3,"totalTokens":300},
                   {"date":"2026-10-04","totalCost":5,"totalTokens":500}]}
                """ };
            completed!(0, result);
            return Task.FromResult<IReadOnlyList<WslResult>>([result]);
        };
        var costs = await CollectCostsAsync(LedgerCostService(ledger.Object, new CostClock { Zone = TimeZoneInfo.Utc }), ["claude"]);
        costs.Single().Cost!.Last30DaysCostUSD.Should().Be(28m);
        ledger.Verify(l => l.RecordScansAsync(It.Is<IReadOnlyList<CostData>>(s => s.Count == 1 && s[0].Last30DaysCostUSD == 8m),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetCostsAsync_BatchScan_CallsRecordScansAsyncOnceWithAllSuccessfulProviders()
    {
        var ledger = new Mock<ICostLedger>();
        var tcs = new TaskCompletionSource<IReadOnlyList<CostData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ledger.Setup(l => l.RecordScansAsync(It.IsAny<IReadOnlyList<CostData>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<CostData>, DateTime?, CancellationToken>((scans, _, _) => tcs.TrySetResult(scans))
            .Returns(Task.CompletedTask);

        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            IReadOnlyList<WslResult> batch = [CostResult("claude", tokens: 100), CostResult("codex", tokens: 200)];
            completed!(0, batch[0]);
            completed(1, batch[1]);
            return Task.FromResult(batch);
        };

        var service = LedgerCostService(ledger.Object);
        var results = await CollectCostsAsync(service, ["claude", "codex"]);
        results.Should().HaveCount(2);

        var recordedScans = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        recordedScans.Should().HaveCount(2);
        recordedScans.Select(s => s.Provider).Should().BeEquivalentTo(["claude", "codex"]);

        ledger.Verify(l => l.RecordScansAsync(It.IsAny<IReadOnlyList<CostData>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        ledger.Verify(l => l.RecordScanAsync(It.IsAny<CostData>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetCostsAsync_BatchScan_WhenNoProviderHasData_DoesNotCallRecordScansAsync()
    {
        var ledger = new Mock<ICostLedger>();

        _mockWslService.BatchHandler = (_, completed, _) =>
        {
            IReadOnlyList<WslResult> batch = [new() { Success = false, Error = "failure" }];
            completed!(0, batch[0]);
            return Task.FromResult(batch);
        };

        var service = LedgerCostService(ledger.Object);
        var results = await CollectCostsAsync(service, ["claude"]);
        results.Should().HaveCount(1);
        results[0].Cost.Should().BeNull();

        await Task.Delay(50);

        ledger.Verify(l => l.RecordScansAsync(It.IsAny<IReadOnlyList<CostData>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        ledger.Verify(l => l.RecordScanAsync(It.IsAny<CostData>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_CommandNotFoundAfterIndividualRetry_ReportsCommandNotFound()
    {
        _mockSettingsService.SetProviders([new() { Id = "claude" }]);
        _mockWslService.BatchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<WslResult>>([new() { ExitCode = 127 }]);
        _mockWslService.CommandHandler = (_, _) => Task.FromResult(new WslResult { ExitCode = 127 });
        (await _service.GetAllUsageAsync()).Single().CommandNotFound.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("claude", "")]
    [DataRow("codex", "")]
    [DataRow("gemini", " --verbose")]
    public async Task GetAllUsageAsync_WhenStatusRequested_ParsesHealthWithoutChangingUsage(string provider, string verbose)
    {
        _mockSettingsService.SetProviders([new() { Id = provider, IsEnabled = true }]);
        var command = $"codexbar usage --provider {provider} --format json --source cli --status{verbose}";
        _mockWslService.SetCommandResult(command, new WslResult { Success = true, Output = $$$"""
            [{"provider":"{{{provider}}}","source":"cli","usage":{"primary":{"usedPercent":25}},
              "status":{"indicator":"major","description":"Service outage"}}]
            """ });

        var data = (await _service.GetAllUsageAsync()).Single();
        _mockWslService.ExecutedCommands.Should().Equal(command);
        data.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Major);
        data.Session!.Percent.Should().Be(25);
        data.HasError.Should().BeFalse();
        _mockCacheService.Get(provider)!.ServiceStatus.Should().Be(data.ServiceStatus);
    }

    [TestMethod]
    public async Task GetAllUsageStreamAsync_WhenGeminiHasNoStatus_KeepsUsageWithoutError()
    {
        _mockSettingsService.SetProviders([new() { Id = "gemini", IsEnabled = true }]);
        _mockWslService.SetCommandResult("codexbar usage --provider gemini --format json --source cli --status --verbose",
            new WslResult { Success = true, Output = """[{"provider":"gemini","usage":{"primary":{"usedPercent":10}}}]""" });
        var results = new List<UsageData>();
        await foreach (var data in _service.GetAllUsageStreamAsync()) results.Add(data);

        var result = results.Single();
        result.Session!.Percent.Should().Be(10);
        result.ServiceStatus.Should().BeNull();
        result.HasError.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAllUsageStreamAsync_WhenGeminiHasNoStatusAndFallbackProvided_AttachesFallbackStatus()
    {
        var fallbackStatus = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Minor,
            Description = "Minor disruption",
            Url = "https://www.google.com/appsstatus/dashboard/products/npdyhgECDJ6tB66MxXyo/history"
        };
        var mockFallback = new Mock<IGeminiStatusFallback>();
        mockFallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(fallbackStatus);

        var serviceWithFallback = new CodexBarService(
            _mockWslService, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
            _mockLogger.Object, geminiStatusFallback: mockFallback.Object);

        _mockSettingsService.SetProviders([new() { Id = "gemini", IsEnabled = true }]);
        _mockWslService.SetCommandResult("codexbar usage --provider gemini --format json --source cli --status --verbose",
            new WslResult { Success = true, Output = """[{"provider":"gemini","usage":{"primary":{"usedPercent":10}}}]""" });

        var results = new List<UsageData>();
        await foreach (var data in serviceWithFallback.GetAllUsageStreamAsync()) results.Add(data);

        var result = results.Single();
        result.Session!.Percent.Should().Be(10);
        result.ServiceStatus.Should().Be(fallbackStatus);
        mockFallback.Verify(f => f.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetAllUsageStreamAsync_WhenGeminiAlreadyHasStatus_DoesNotOverwriteWithFallback()
    {
        var mockFallback = new Mock<IGeminiStatusFallback>(MockBehavior.Strict);
        mockFallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ProviderStatus { Indicator = ProviderStatusIndicator.Critical });

        var serviceWithFallback = new CodexBarService(
            _mockWslService, _mockCacheService, _mockSettingsService, _mockSampleDataLoader,
            _mockLogger.Object, geminiStatusFallback: mockFallback.Object);

        _mockSettingsService.SetProviders([new() { Id = "gemini", IsEnabled = true }]);
        _mockWslService.SetCommandResult("codexbar usage --provider gemini --format json --source cli --status --verbose",
            new WslResult { Success = true, Output = """[{"provider":"gemini","usage":{"primary":{"usedPercent":10}},"status":{"indicator":"major","description":"CLI reported major","url":"https://status.gemini.com"}}]""" });

        var results = new List<UsageData>();
        await foreach (var data in serviceWithFallback.GetAllUsageStreamAsync()) results.Add(data);

        var result = results.Single();
        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Major);
        result.ServiceStatus.Description.Should().Be("CLI reported major");
        mockFallback.Verify(f => f.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetUsageAsync_InvalidProvider_ReturnsNull()
    {
        // The service catches all exceptions and returns cached/null data
        // rather than throwing to avoid crashing the UI

        // Act
        var result = await _service.GetUsageAsync("invalid");

        // Assert
        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetUsageAsync_CommandFails_ReturnsCachedData()
    {
        // Arrange
        var cachedData = new UsageData
        {
            Provider = "claude",
            Plan = "Pro",
            FetchedAt = DateTime.UtcNow
        };
        _mockCacheService.PreloadCache("claude", cachedData);

        _mockWslService.SetCommandResult(
            "codexbar --provider claude --format json --source cli --status",
            new WslResult { Success = false, Error = "Command failed", ExitCode = 1 });

        // Act
        var result = await _service.GetUsageAsync("claude");

        // Assert
        result.Should().NotBeNull();
        result!.Provider.Should().Be("claude");
    }

    [TestMethod]
    public async Task GetAllUsageAsync_FiltersInvalidProviders()
    {
        // Arrange
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "invalid", IsEnabled = true },  // Should be filtered
            new() { Id = "codex", IsEnabled = true }
        });

        // Act
        var results = await _service.GetAllUsageAsync();

        // Assert
        // Only valid providers should be fetched
        _mockWslService.ExecutedCommands.Should().HaveCount(2);
        _mockWslService.ExecutedCommands.Any(c => c.Contains("invalid")).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_SkipsDisabledProviders()
    {
        // Arrange
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "codex", IsEnabled = false },
            new() { Id = "gemini", IsEnabled = true }
        });

        // Act
        var results = await _service.GetAllUsageAsync();

        // Assert
        _mockWslService.ExecutedCommands.Should().HaveCount(2);
        // Note: "codexbar" contains "codex", so check for "--provider codex" specifically
        _mockWslService.ExecutedCommands.Any(c => c.Contains("--provider codex")).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_NoEnabledProviders_ReturnsEmpty()
    {
        // Arrange
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = false },
            new() { Id = "codex", IsEnabled = false }
        });

        // Act
        var results = await _service.GetAllUsageAsync();

        // Assert
        results.Should().BeEmpty();
        _mockWslService.ExecutedCommands.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAllUsageStreamAsync_YieldsResultsAsTheyComplete()
    {
        // Arrange
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "gemini", IsEnabled = true }
        });

        // Act
        var results = new List<UsageData>();
        await foreach (var data in _service.GetAllUsageStreamAsync())
        {
            results.Add(data);
        }

        // Assert
        results.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task GetVersionAsync_Success_ReturnsVersion()
    {
        // Arrange
        _mockWslService.SetCommandResult(
            "codexbar --version",
            new WslResult { Success = true, Output = "1.0.0", ExitCode = 0 });

        // Act
        var result = await _service.GetVersionAsync();

        // Assert
        result.Should().Be("1.0.0");
    }

    [TestMethod]
    public async Task GetVersionAsync_Failure_ReturnsNull()
    {
        // Arrange
        _mockWslService.SetCommandResult(
            "codexbar --version",
            new WslResult { Success = false, Error = "Not found", ExitCode = 1 });

        // Act
        var result = await _service.GetVersionAsync();

        // Assert
        result.Should().BeNull();
    }

    [TestMethod]
    public async Task IsAvailableAsync_CodexBarExists_ReturnsTrue()
    {
        // Arrange
        _mockWslService.SetCommandResult(
            "which codexbar",
            new WslResult { Success = true, Output = "/usr/local/bin/codexbar", ExitCode = 0 });

        // Act
        var result = await _service.IsAvailableAsync();

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public async Task IsAvailableAsync_CodexBarNotFound_ReturnsFalse()
    {
        // Arrange
        _mockWslService.SetCommandResult(
            "which codexbar",
            new WslResult { Success = false, Output = "", ExitCode = 1 });

        // Act
        var result = await _service.IsAvailableAsync();

        // Assert
        result.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetUsageAsync_SuccessfulParse_CachesData()
    {
        // Arrange
        // ParseUsageJson expects a single object, not an array
        var jsonResponse = """{"provider":"claude","usage":{"primary":{"usedPercent":75,"windowMinutes":60}}}""";
        _mockWslService.SetCommandResult(
            "codexbar --provider claude --format json --source cli --status",
            new WslResult { Success = true, Output = jsonResponse, ExitCode = 0 });

        // Act
        await _service.GetUsageAsync("claude");

        // Assert
        _mockCacheService.SetCallCount.Should().Be(1);
    }

    #region Developer Mode Tests

    [TestMethod]
    public async Task GetAllUsageAsync_DeveloperModeEnabled_UsesSampleData()
    {
        // Arrange
        _mockSettingsService.Settings.DeveloperModeEnabled = true;
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "gemini", IsEnabled = true }
        });

        var claudeSample = """[{"provider":"claude","usage":{"primary":{"usedPercent":45.5,"windowMinutes":60}}}]""";
        var geminiSample = """[{"provider":"gemini","usage":{"primary":{"usedPercent":32.8,"windowMinutes":60}}}]""";
        _mockSampleDataLoader.SetSampleData("claude", claudeSample);
        _mockSampleDataLoader.SetSampleData("gemini", geminiSample);

        // Act
        var results = await _service.GetAllUsageAsync();

        // Assert
        results.Should().HaveCount(2);
        _mockWslService.ExecutedCommands.Should().BeEmpty(); // Should NOT execute WSL commands
        _mockCacheService.SetCallCount.Should().Be(0, "developer samples must never overwrite the real usage cache");
    }

    [TestMethod]
    public async Task GetAllUsageAsync_DeveloperModeDisabled_UsesWsl()
    {
        // Arrange
        _mockSettingsService.Settings.DeveloperModeEnabled = false;
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = true }
        });

        // Act
        var results = await _service.GetAllUsageAsync();

        // Assert
        _mockWslService.ExecutedCommands.Should().NotBeEmpty(); // Should execute WSL commands
    }

    [TestMethod]
    public async Task GetAllUsageAsync_DeveloperMode_SampleFileNotFound_ReturnsError()
    {
        // Arrange
        _mockSettingsService.Settings.DeveloperModeEnabled = true;
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = true }
        });

        // Don't set sample data - simulate file not found
        _mockSampleDataLoader.SetSampleData("claude", null);

        // Act
        var results = await _service.GetAllUsageAsync();

        // Assert
        results.Should().HaveCount(1);
        results[0].Error.Should().Contain("Sample data not available");
    }

    #endregion

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task GetAllUsageStreamAsync_StatusPending_PublishesFastProviderBeforeSlowBatch(bool completeStatus, bool batchFails)
    {
        _mockSettingsService.SetProviders([new() { Id = "claude" }, new() { Id = "gemini" }]);
        var status = new TaskCompletionSource<ProviderStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var incidents = new Mock<IProviderIncidentFetcher>();
        incidents.Setup(s => s.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).Returns(status.Task);
        static WslResult Usage(string provider) => new() { Success = true,
            Output = "[{\"provider\":\"" + provider + "\",\"usage\":{\"primary\":{\"usedPercent\":20}}}]" };
        _mockWslService.BatchHandler = async (_, callback, ct) =>
        {
            callback!(0, Usage("claude")); frame.SetResult();
            await release.Task.WaitAsync(ct);
            if (batchFails) throw new InvalidDataException("missing sibling frame");
            callback(1, Usage("gemini"));
            return [Usage("claude"), Usage("gemini")];
        };
        _mockWslService.CommandHandler = (_, _) => Task.FromResult(Usage("gemini"));
        var service = new CodexBarService(_mockWslService, _mockCacheService, _mockSettingsService,
            _mockSampleDataLoader, _mockLogger.Object, incidentFetcher: incidents.Object);
        await using var stream = service.GetAllUsageStreamAsync().GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        try
        {
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (completeStatus) status.SetResult(new() { Indicator = ProviderStatusIndicator.Minor });
            (await first.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
            stream.Current.Provider.Should().Be("claude");
            stream.Current.Session!.Percent.Should().Be(20);
            if (completeStatus) stream.Current.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
            release.Task.IsCompleted.Should().BeFalse();
        }
        finally
        {
            status.TrySetResult(null); release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
        (await stream.MoveNextAsync()).Should().BeTrue(); stream.Current.Provider.Should().Be("gemini");
        (await stream.MoveNextAsync()).Should().BeFalse();
        _mockCacheService.SetCallCount.Should().Be(2);
    }

    [TestMethod]
    public async Task GetCostsAsync_DisposedDuringScan_DrainsCompletedLedgerWrites()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new Mock<ICostLedger>();
        ledger.Setup(l => l.RecordScansAsync(It.IsAny<IReadOnlyList<CostData>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<CostData> scans, DateTime? _, CancellationToken ct) =>
            {
                scans.Should().ContainSingle().Which.Provider.Should().Be("claude");
                ct.IsCancellationRequested.Should().BeFalse(); started.SetResult(); return written.Task;
            });
        _mockWslService.BatchHandler = async (_, callback, ct) =>
        {
            callback!(0, CostResult("claude", tokens: 100));
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };
        var stream = LedgerCostService(ledger.Object).GetCostsAsync(["claude", "codex"]).GetAsyncEnumerator();
        (await stream.MoveNextAsync()).Should().BeTrue();
        var disposal = stream.DisposeAsync().AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(disposal, Task.Delay(150));
            disposal.IsCompleted.Should().BeFalse();
        }
        finally { written.TrySetResult(); await disposal.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task GetCostsAsync_LedgerWriteFails_ReleasesGateAndAllowsNextScan()
    {
        var ledger = new Mock<ICostLedger>();
        ledger.Setup(l => l.RecordScansAsync(It.IsAny<IReadOnlyList<CostData>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk unavailable"));
        _mockWslService.BatchHandler = (_, callback, _) =>
        {
            var cost = CostResult("claude", tokens: 100); callback!(0, cost);
            return Task.FromResult<IReadOnlyList<WslResult>>([cost]);
        };
        var service = LedgerCostService(ledger.Object);
        (await CollectCostsAsync(service, ["claude"])).Single().Cost.Should().NotBeNull();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var result in service.GetCostsAsync(["claude"], forceRefresh: true).WithCancellation(deadline.Token))
            result.Cost.Should().NotBeNull();
        ledger.Verify(l => l.RecordScansAsync(It.IsAny<IReadOnlyList<CostData>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
