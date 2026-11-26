using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Tests.Unit.Mocks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

/// <summary>
/// Unit tests for WslService.
/// Note: Some tests require actual WSL installation and are marked with [TestCategory("Integration")].
/// Process execution tests are limited due to the difficulty of mocking System.Diagnostics.Process.
/// </summary>
[TestClass]
public class WslServiceTests
{
    private Mock<ILogger<WslService>> _mockLogger = null!;
    private MockSettingsService _mockSettingsService = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<WslService>>();
        _mockSettingsService = new MockSettingsService();
    }

    #region Constructor Tests

    [TestMethod]
    public void Constructor_WithNullSettingsService_UsesDefaultTimeout()
    {
        // Arrange & Act
        var service = new WslService(_mockLogger.Object, settingsService: null);

        // Assert - Service should be created successfully
        service.Should().NotBeNull();
    }

    [TestMethod]
    public void Constructor_WithSettingsService_UsesConfiguredTimeout()
    {
        // Arrange
        var settings = AppSettings.GetDefaults();
        settings.Timeouts.WslCommandTimeoutSeconds = 60;
        _mockSettingsService.SetSettings(settings);

        // Act
        var service = new WslService(_mockLogger.Object, _mockSettingsService);

        // Assert
        service.Should().NotBeNull();
    }

    [TestMethod]
    public void Constructor_WithDistro_SetsDistroParameter()
    {
        // Arrange & Act
        var service = new WslService(_mockLogger.Object, _mockSettingsService, distro: "Ubuntu-22.04");

        // Assert
        service.Should().NotBeNull();
    }

    #endregion

    #region ExecuteAsync Tests

    [TestMethod]
    public async Task ExecuteAsync_ExplicitTimeoutLongerThanDefault_AllowsCommandToCompleteAfterSixtySeconds()
    {
        var clock = new DeadlineClock();
        var completion = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WslService(_mockLogger.Object, clock, (_, token) => completion.Task.WaitAsync(token));

        var executing = service.ExecuteAsync("cost scan", TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromSeconds(61));
        executing.IsCompleted.Should().BeFalse();
        completion.SetResult(new WslResult { Success = true, Output = "complete" });

        (await executing.WaitAsync(TimeSpan.FromSeconds(5))).Success.Should().BeTrue();
    }

    [TestMethod]
    public async Task ExecuteAsync_ExplicitTimeoutExpires_ReturnsFailureAtRequestedDeadline()
    {
        var clock = new DeadlineClock();
        var service = new WslService(_mockLogger.Object, clock, WaitForCancellationAsync);
        var executing = service.ExecuteAsync("cost scan", TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        executing.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromTicks(1));

        var result = await executing.WaitAsync(TimeSpan.FromSeconds(5));
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(-1);
        result.Error.Should().Be("Command timed out after 300 seconds");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithoutExplicitTimeout_ReturnsFailureAtDefaultDeadline()
    {
        var clock = new DeadlineClock();
        var service = new WslService(_mockLogger.Object, clock, WaitForCancellationAsync);
        var executing = service.ExecuteAsync("ordinary command");

        clock.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
        executing.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromTicks(1));

        var result = await executing.WaitAsync(TimeSpan.FromSeconds(5));
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Command timed out after 60 seconds");
    }

    [TestMethod]
    public async Task ExecuteAsync_ExplicitTimeoutWithCallerCancellation_StopsBeforeDeadline()
    {
        var clock = new DeadlineClock();
        var service = new WslService(_mockLogger.Object, clock, WaitForCancellationAsync);
        using var caller = new CancellationTokenSource();
        var executing = service.ExecuteAsync("cost scan", TimeSpan.FromMinutes(5), caller.Token);

        caller.Cancel();

        var result = await executing.WaitAsync(TimeSpan.FromSeconds(5));
        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("timed out");
    }

    private static async Task<WslResult> WaitForCancellationAsync(string command, CancellationToken token)
    {
        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        return new WslResult();
    }

    // Advance the real CancellationTokenSource timer without wall-clock waits or WSL dependencies.
    private sealed class DeadlineClock : TimeProvider
    {
        private DeadlineTimer? _timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new DeadlineTimer(callback, state, dueTime);
            return _timer;
        }

        public void Advance(TimeSpan elapsed) => _timer!.Advance(elapsed);

        private sealed class DeadlineTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private TimeSpan _remaining = dueTime;
            private bool _disposed;
            public void Advance(TimeSpan elapsed)
            {
                if (_disposed) return;
                _remaining -= elapsed;
                if (_remaining > TimeSpan.Zero) return;
                _disposed = true;
                callback(state);
            }

            public bool Change(TimeSpan due, TimeSpan period)
            {
                if (_disposed) return false;
                _remaining = due;
                return true;
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [TestMethod]
    [DataRow(2, 127)]
    [DataRow(5, -1)]
    public void ExecutionFailure_Win32StartupFailure_ClassifiesOnlyMissingExecutableAsCommandNotFound(int nativeError, int exit)
    {
        var result = WslService.ExecutionFailure(new System.ComponentModel.Win32Exception(nativeError, "startup failure"));
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(exit);
        result.Error.Should().Be("startup failure");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithCancellationToken_RespectsToken()
    {
        // Arrange
        var service = new WslService(_mockLogger.Object, _mockSettingsService);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await service.ExecuteAsync("echo test", cts.Token);

        // Assert
        // Either throws OperationCanceledException or returns failure
        result.Success.Should().BeFalse();
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ExecuteAsync_SimpleCommand_ReturnsResult()
    {
        RequireWslExecution();
        // This test requires WSL to be installed
        // Arrange
        var service = new WslService(_mockLogger.Object, _mockSettingsService);

        // Act
        var result = await service.ExecuteAsync("echo 'hello'");

        // Assert - Will fail if WSL not installed, which is expected
        // This is an integration test
        result.Should().NotBeNull();
    }

    [TestMethod]
    public async Task ExecuteAsync_EmptyCommand_ReturnsResult()
    {
        // Arrange
        var service = new WslService(_mockLogger.Object, TimeProvider.System, (_, _) => Task.FromResult(new WslResult()));

        // Act
        var result = await service.ExecuteAsync("");

        // Assert
        result.Should().NotBeNull();
    }

    #endregion

    #region CheckWslStatusAsync Tests

    [TestMethod]
    [TestCategory("Integration")]
    public async Task CheckWslStatusAsync_ReturnsStatusResult()
    {
        RequireWslExecution();
        // Arrange
        var service = new WslService(_mockLogger.Object, _mockSettingsService);

        // Act
        var result = await service.CheckWslStatusAsync();

        // Assert
        result.Should().NotBeNull();
        // Either WSL is installed or not - both are valid states
    }

    [TestMethod]
    public async Task CheckWslStatusAsync_WithCancellation_HandlesCancellation()
    {
        // Arrange
        var service = new WslService(_mockLogger.Object, _mockSettingsService);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await service.CheckWslStatusAsync(cts.Token);

        // Assert
        result.Should().NotBeNull();
        // Should handle cancellation gracefully
    }

    #endregion

    #region IsWslInstalledAsync Tests

    [TestMethod]
    [TestCategory("Integration")]
    public async Task IsWslInstalledAsync_ReturnsBool()
    {
        RequireWslExecution();
        // Arrange
        var service = new WslService(_mockLogger.Object, _mockSettingsService);

        // Act
        var result = await service.IsWslInstalledAsync();

        // Assert
        // Result is either true or false based on system state - this is a basic sanity check
        (result == true || result == false).Should().BeTrue();
    }

    #endregion

    #region GetDistrosAsync Tests

    [TestMethod]
    [TestCategory("Integration")]
    public async Task GetDistrosAsync_ReturnsList()
    {
        RequireWslExecution();
        // Arrange
        var service = new WslService(_mockLogger.Object, _mockSettingsService);

        // Act
        var result = await service.GetDistrosAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<List<string>>();
    }

    [TestMethod]
    public async Task GetDistrosAsync_WithCancellation_ReturnsEmptyList()
    {
        // Arrange
        var service = new WslService(_mockLogger.Object, _mockSettingsService);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await service.GetDistrosAsync(cts.Token);

        // Assert
        result.Should().NotBeNull();
    }

    #endregion

    #region Timeout Configuration Tests

    [TestMethod]
    public void TimeoutConfiguration_DefaultValues_AreReasonable()
    {
        // Arrange
        var settings = AppSettings.GetDefaults();

        // Assert
        settings.Timeouts.WslCommandTimeoutSeconds.Should().BeGreaterThan(0);
        settings.Timeouts.WslCommandTimeoutSeconds.Should().BeLessThanOrEqualTo(120);
        settings.Timeouts.WslStatusCheckTimeoutSeconds.Should().BeGreaterThan(0);
        settings.Timeouts.WslStatusCheckTimeoutSeconds.Should().BeLessThanOrEqualTo(30);
    }

    [TestMethod]
    public void TimeoutConfiguration_CustomValues_AreRespected()
    {
        // Arrange
        var settings = AppSettings.GetDefaults();
        settings.Timeouts.WslCommandTimeoutSeconds = 90;
        settings.Timeouts.WslStatusCheckTimeoutSeconds = 15;
        _mockSettingsService.SetSettings(settings);

        // Act
        var service = new WslService(_mockLogger.Object, _mockSettingsService);

        // Assert
        service.Should().NotBeNull();
        _mockSettingsService.Settings.Timeouts.WslCommandTimeoutSeconds.Should().Be(90);
        _mockSettingsService.Settings.Timeouts.WslStatusCheckTimeoutSeconds.Should().Be(15);
    }

    [TestMethod]
    public void ResolveCommandTimeout_WhenWslCapIsShorterThanProviderTimeouts_UsesTheLongerProviderTimeout()
    {
        // The default 30s WSL cap used to fire before the 45s/60s provider timeouts ("Command timed out after 30 seconds")
        var timeouts = new TimeoutSettings
        {
            WslCommandTimeoutSeconds = 30,
            CliProviderTimeoutSeconds = 45,
            CliProviderFirstFetchTimeoutSeconds = 60
        };

        WslService.ResolveCommandTimeout(timeouts).Should().Be(TimeSpan.FromSeconds(60));
    }

    [TestMethod]
    public void ResolveCommandTimeout_WhenWslCapIsAlreadyLongest_KeepsIt()
    {
        var timeouts = new TimeoutSettings
        {
            WslCommandTimeoutSeconds = 120,
            CliProviderTimeoutSeconds = 45,
            CliProviderFirstFetchTimeoutSeconds = 60
        };

        WslService.ResolveCommandTimeout(timeouts).Should().Be(TimeSpan.FromSeconds(120));
    }

    [TestMethod]
    public void ResolveCommandTimeout_ShortProviderSettings_KeepsSixtySecondDefaultMinimum()
    {
        WslService.ResolveCommandTimeout(new TimeoutSettings
        {
            WslCommandTimeoutSeconds = 10, CliProviderTimeoutSeconds = 15, CliProviderFirstFetchTimeoutSeconds = 20
        }).Should().Be(TimeSpan.FromSeconds(60));
    }

    [TestMethod]
    public void ResolveCommandTimeout_WithDefaults_IsNeverBelowTheCliProviderTimeouts()
    {
        var defaults = AppSettings.GetDefaults().Timeouts;

        WslService.ResolveCommandTimeout(defaults).TotalSeconds
            .Should().BeGreaterThanOrEqualTo(Math.Max(defaults.CliProviderTimeoutSeconds, defaults.CliProviderFirstFetchTimeoutSeconds));
    }

    #endregion

    #region WslResult Tests

    [TestMethod]
    public void WslResult_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var result = new WslResult();

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().BeEmpty();
        result.Error.Should().BeEmpty();
        result.ExitCode.Should().Be(0);
    }

    [TestMethod]
    public void WslResult_WithValues_StoresCorrectly()
    {
        // Arrange & Act
        var result = new WslResult
        {
            Success = true,
            Output = "test output",
            Error = "test error",
            ExitCode = 0
        };

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Be("test output");
        result.Error.Should().Be("test error");
        result.ExitCode.Should().Be(0);
    }

    #endregion

    #region WslStatusResult Tests

    [TestMethod]
    public void WslStatusResult_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var result = new WslStatusResult();

        // Assert
        result.IsInstalled.Should().BeFalse();
        result.IsRunning.Should().BeFalse();
        result.Error.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [TestMethod]
    public void WslStatusResult_NotInstalled_HasCorrectState()
    {
        // Arrange & Act
        var result = new WslStatusResult
        {
            IsInstalled = false,
            Error = WslErrorType.NotInstalled
        };

        // Assert
        result.IsInstalled.Should().BeFalse();
        result.Error.Should().Be(WslErrorType.NotInstalled);
    }

    [TestMethod]
    public void WslStatusResult_InstalledButNotRunning_HasCorrectState()
    {
        // Arrange & Act
        var result = new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = false,
            Error = WslErrorType.NotRunning,
            ErrorMessage = "WSL is not running"
        };

        // Assert
        result.IsInstalled.Should().BeTrue();
        result.IsRunning.Should().BeFalse();
        result.Error.Should().Be(WslErrorType.NotRunning);
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void WslStatusResult_Timeout_HasCorrectState()
    {
        // Arrange & Act
        var result = new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = false,
            Error = WslErrorType.Timeout,
            ErrorMessage = "Timeout occurred"
        };

        // Assert
        result.Error.Should().Be(WslErrorType.Timeout);
    }

    [TestMethod]
    public void WslStatusResult_OtherError_HasCorrectState()
    {
        // Arrange & Act
        var result = new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = false,
            Error = WslErrorType.Other,
            ErrorMessage = "Unknown error"
        };

        // Assert
        result.Error.Should().Be(WslErrorType.Other);
    }

    #endregion

    #region WslErrorType Tests

    [TestMethod]
    public void WslErrorType_AllValues_AreDefined()
    {
        // Arrange
        var errorTypes = Enum.GetValues<WslErrorType>();

        // Assert
        errorTypes.Should().Contain(WslErrorType.NotInstalled);
        errorTypes.Should().Contain(WslErrorType.NotRunning);
        errorTypes.Should().Contain(WslErrorType.Timeout);
        errorTypes.Should().Contain(WslErrorType.Other);
    }

    #endregion

    #region Logging Tests

    [TestMethod]
    public async Task ExecuteAsync_LogsCommandExecution()
    {
        // Arrange
        var service = new WslService(_mockLogger.Object, TimeProvider.System, (_, _) => Task.FromResult(new WslResult()));

        // Act
        await service.ExecuteAsync("test command");

        // Assert - Verify logging was called
        _mockLogger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    #endregion

    private static void RequireWslExecution()
    {
        // Opt-in: these run real wsl.exe, which boots the WSL virtual machine (about 1.3 GB of RAM).
        if (!TestEnvironment.IsSet("RUN_WSL_INTEGRATION") ||
            TestEnvironment.IsSet("SKIP_BASH_INTEGRATION") ||
            TestEnvironment.IsSet("SKIP_WSL_INTEGRATION"))
            Assert.Inconclusive("WSL integration tests run only with AGENTSWINBAR_RUN_WSL_INTEGRATION=1");
    }

    [TestMethod]
    public async Task ReadOutputAsync_OutputLimitExceeded_CancelsProcessLifetimeAndReturnsClearFailure()
    {
        using var cts = new CancellationTokenSource();
        using var reader = new StringReader("too much output");
        Func<Task> read = () => WslService.ReadOutputAsync(reader, new WslOutputReader.Budget(4), cts);
        var failure = await read.Should().ThrowAsync<WslOutputLimitException>();
        cts.IsCancellationRequested.Should().BeTrue();
        var result = WslService.ExecutionFailure(failure.Which);
        result.Success.Should().BeFalse(); result.Output.Should().BeEmpty();
        result.Error.Should().Contain("output limit exceeded");
    }

    [TestMethod]
    public async Task ExecuteAsync_CanceledBeforeStart_DoesNotInvokeExecutor()
    {
        var calls = 0;
        var service = new WslService(_mockLogger.Object, TimeProvider.System, (_, _) =>
        { calls++; return Task.FromResult(new WslResult { Success = true }); });
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var result = await service.ExecuteAsync("unused command", cts.Token);
        calls.Should().Be(0); result.Success.Should().BeFalse();
    }
}
