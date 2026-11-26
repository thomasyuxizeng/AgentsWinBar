using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Services.Native;
using AgentsWinBar.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

/// <summary>Setup, onboarding and dashboard diagnostics do not require WSL when every enabled provider is native.</summary>
[TestClass]
public class SetupCheckerNativeUsageTests
{
    private MockSettingsService _settings = null!;

    [TestInitialize]
    public void Setup()
    {
        _settings = new();
        _settings.Settings.Normalize();
    }

    private SetupChecker Create(IWslService wsl, ISetupMemoService? memo = null) =>
        new(wsl, NullLogger<SetupChecker>.Instance, memo, _settings);

    [TestMethod]
    public async Task CheckAsync_AllUsageNative_IsReadyWithoutTouchingWsl()
    {
        var wsl = new Mock<IWslService>(MockBehavior.Strict);

        var status = await Create(wsl.Object).CheckAsync();

        status.IsReady.Should().BeTrue();
        status.UsageIsNative.Should().BeTrue();
        status.CurrentStep.Should().Be(SetupStep.Ready);
        wsl.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task CheckAsync_AllUsageNative_LeavesSetupMemoAlone()
    {
        var memo = new Mock<ISetupMemoService>(MockBehavior.Strict);

        var status = await Create(new Mock<IWslService>(MockBehavior.Strict).Object, memo.Object).CheckAsync();

        status.IsReady.Should().BeTrue();
        memo.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task CheckAsync_OnlyDisabledProviderSetToWsl_StillSkipsWsl()
    {
        _settings.Settings.UsageSources["codex"] = UsageSource.Wsl;
        _settings.Settings.Providers.Single(p => p.Id == "codex").IsEnabled = false;
        var wsl = new Mock<IWslService>(MockBehavior.Strict);

        (await Create(wsl.Object).CheckAsync()).IsReady.Should().BeTrue();
        wsl.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task CheckAsync_EnabledProviderSetToWslAndWslMissing_ReportsWslSetupStep()
    {
        _settings.Settings.UsageSources["claude"] = UsageSource.Wsl;
        var wsl = new MockWslService();
        wsl.SetStatusResult(new WslStatusResult { IsInstalled = false, Error = WslErrorType.NotInstalled });

        var status = await Create(wsl).CheckAsync();

        status.IsReady.Should().BeFalse();
        status.UsageIsNative.Should().BeFalse();
        status.CurrentStep.Should().Be(SetupStep.InstallWsl);
    }

    [TestMethod]
    public async Task CheckAsync_EnabledProviderSetToWslAndCliMissing_ReportsInstallCodexBar()
    {
        _settings.Settings.UsageSources["gemini"] = UsageSource.Wsl;
        var wsl = new MockWslService();
        wsl.SetCommandResult("codexbar --version", new WslResult { ExitCode = 127, Error = "codexbar: command not found" });

        var status = await Create(wsl).CheckAsync();

        status.CurrentStep.Should().Be(SetupStep.InstallCodexBar);
        wsl.ExecutedCommands.Should().Equal("codexbar --version");
    }

    [TestMethod]
    public async Task CheckAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Create(new Mock<IWslService>(MockBehavior.Strict).Object).CheckAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public void CurrentStep_UsageIsNativeWithoutWsl_ReturnsReady()
    {
        new SetupStatus { UsageIsNative = true, IsReady = true, WslInstalled = false }.CurrentStep.Should().Be(SetupStep.Ready);
    }

    [TestMethod]
    public async Task RefreshDiagnosticsAsync_AllUsageNative_ReportsNativeRouteWithoutWsl()
    {
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        var native = new Mock<INativeUsageService>(MockBehavior.Strict);
        var cli = new CodexBarService(wsl.Object, new MockCacheService(), _settings, new MockSampleDataLoader(),
            NullLogger<CodexBarService>.Instance, nativeUsage: native.Object);
        var directory = Path.Combine(Path.GetTempPath(), "agentswinbar-dashboard-native-" + Guid.NewGuid().ToString("N"));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.Settings).Returns(_settings.Settings);
        settings.SetupGet(s => s.SettingsFilePath).Returns(Path.Combine(directory, "settings.json"));
        var history = new UsageHistoryStore();
        var persistence = new UsageHistoryPersistence(history, settings.Object, NullLogger<UsageHistoryPersistence>.Instance);
        var dashboard = new DashboardService(settings.Object, history, persistence, new ActivityLog(), cli,
            NullLogger<DashboardService>.Instance);

        try
        {
            await dashboard.RefreshDiagnosticsAsync();
            var snapshot = dashboard.GetSnapshot([], null);

            snapshot.Diagnostics.CliVersion.Should().Be(CodexBarService.NativeVersion);
            snapshot.Diagnostics.CliAvailable.Should().BeTrue();
            wsl.VerifyNoOtherCalls();
            native.VerifyNoOtherCalls();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
