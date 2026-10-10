using AwesomeAssertions;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Models;

[TestClass]
public class SetupStatusTests
{
    [TestMethod]
    public void DefaultDistro_WhenDistrosEmpty_ReturnsNull()
    {
        // Arrange
        var status = new SetupStatus { Distros = [] };

        // Act & Assert
        status.DefaultDistro.Should().BeNull();
    }

    [TestMethod]
    public void DefaultDistro_WhenDistrosHasItems_ReturnsFirstDistro()
    {
        // Arrange
        var status = new SetupStatus { Distros = ["Ubuntu-24.04", "Debian"] };

        // Act & Assert
        status.DefaultDistro.Should().Be("Ubuntu-24.04");
    }

    [TestMethod]
    public void CurrentStep_WhenWslErrorNotInstalled_ReturnsInstallWsl()
    {
        // Arrange
        var status = new SetupStatus
        {
            WslInstalled = true,
            WslError = WslErrorType.NotInstalled
        };

        // Act & Assert
        status.CurrentStep.Should().Be(SetupStep.InstallWsl);
    }

    [TestMethod]
    public void CurrentStep_WhenWslErrorNotRunning_ReturnsStartWsl()
    {
        // Arrange
        var status = new SetupStatus
        {
            WslInstalled = true,
            WslRunning = false,
            WslError = WslErrorType.NotRunning
        };

        // Act & Assert
        status.CurrentStep.Should().Be(SetupStep.StartWsl);
    }

    [TestMethod]
    [DataRow(WslErrorType.Timeout)]
    [DataRow(WslErrorType.Other)]
    public void CurrentStep_WhenWslErrorIsOtherError_ReturnsFixWsl(WslErrorType errorType)
    {
        // Arrange
        var status = new SetupStatus
        {
            WslInstalled = true,
            WslError = errorType
        };

        // Act & Assert
        status.CurrentStep.Should().Be(SetupStep.FixWsl);
    }

    [TestMethod]
    public void CurrentStep_WhenWslNotInstalledAndNoError_ReturnsInstallWsl()
    {
        // Arrange
        var status = new SetupStatus
        {
            WslInstalled = false,
            WslError = null
        };

        // Act & Assert
        status.CurrentStep.Should().Be(SetupStep.InstallWsl);
    }

    [TestMethod]
    public void CurrentStep_WhenWslInstalledNoErrorAndDistrosEmpty_ReturnsInstallDistro()
    {
        // Arrange
        var status = new SetupStatus
        {
            WslInstalled = true,
            WslError = null,
            Distros = []
        };

        // Act & Assert
        status.CurrentStep.Should().Be(SetupStep.InstallDistro);
    }

    [TestMethod]
    public void CurrentStep_WhenDistrosPresentAndCodexBarNotInstalled_ReturnsInstallCodexBar()
    {
        // Arrange
        var status = new SetupStatus
        {
            WslInstalled = true,
            WslError = null,
            Distros = ["Ubuntu"],
            CodexBarInstalled = false
        };

        // Act & Assert
        status.CurrentStep.Should().Be(SetupStep.InstallCodexBar);
    }

    [TestMethod]
    public void CurrentStep_WhenEverythingInstalled_ReturnsReady()
    {
        // Arrange
        var status = new SetupStatus
        {
            WslInstalled = true,
            WslRunning = true,
            WslError = null,
            Distros = ["Ubuntu"],
            CodexBarInstalled = true,
            CodexBarVersion = "1.0.0",
            IsReady = true
        };

        // Act & Assert
        status.CurrentStep.Should().Be(SetupStep.Ready);
    }

    [TestMethod]
    public void SetupStatus_DefaultValues_AreExpected()
    {
        // Arrange & Act
        var status = new SetupStatus();

        // Assert
        status.WslInstalled.Should().BeFalse();
        status.WslRunning.Should().BeFalse();
        status.WslError.Should().BeNull();
        status.Distros.Should().BeEmpty();
        status.DefaultDistro.Should().BeNull();
        status.CodexBarInstalled.Should().BeFalse();
        status.CodexBarVersion.Should().BeNull();
        status.CodexBarError.Should().BeNull();
        status.IsReady.Should().BeFalse();
        status.CurrentStep.Should().Be(SetupStep.InstallWsl);
    }

    [TestMethod]
    public void SetupStatus_RecordEqualityAndWithExpression_BehavesAsRecord()
    {
        // Arrange
        var status1 = new SetupStatus
        {
            WslInstalled = true,
            WslRunning = true,
            Distros = ["Ubuntu"],
            CodexBarInstalled = true
        };

        var status2 = status1 with { CodexBarVersion = "2.0" };

        // Assert
        status2.CodexBarVersion.Should().Be("2.0");
        status2.WslInstalled.Should().BeTrue();
        status2.Distros.Should().Equal("Ubuntu");
        status2.Should().NotBe(status1);
    }
}
