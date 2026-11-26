using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Tests.Unit.Mocks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class SetupCheckerTests
{
    private MockWslService _mockWslService = null!;
    private Mock<ILogger<SetupChecker>> _mockLogger = null!;
    private SetupChecker _setupChecker = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockWslService = new MockWslService();
        _mockLogger = new Mock<ILogger<SetupChecker>>();

        _setupChecker = new SetupChecker(
            _mockWslService,
            _mockLogger.Object);
    }

    [TestMethod]
    public async Task CheckAsync_WslNotInstalled_ReturnsNotInstalled()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = false,
            Error = WslErrorType.NotInstalled
        });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.WslInstalled.Should().BeFalse();
        result.WslError.Should().Be(WslErrorType.NotInstalled);
        result.IsReady.Should().BeFalse();
    }

    [TestMethod]
    public async Task CheckAsync_WslNotRunning_ReturnsNotRunning()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = false,
            Error = WslErrorType.NotRunning
        });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.WslInstalled.Should().BeTrue();
        result.WslRunning.Should().BeFalse();
        result.WslError.Should().Be(WslErrorType.NotRunning);
        result.IsReady.Should().BeFalse();
    }

    [TestMethod]
    public async Task CheckAsync_NoDistros_ReturnsEmptyDistros()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros([]);

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.WslInstalled.Should().BeTrue();
        result.WslRunning.Should().BeTrue();
        result.Distros.Should().BeEmpty();
        result.IsReady.Should().BeFalse();
    }

    [TestMethod]
    public async Task CheckAsync_CodexBarNotInstalled_ReturnsNotInstalled()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { ExitCode = 127 });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.WslInstalled.Should().BeTrue();
        result.WslRunning.Should().BeTrue();
        result.Distros.Should().Contain("Ubuntu");
        result.CodexBarInstalled.Should().BeFalse();
        result.IsReady.Should().BeFalse();
    }

    [TestMethod]
    public async Task CheckAsync_CodexBarOldVersion_ReturnsNotCompatible()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { Success = true, Output = "0.16.0" }); // Older than minimum 0.17.0

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.WslInstalled.Should().BeTrue();
        result.WslRunning.Should().BeTrue();
        result.CodexBarInstalled.Should().BeTrue();
        result.CodexBarVersion.Should().Be("0.16.0");
        result.IsReady.Should().BeFalse();
        _mockWslService.ExecutedCommands.Should().Equal("codexbar --version");
    }

    [TestMethod]
    public async Task CheckAsync_AllReady_ReturnsIsReady()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { Success = true, Output = "0.17.0" });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.WslInstalled.Should().BeTrue();
        result.WslRunning.Should().BeTrue();
        result.Distros.Should().NotBeEmpty();
        result.CodexBarInstalled.Should().BeTrue();
        result.CodexBarVersion.Should().Be("0.17.0");
        result.IsReady.Should().BeTrue();
        _mockWslService.ExecutedCommands.Should().Equal("codexbar --version");
    }

    [TestMethod]
    public async Task CheckAsync_CodexBarNewerVersion_ReturnsIsReady()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { Success = true, Output = "1.0.0" });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.IsReady.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("custom development build")]
    public async Task CheckAsync_UnknownOrUnparseableVersion_TreatedAsCompatible(string version)
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { Success = true, Output = version });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.IsReady.Should().BeTrue();
    }

    [TestMethod]
    public async Task CheckAsync_MultipleDistros_ReturnsAll()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu", "Debian", "Alpine"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { Success = true, Output = "0.17.0" });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.Distros.Should().HaveCount(3);
        result.Distros.Should().Contain(["Ubuntu", "Debian", "Alpine"]);
    }

    [TestMethod]
    public async Task CheckAsync_WslTimeout_ReturnsTimeoutError()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = false,
            Error = WslErrorType.Timeout,
            ErrorMessage = "WSL status check timed out."
        });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.WslInstalled.Should().BeTrue();
        result.WslRunning.Should().BeFalse();
        result.WslError.Should().Be(WslErrorType.Timeout);
        result.IsReady.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("0.17.0", true)]
    [DataRow("0.17.1", true)]
    [DataRow("0.18.0", true)]
    [DataRow("1.0.0", true)]
    [DataRow("0.16.9", false)]
    [DataRow("0.16.0", false)]
    [DataRow("0.1.0", false)]
    public async Task CheckAsync_VersionComparison_ReturnsExpected(string version, bool expectedReady)
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { Success = true, Output = version ?? string.Empty });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.IsReady.Should().Be(expectedReady);
    }

    [TestMethod]
    public async Task CheckAsync_VersionWithPrefix_ParsesCorrectly()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { Success = true, Output = "codexbar 0.17.0" });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.IsReady.Should().BeTrue();
    }

    [TestMethod]
    public async Task CheckAsync_NullVersion_ReturnsNotCompatible()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { Success = false, ExitCode = 1 });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.CodexBarInstalled.Should().BeTrue();
        result.IsReady.Should().BeFalse();
    }

    [TestMethod]
    public async Task CheckAsync_EmptyVersion_ReturnsNotCompatible()
    {
        // Arrange
        _mockWslService.SetStatusResult(new WslStatusResult
        {
            IsInstalled = true,
            IsRunning = true
        });
        _mockWslService.SetDistros(["Ubuntu"]);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult { Success = true, Output = "" });

        // Act
        var result = await _setupChecker.CheckAsync();

        // Assert
        result.CodexBarInstalled.Should().BeTrue();
        result.IsReady.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(127, "", "")]
    [DataRow(1, "bash: codexbar: command not found", "")]
    [DataRow(1, "", "CODEXBAR: COMMAND NOT FOUND")]
    public async Task CheckAsync_CommandNotFound_ReturnsNotInstalledUsingOneShell(
        int exitCode, string output, string error)
    {
        _mockWslService.SetCommandResult("codexbar --version", new WslResult
        {
            ExitCode = exitCode, Output = output, Error = error
        });

        var result = await _setupChecker.CheckAsync();

        result.CodexBarInstalled.Should().BeFalse();
        result.IsReady.Should().BeFalse();
        _mockWslService.ExecutedCommands.Should().Equal("codexbar --version");
    }

    [TestMethod]
    public async Task CheckAsync_SuccessfulVersion_PersistsMemo()
    {
        var memo = new Mock<ISetupMemoService>();
        var checker = new SetupChecker(_mockWslService, _mockLogger.Object, memo.Object);
        _mockWslService.SetCommandResult("codexbar --version", new WslResult
        {
            Success = true, Output = "codexbar 0.17.0\n"
        });

        (await checker.CheckAsync()).IsReady.Should().BeTrue();

        memo.Verify(m => m.SaveAsync("codexbar 0.17.0", It.IsAny<CancellationToken>()), Times.Once);
        memo.Verify(m => m.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CheckAsync_NotReady_InvalidatesMemo(bool commandMissing)
    {
        var memo = new Mock<ISetupMemoService>();
        var checker = new SetupChecker(_mockWslService, _mockLogger.Object, memo.Object);
        if (commandMissing)
            _mockWslService.SetCommandResult("codexbar --version", new WslResult { ExitCode = 127 });
        else
            _mockWslService.SetStatusResult(new WslStatusResult { IsInstalled = false });

        (await checker.CheckAsync()).IsReady.Should().BeFalse();

        memo.Verify(m => m.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        memo.Verify(m => m.SaveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public void MinCodexBarVersion_IsExpectedValue()
    {
        // Assert
        SetupChecker.MinCodexBarVersion.Should().Be(new Version("0.17.0"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CheckAsync_WslProbeThrows_ReturnsErrorAndInvalidatesMemo(bool statusProbe)
    {
        var wsl = new Mock<IWslService>();
        wsl.Setup(w => w.CheckWslStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WslStatusResult { IsInstalled = true, IsRunning = true });
        if (statusProbe)
            wsl.Setup(w => w.CheckWslStatusAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("WSL removed"));
        else
            wsl.Setup(w => w.GetDistrosAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("WSL removed"));
        var memo = new Mock<ISetupMemoService>();
        var checker = new SetupChecker(wsl.Object, _mockLogger.Object, memo.Object);

        var status = await checker.CheckAsync();

        status.IsReady.Should().BeFalse();
        status.WslError.Should().Be(WslErrorType.Other);
        memo.Verify(m => m.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CheckAsync_CancelledProbe_PropagatesCancellationWithoutUpdatingMemo()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var wsl = new Mock<IWslService>();
        wsl.Setup(w => w.CheckWslStatusAsync(cts.Token)).ThrowsAsync(new OperationCanceledException(cts.Token));
        var memo = new Mock<ISetupMemoService>();
        var checker = new SetupChecker(wsl.Object, _mockLogger.Object, memo.Object);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => checker.CheckAsync(cts.Token));

        memo.Verify(m => m.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Never);
        memo.Verify(m => m.SaveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
