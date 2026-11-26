using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class CodexBarBatchTests
{
    private readonly MockWslService _wsl = new();
    private readonly MockSettingsService _settings = new();
    private readonly MockCacheService _cache = new();
    private readonly MockSampleDataLoader _samples = new();
    private CodexBarService Service() => new(_wsl, _cache, _settings, _samples, NullLogger<CodexBarService>.Instance);
    private static WslResult Data(string provider) => new()
    {
        Success = true, Output = $$$$"""[{"provider":"{{{{provider}}}}","usage":{"primary":{"usedPercent":20}}}]"""
    };

    [TestMethod]
    public async Task GetAllUsageAsync_CommandNotFoundFallback_RecordsActivityWithoutChangingSuccess()
    {
        _settings.SetProviders([new() { Id = "claude" }]);
        _wsl.BatchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<WslResult>>([new() { ExitCode = 127 }]);
        _wsl.CommandHandler = (_, _) => Task.FromResult(Data("claude"));
        var activity = new ActivityLog();
        var service = new CodexBarService(_wsl, _cache, _settings, _samples,
            NullLogger<CodexBarService>.Instance, activityLog: activity);
        (await service.GetAllUsageAsync()).Single().HasError.Should().BeFalse();
        activity.RecentRefreshes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProviderId = "claude", Success = false, Message = "WSL batch fallback: Command failed (exit code 127)"
        });
    }

    [TestMethod]
    public async Task GetAllUsageAsync_BrokenBatchFallback_RecordsFailureReason()
    {
        _settings.SetProviders([new() { Id = "claude" }]);
        _wsl.BatchHandler = (_, _, _) => throw new InvalidDataException("missing frames");
        _wsl.CommandHandler = (_, _) => Task.FromResult(Data("claude"));
        var activity = new ActivityLog();
        var service = new CodexBarService(_wsl, _cache, _settings, _samples,
            NullLogger<CodexBarService>.Instance, activityLog: activity);
        await service.GetAllUsageAsync();
        activity.RecentRefreshes.Should().ContainSingle().Which.Message.Should().Be("WSL batch fallback: missing frames");
    }

    [TestMethod]
    public async Task GetAllUsageAsync_BatchCompletesOutOfOrder_ReturnsInputOrderAndUsesOneBatch()
    {
        _settings.SetProviders([new() { Id = "claude" }, new() { Id = "gemini" },
            new() { Id = "invalid" }, new() { Id = "codex", IsEnabled = false }]);
        _wsl.BatchHandler = (commands, completed, ct) =>
        {
            commands.Count.Should().Be(2);
            commands[0].Command.Should().Be("codexbar usage --provider claude --format json --source cli --status");
            commands[1].Command.Should().EndWith("--status --verbose");
            completed!(1, Data("gemini"));
            completed(0, Data("claude"));
            return Task.FromResult<IReadOnlyList<WslResult>>([Data("claude"), Data("gemini")]);
        };
        var data = await Service().GetAllUsageAsync();
        data.Select(d => d.Provider).Should().Equal("claude", "gemini");
        _wsl.ExecutedBatches.Should().ContainSingle();
        _wsl.ExecutedCommands.Should().BeEmpty();
        _cache.SetCallCount.Should().Be(2);
    }

    [TestMethod]
    public async Task GetAllUsageStreamAsync_FastCompletionBeforeBatchFinishes_YieldsImmediatelyInCompletionOrder()
    {
        _settings.SetProviders([new() { Id = "claude" }, new() { Id = "gemini" }]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wsl.BatchHandler = async (_, completed, ct) =>
        {
            completed!(1, Data("gemini"));
            await release.Task.WaitAsync(ct);
            completed(0, Data("claude"));
            return [Data("claude"), Data("gemini")];
        };
        await using var stream = Service().GetAllUsageStreamAsync().GetAsyncEnumerator();
        (await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        stream.Current.Provider.Should().Be("gemini");
        release.TrySetResult();
        (await stream.MoveNextAsync()).Should().BeTrue();
        stream.Current.Provider.Should().Be("claude");
        (await stream.MoveNextAsync()).Should().BeFalse();
        _wsl.ExecutedBatches.Should().ContainSingle();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_ProviderResults_PreservesErrorsAndSuccessfulCache()
    {
        _settings.SetProviders([new() { Id = "claude" }, new() { Id = "codex" }, new() { Id = "gemini" }]);
        _wsl.BatchHandler = (_, completed, _) =>
        {
            IReadOnlyList<WslResult> results = [new() { ExitCode = 124, Error = "deadline", Output = "partial" },
                new() { Success = true, Output = "[]" }, new() { ExitCode = 7 }];
            for (var i = 0; i < results.Count; i++) completed!(i, results[i]);
            return Task.FromResult(results);
        };
        var results = await Service().GetAllUsageAsync();
        results.Select(r => r.Error).Should().Equal("Request timed out", "No data available", "Command failed (exit code 7)");
        _cache.SetCallCount.Should().Be(0);
    }

    [TestMethod]
    [DataRow(-1, "Command timed out", "", false, "Request timed out")]
    [DataRow(0, "", "", true, "Command failed (exit code 0)")]
    [DataRow(0, " warning ", " ", true, "warning")]
    [DataRow(3, " specific failure \n", "", false, "specific failure")]
    [DataRow(0, "", "bad json", true, "No data available")]
    public async Task GetAllUsageAsync_ResultMappings_PreservesExistingMessages(int exit, string error, string output,
        bool success, string expected)
    {
        _settings.SetProviders([new() { Id = "claude" }]);
        _wsl.BatchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<WslResult>>(
            [new() { ExitCode = exit, Error = error, Output = output, Success = success }]);
        (await Service().GetAllUsageAsync()).Single().Error.Should().Be(expected);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_BatchFailsAfterCompletion_RetriesOnlyUnfinishedProvider()
    {
        _settings.SetProviders([new() { Id = "claude" }, new() { Id = "gemini" }]);
        _wsl.BatchHandler = (_, completed, _) =>
        {
            completed!(0, Data("claude"));
            throw new InvalidDataException("broken frame");
        };
        _wsl.CommandHandler = (_, _) => Task.FromResult(Data("gemini"));
        var results = await Service().GetAllUsageAsync();
        results.Select(r => r.Provider).Should().Equal("claude", "gemini");
        _wsl.ExecutedBatches.Should().ContainSingle();
        _wsl.ExecutedCommands.Should().ContainSingle().Which.Should().Contain("--provider gemini");
        _cache.SetCallCount.Should().Be(2);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_IncompleteBatchRetainsCallback_RetriesOnlyMissingProvider()
    {
        _settings.SetProviders([new() { Id = "claude" }, new() { Id = "gemini" }]);
        _wsl.BatchHandler = (_, completed, _) =>
        {
            completed!(0, Data("claude"));
            return Task.FromResult<IReadOnlyList<WslResult>>([]);
        };
        _wsl.CommandHandler = (_, _) => Task.FromResult(Data("gemini"));
        var results = await Service().GetAllUsageAsync();
        results.Select(r => r.Provider).Should().Equal("claude", "gemini");
        _wsl.ExecutedCommands.Should().ContainSingle().Which.Should().Contain("--provider gemini");
        _cache.SetCallCount.Should().Be(2);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_CacheThrows_ReportsProviderErrorWithoutRetryingCommand()
    {
        _settings.SetProviders([new() { Id = "claude" }]);
        var cache = new Mock<ICacheService>();
        cache.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<UsageData>())).Throws(new InvalidOperationException("cache failure"));
        _wsl.BatchHandler = (_, completed, _) =>
        {
            completed!(0, Data("claude"));
            return Task.FromResult<IReadOnlyList<WslResult>>([Data("claude")]);
        };
        var service = new CodexBarService(_wsl, cache.Object, _settings, _samples, NullLogger<CodexBarService>.Instance);
        (await service.GetAllUsageAsync()).Single().Error.Should().Be("Unexpected error occurred");
        _wsl.ExecutedBatches.Should().ContainSingle();
        _wsl.ExecutedCommands.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_BatchCannotStart_UsesIndividualCommandsWithTheirDeadlines()
    {
        _settings.SetProviders([new() { Id = "claude" }]);
        _settings.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds = 1;
        _wsl.BatchHandler = (_, _, _) => throw new InvalidOperationException("cannot start");
        _wsl.CommandHandler = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Data("claude");
        };
        (await Service().GetAllUsageAsync()).Single().Error.Should().Be("Request timed out");
        _wsl.ExecutedCommands.Should().ContainSingle();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_CallerCancelsAfterOneCompletion_DoesNotRetryOrCacheLateResults()
    {
        _settings.SetProviders([new() { Id = "claude" }, new() { Id = "gemini" }]);
        using var cts = new CancellationTokenSource();
        _wsl.BatchHandler = (_, completed, _) =>
        {
            completed!(0, Data("claude"));
            cts.Cancel();
            completed(1, Data("gemini"));
            return Task.FromResult<IReadOnlyList<WslResult>>([Data("claude"), Data("gemini")]);
        };
        Func<Task> action = () => Service().GetAllUsageAsync(cts.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        _cache.SetCallCount.Should().Be(1);
        _cache.Get("claude").Should().NotBeNull();
        _cache.Get("gemini").Should().BeNull();
        _wsl.ExecutedCommands.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAllUsageStreamAsync_EnumerationDisposedEarly_CancelsBatch()
    {
        _settings.SetProviders([new() { Id = "claude" }, new() { Id = "gemini" }]);
        var canceled = false;
        _wsl.BatchHandler = async (_, completed, ct) =>
        {
            completed!(0, Data("claude"));
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { canceled = true; throw; }
            return [];
        };
        var stream = Service().GetAllUsageStreamAsync().GetAsyncEnumerator();
        (await stream.MoveNextAsync()).Should().BeTrue();
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Should().BeTrue();
        _wsl.ExecutedCommands.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_RepeatedBatches_UsesFirstThenSubsequentTimeouts()
    {
        _settings.SetProviders([new() { Id = "claude" }]);
        _wsl.BatchHandler = (_, _, _) => Task.FromResult<IReadOnlyList<WslResult>>([Data("claude")]);
        var service = Service();
        await service.GetAllUsageAsync();
        await service.GetAllUsageAsync();
        _wsl.ExecutedBatches[0][0].Timeout.Should().Be(TimeSpan.FromSeconds(_settings.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds));
        _wsl.ExecutedBatches[1][0].Timeout.Should().Be(TimeSpan.FromSeconds(_settings.Settings.Timeouts.CliProviderTimeoutSeconds));
    }

    [TestMethod]
    public async Task GetAllUsageAsync_SampleMode_DoesNotBatchOrCacheSamples()
    {
        _settings.SetProviders([new() { Id = "claude" }]);
        _settings.Settings.DeveloperModeEnabled = true;
        _samples.SetSampleData("claude", Data("claude").Output);
        (await Service().GetAllUsageAsync()).Single().Provider.Should().Be("claude");
        _wsl.ExecutedBatches.Should().BeEmpty();
        _wsl.ExecutedCommands.Should().BeEmpty();
        _cache.SetCallCount.Should().Be(0);
    }
}
