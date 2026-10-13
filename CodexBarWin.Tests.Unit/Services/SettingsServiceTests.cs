using System.Text.Json;
using CodexBarWin.Models;
using CodexBarWin.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

/// <summary>
/// Unit tests for SettingsService.
/// Uses a testable subclass to override the settings file path for isolated testing.
/// </summary>
[TestClass]
public class SettingsServiceTests
{
    [TestMethod]
    [DataRow(ResetAnimationMode.WhenNotBatterySaver)]
    [DataRow(ResetAnimationMode.Always)]
    [DataRow(ResetAnimationMode.Never)]
    public async Task SaveAndLoadAsync_ResetAnimationMode_PersistsInSettingsJson(ResetAnimationMode mode)
    {
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        service.Settings.ResetAnimationMode = mode;
        await service.SaveAsync();
        (await File.ReadAllTextAsync(_testSettingsPath)).Should().Contain("resetAnimationMode");
        var loaded = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        await loaded.LoadAsync();
        loaded.Settings.ResetAnimationMode.Should().Be(mode);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"resetAnimationMode\":99}")]
    public async Task LoadAsync_OldOrInvalidResetAnimationMode_UsesDefault(string json)
    {
        await File.WriteAllTextAsync(_testSettingsPath, json);
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        await service.LoadAsync();
        service.Settings.ResetAnimationMode.Should().Be(ResetAnimationMode.WhenNotBatterySaver);
    }

    private Mock<ILogger<SettingsService>> _mockLogger = null!;
    private string _testDirectory = null!;
    private string _testSettingsPath = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<SettingsService>>();
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CodexBarWin_Tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _testSettingsPath = Path.Combine(_testDirectory, "settings.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    #region Constructor Tests

    [TestMethod]
    public void Constructor_CreatesInstance_WithDefaultSettings()
    {
        // Arrange & Act
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Assert
        service.Should().NotBeNull();
        service.Settings.Should().NotBeNull();
    }

    [TestMethod]
    public void Constructor_CreatesAppFolder_IfNotExists()
    {
        // Arrange
        var newTestDir = Path.Combine(Path.GetTempPath(), $"CodexBarWin_Tests_{Guid.NewGuid()}");

        // Act
        var service = new TestableSettingsService(_mockLogger.Object, newTestDir);

        // Assert
        Directory.Exists(newTestDir).Should().BeTrue();

        // Cleanup
        Directory.Delete(newTestDir, recursive: true);
    }

    [TestMethod]
    public void SettingsFilePath_ReturnsCorrectPath()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        var path = service.SettingsFilePath;

        // Assert
        path.Should().EndWith("settings.json");
        path.Should().Contain(_testDirectory);
    }

    #endregion

    #region LoadAsync Tests

    [TestMethod]
    public async Task LoadAsync_FileNotExists_UsesDefaultSettings()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        await service.LoadAsync();

        // Assert
        service.Settings.Should().NotBeNull();
        service.Settings.Providers.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task LoadAsync_ValidSettingsFile_LoadsSettings()
    {
        // Arrange
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 120,
            Providers =
            [
                new ProviderConfig { Id = "claude", IsEnabled = true, Order = 0 },
                new ProviderConfig { Id = "codex", IsEnabled = false, Order = 1 }
            ]
        };
        await WriteSettingsFileAsync(settings);
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        await service.LoadAsync();

        // Assert
        service.Settings.RefreshIntervalSeconds.Should().Be(120);
        service.Settings.Providers.Should().HaveCount(2);
        service.Settings.Providers.First(p => p.Id == "claude").IsEnabled.Should().BeTrue();
        service.Settings.Providers.First(p => p.Id == "codex").IsEnabled.Should().BeFalse();
    }

    [TestMethod]
    public async Task LoadAsync_InvalidProviders_FiltersThemOut()
    {
        // Arrange
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderConfig { Id = "claude", IsEnabled = true, Order = 0 },
                new ProviderConfig { Id = "invalid_provider", IsEnabled = true, Order = 1 },
                new ProviderConfig { Id = "codex", IsEnabled = true, Order = 2 }
            ]
        };
        await WriteSettingsFileAsync(settings);
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        await service.LoadAsync();

        // Assert
        service.Settings.Providers.Should().HaveCount(2);
        service.Settings.Providers.Should().NotContain(p => p.Id == "invalid_provider");
        service.Settings.Providers.Should().Contain(p => p.Id == "claude");
        service.Settings.Providers.Should().Contain(p => p.Id == "codex");
    }

    [TestMethod]
    public async Task LoadAsync_InvalidJson_UsesDefaultSettings()
    {
        // Arrange
        await File.WriteAllTextAsync(_testSettingsPath, "{ invalid json }");
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        await service.LoadAsync();

        // Assert
        service.Settings.Should().NotBeNull();
        // Should use defaults when JSON is invalid
    }

    [TestMethod]
    public async Task LoadAsync_EmptyFile_UsesDefaultSettings()
    {
        // Arrange
        await File.WriteAllTextAsync(_testSettingsPath, "");
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        await service.LoadAsync();

        // Assert
        service.Settings.Should().NotBeNull();
    }

    [TestMethod]
    public async Task LoadAsync_NullProvidersInFile_HandlesGracefully()
    {
        // Arrange
        await File.WriteAllTextAsync(_testSettingsPath, """{"RefreshIntervalSeconds":5,"Providers":null}""");
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        await service.LoadAsync();

        // Assert
        service.Settings.Should().NotBeNull();
    }

    #endregion

    #region SaveAsync Tests

    [TestMethod]
    public async Task SaveAsync_OverlappingQuietAndNormalSaves_LeavesCompleteSettingsAndNoTempFile()
    {
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        service.Settings.RefreshIntervalSeconds = 42;
        var events = 0;
        service.SettingsChanged += (_, _) => Interlocked.Increment(ref events);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? service.SaveAsync() : service.SaveQuietAsync()));
        var loaded = JsonSerializer.Deserialize(await File.ReadAllTextAsync(_testSettingsPath), AppJsonSerializerContext.Default.AppSettings)!;
        loaded.RefreshIntervalSeconds.Should().Be(42);
        loaded.Providers.Should().HaveCount(3);
        events.Should().Be(20);
        File.Exists(_testSettingsPath + ".tmp").Should().BeFalse();
    }

    [TestMethod]
    public async Task SaveAsync_ReplacementCannotBeWritten_PreservesPreviousFileAndReportsFailure()
    {
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        await service.SaveAsync();
        var previous = await File.ReadAllTextAsync(_testSettingsPath);
        Directory.CreateDirectory(_testSettingsPath + ".tmp");
        service.Settings.RefreshIntervalSeconds = 42;
        var events = 0;
        service.SettingsChanged += (_, _) => Interlocked.Increment(ref events);
        Func<Task> save = () => service.SaveAsync();
        await save.Should().ThrowAsync<UnauthorizedAccessException>();
        (await File.ReadAllTextAsync(_testSettingsPath)).Should().Be(previous);
        events.Should().Be(0);
        // Quiet position saves tolerate IO failures and must not trigger a refresh.
        await service.SaveQuietAsync();
        events.Should().Be(0);
    }

    [TestMethod]
    public async Task LoadAsync_NullAndMalformedSections_RepairsWithoutLosingOtherSettings()
    {
        await File.WriteAllTextAsync(_testSettingsPath,
            """{"providers":null,"timeouts":null,"animation":{"steps":0},"refreshIntervalSeconds":-10,"showCostTotals":false,"taskbarDisplayEnabled":true}""");
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        await service.LoadAsync();
        service.Settings.Providers.Should().HaveCount(3);
        service.Settings.Timeouts.Should().NotBeNull();
        service.Settings.Animation.Steps.Should().Be(15);
        service.Settings.RefreshIntervalSeconds.Should().Be(120);
        service.Settings.ShowCostTotals.Should().BeFalse();
        service.Settings.TaskbarDisplayEnabled.Should().BeTrue();
    }

    [TestMethod]
    public async Task SaveAsync_WritesSettingsToFile()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        service.Settings.RefreshIntervalSeconds = 15;

        // Act
        await service.SaveAsync();

        // Assert
        File.Exists(_testSettingsPath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(_testSettingsPath);
        content.Should().Contain("15");
    }

    [TestMethod]
    public async Task SaveAsync_RaisesSettingsChangedEvent()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        var eventRaised = false;
        service.SettingsChanged += (_, _) => eventRaised = true;

        // Act
        await service.SaveAsync();

        // Assert
        eventRaised.Should().BeTrue();
    }

    [TestMethod]
    public async Task SaveAsync_WritesValidJson()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        await service.SaveAsync();

        // Assert
        var content = await File.ReadAllTextAsync(_testSettingsPath);
        var action = () => JsonDocument.Parse(content);
        action.Should().NotThrow();
    }

    #endregion

    #region Reset Tests

    [TestMethod]
    public void Reset_RestoresDefaultSettings()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        service.Settings.RefreshIntervalSeconds = 99;

        // Act
        service.Reset();

        // Assert
        var defaults = AppSettings.GetDefaults();
        service.Settings.RefreshIntervalSeconds.Should().Be(defaults.RefreshIntervalSeconds);
    }

    [TestMethod]
    public void Reset_RaisesSettingsChangedEvent()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        var eventRaised = false;
        service.SettingsChanged += (_, _) => eventRaised = true;

        // Act
        service.Reset();

        // Assert
        eventRaised.Should().BeTrue();
    }

    [TestMethod]
    public void Reset_RestoresDefaultProviders()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        service.Settings.Providers.Clear();

        // Act
        service.Reset();

        // Assert
        var defaults = AppSettings.GetDefaults();
        service.Settings.Providers.Should().HaveCount(defaults.Providers.Count);
    }

    #endregion

    #region SettingsChanged Event Tests

    [TestMethod]
    public async Task SettingsChanged_MultipleSubscribers_AllNotified()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        var count = 0;
        service.SettingsChanged += (_, _) => count++;
        service.SettingsChanged += (_, _) => count++;

        // Act
        await service.SaveAsync();

        // Assert
        count.Should().Be(2);
    }

    [TestMethod]
    public void SettingsChanged_NoSubscribers_DoesNotThrow()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act & Assert
        var action = () => service.Reset();
        action.Should().NotThrow();
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public async Task SaveAndLoad_RoundTrip_PreservesSettings()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        service.Settings.RefreshIntervalSeconds = 42;
        service.Settings.Providers =
        [
            new ProviderConfig { Id = "claude", IsEnabled = false, Order = 2 },
            new ProviderConfig { Id = "gemini", IsEnabled = true, Order = 0 }
        ];

        // Act
        await service.SaveAsync();
        var service2 = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        await service2.LoadAsync();

        // Assert
        service2.Settings.RefreshIntervalSeconds.Should().Be(42);
        service2.Settings.Providers.Should().HaveCount(2);
        service2.Settings.Providers.First(p => p.Id == "claude").IsEnabled.Should().BeFalse();
        service2.Settings.Providers.First(p => p.Id == "gemini").IsEnabled.Should().BeTrue();
    }

    [TestMethod]
    public async Task SaveAndLoad_WithTimeouts_PreservesValues()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        service.Settings.Timeouts.WslCommandTimeoutSeconds = 120;
        service.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds = 90;

        // Act
        await service.SaveAsync();
        var service2 = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        await service2.LoadAsync();

        // Assert
        service2.Settings.Timeouts.WslCommandTimeoutSeconds.Should().Be(120);
        service2.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds.Should().Be(90);
    }

    [TestMethod]
    public async Task SaveAndLoad_DeveloperMode_RoundTrip_PreservesValue()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        service.Settings.DeveloperModeEnabled = true;

        // Act
        await service.SaveAsync();
        var service2 = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        await service2.LoadAsync();

        // Assert
        service2.Settings.DeveloperModeEnabled.Should().BeTrue();
    }

    [TestMethod]
    public void Reset_RestoresDeveloperModeToFalse()
    {
        // Arrange
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);
        service.Settings.DeveloperModeEnabled = true;

        // Act
        service.Reset();

        // Assert
        service.Settings.DeveloperModeEnabled.Should().BeFalse();
    }

    #endregion

    #region Provider Security Filtering Tests

    [TestMethod]
    public async Task LoadAsync_MaliciousProviderIds_AreFiltered()
    {
        // Arrange - Try to inject malicious provider IDs
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderConfig { Id = "claude", IsEnabled = true, Order = 0 },
                new ProviderConfig { Id = "../../../etc/passwd", IsEnabled = true, Order = 1 },
                new ProviderConfig { Id = "'; DROP TABLE users; --", IsEnabled = true, Order = 2 },
                new ProviderConfig { Id = "<script>alert('xss')</script>", IsEnabled = true, Order = 3 },
                new ProviderConfig { Id = "codex", IsEnabled = true, Order = 4 }
            ]
        };
        await WriteSettingsFileAsync(settings);
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        await service.LoadAsync();

        // Assert
        service.Settings.Providers.Should().HaveCount(2);
        service.Settings.Providers.Select(p => p.Id).Should().BeEquivalentTo(["claude", "codex"]);
    }

    [TestMethod]
    public async Task LoadAsync_AllInvalidProviders_ResultsInEmptyList()
    {
        // Arrange
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderConfig { Id = "invalid1", IsEnabled = true, Order = 0 },
                new ProviderConfig { Id = "invalid2", IsEnabled = true, Order = 1 }
            ]
        };
        await WriteSettingsFileAsync(settings);
        var service = new TestableSettingsService(_mockLogger.Object, _testDirectory);

        // Act
        await service.LoadAsync();

        // Assert
        service.Settings.Providers.Should().BeEmpty();
    }

    #endregion

    #region Helper Methods

    private async Task WriteSettingsFileAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);
        await File.WriteAllTextAsync(_testSettingsPath, json);
    }

    #endregion

    private class TestableSettingsService : SettingsService
    {
        public TestableSettingsService(ILogger<SettingsService> logger, string testDirectory)
            : base(logger, testDirectory) { }
    }
}
