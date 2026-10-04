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
        // ParseUsageJson expects a single object, not an array
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
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private CodexBarService CostService(CostClock? clock = null, string profile = "C:\\Users\\Test", bool folderExists = true) =>
        new(_mockWslService, _mockCacheService, _mockSettingsService, _mockSampleDataLoader, _mockLogger.Object,
            clock, () => profile, _ => folderExists);

    private void SetCostResponse(string provider = "claude", bool success = true, string? json = null)
    {
        var command = CodexBarWin.Helpers.CostCommandBuilder.Build(provider, "C:\\Users\\Test", _ => true);
        var cliProvider = provider == "gemini" ? "antigravity" : provider;
        _mockWslService.SetCommandResult(command, new WslResult { Success = success, ExitCode = success ? 0 : 3,
            Output = json ?? $$$"""[{"provider":"{{{cliProvider}}}","sessionCostUSD":1.5,"last30DaysCostUSD":5,"last30DaysTokens":123456,"daily":[]}]""" });
    }

    [TestMethod]
    public async Task GetCostAsync_WhenRepeated_UsesCacheUntilFifteenMinutesAndManualRefreshBypassesIt()
    {
        var clock = new CostClock();
        var service = CostService(clock);
        SetCostResponse();
        var first = await service.GetCostAsync("claude");
        first!.TodayCostUSD.Should().Be(1.5m);
        (await service.GetCostAsync("claude")).Should().BeSameAs(first);
        clock.Now += TimeSpan.FromMinutes(14);
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
        clock.Now += TimeSpan.FromMinutes(15); SetCostResponse();
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
        _mockCacheService.SetCallCount.Should().Be(2); // Should cache sample data
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
}
