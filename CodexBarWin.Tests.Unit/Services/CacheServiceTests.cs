using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class CacheServiceTests
{
    private Mock<ILogger<CacheService>> _mockLogger = null!;
    private MockSettingsService _mockSettingsService = null!;
    private CacheService _cacheService = null!;
    private string _cachePath = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<CacheService>>();
        _mockSettingsService = new MockSettingsService();
        _cachePath = Path.Combine(Path.GetTempPath(), "CodexBarWin-CacheTests", Guid.NewGuid().ToString("N"), "cache.json");
        _cacheService = new CacheService(_mockLogger.Object, _mockSettingsService, _cachePath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var folder = Path.GetDirectoryName(_cachePath)!;
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    [TestMethod]
    public void Get_FutureTimestamp_ReturnsNull()
    {
        _cacheService.Set("claude", new UsageData { Provider = "claude", FetchedAt = DateTime.UtcNow.AddHours(1) });
        _cacheService.Get("claude").Should().BeNull();
        _cacheService.GetAll().Should().BeEmpty();
    }

    [TestMethod]
    public void Get_LocalTimestamp_NormalizesToUtc()
    {
        _cacheService.Set("claude", new UsageData { Provider = "claude", FetchedAt = DateTime.UtcNow.AddMinutes(-1).ToLocalTime() });
        _cacheService.Get("claude").Should().NotBeNull();
    }

    [TestMethod]
    public async Task GetLastKnown_WhenFileAndReadingExpired_RestoresReadingWithoutTreatingItAsFresh()
    {
        var old = DateTime.UtcNow.AddDays(-30);
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        await File.WriteAllTextAsync(_cachePath, System.Text.Json.JsonSerializer.Serialize(new CacheData
        {
            Timestamp = old, Items = new() { ["claude"] = new UsageData
                { Provider = "claude", FetchedAt = old, Session = new() { ReportedPercent = 42 } } }
        }, AppJsonSerializerContext.Default.CacheData));
        await _cacheService.LoadAsync();
        _cacheService.Get("claude").Should().BeNull();
        _cacheService.GetLastKnown("CLAUDE")!.Session!.Percent.Should().Be(42);
    }

    [TestMethod]
    public void Set_WhenFailedReading_DoesNotReplaceSuccessfulZero()
    {
        var zero = new UsageData { Provider = "claude", Session = new() { ReportedPercent = 0 }, UsageFetchOutcome = FetchOutcome.Success };
        _cacheService.Set("claude", zero);
        _cacheService.Set("claude", new() { Provider = "claude", UsageFetchOutcome = FetchOutcome.Failed });
        _cacheService.GetLastKnown("claude").Should().BeSameAs(zero);
    }

    [TestMethod]
    public async Task SaveAsync_ConcurrentSaves_PreservesCompleteCache()
    {
        _cacheService.Set("claude", new UsageData { Provider = "claude" });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _cacheService.SaveAsync()));
        _cacheService.Clear();
        await _cacheService.LoadAsync();
        _cacheService.Get("claude").Should().NotBeNull();
        File.Exists(_cachePath + ".tmp").Should().BeFalse();
    }

    [TestMethod]
    public async Task LoadAsync_DeveloperMode_DoesNotReadOrOverwriteRealCache()
    {
        _cacheService.Set("claude", new UsageData { Provider = "claude", Plan = "Real" });
        await _cacheService.SaveAsync();
        var original = await File.ReadAllTextAsync(_cachePath);
        _cacheService.Clear();
        _mockSettingsService.Settings.DeveloperModeEnabled = true;
        await _cacheService.LoadAsync();
        _cacheService.Set("claude", new UsageData { Provider = "claude", Plan = "Sample" });
        await _cacheService.SaveAsync();
        _cacheService.GetAll().Should().BeEmpty();
        (await File.ReadAllTextAsync(_cachePath)).Should().Be(original);
        _mockSettingsService.Settings.DeveloperModeEnabled = false;
        _cacheService.GetAll().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{\"provider\":\"claude\"}")]
    [DataRow("{\"provider\":\"codex\",\"fetchedAt\":\"$now\"}")]
    [DataRow("{\"provider\":\"claude\",\"fetchedAt\":\"$now\",\"extraWindows\":null}")]
    [DataRow("{\"provider\":\"claude\",\"fetchedAt\":\"$now\",\"error\":\"failed\"}")]
    [DataRow("{\"provider\":\"claude\",\"fetchedAt\":\"$now\",\"commandNotFound\":true}")]
    public async Task LoadAsync_PartialOrInvalidEntry_SkipsEntry(string item)
    {
        var now = DateTime.UtcNow.ToString("O");
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var json = "{\"timestamp\":\"" + now + "\",\"items\":{\"claude\":" + item.Replace("$now", now) + "}}";
        await File.WriteAllTextAsync(_cachePath, json);
        await _cacheService.LoadAsync();
        _cacheService.GetAll().Should().BeEmpty();
    }

    [TestMethod]
    public async Task LoadAsync_InvalidSiblingEntry_RestoresValidProvider()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var now = DateTime.UtcNow.ToString("O");
        await File.WriteAllTextAsync(_cachePath,
            "{\"timestamp\":\"" + now + "\",\"items\":{\"claude\":null,\"codex\":{\"provider\":\"codex\",\"fetchedAt\":\"" + now + "\"}}}");
        await _cacheService.LoadAsync();
        _cacheService.GetAll().Keys.Should().Equal("codex");
    }

    [TestMethod]
    public async Task LoadAsync_ExtraWindowHasNullNestedWindow_SkipsCorruptProviderAndRestoresValidSibling()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var now = DateTime.UtcNow.ToString("O");
        await File.WriteAllTextAsync(_cachePath, $$$"""
            {"timestamp":"{{{now}}}","items":{
              "claude":{"provider":"claude","fetchedAt":"{{{now}}}","extraWindows":[{"key":"quota","title":"Quota","window":null}]},
              "codex":{"provider":"codex","fetchedAt":"{{{now}}}","extraWindows":[{"key":"quota","title":"Quota","window":{"reportedPercent":12}}]}
            }}
            """);

        await _cacheService.LoadAsync();

        _cacheService.Get("claude").Should().BeNull();
        _cacheService.GetAll().Should().ContainSingle().Which.Key.Should().Be("codex");
        _cacheService.Get("codex")!.ExtraWindows.Single().Window.Percent.Should().Be(12);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"timestamp\":\"$now\",\"items\":null}")]
    [DataRow("{\"timestamp\":\"$future\",\"items\":{\"claude\":{\"provider\":\"claude\",\"fetchedAt\":\"$now\"}}}")]
    public async Task LoadAsync_CorruptOrFutureCache_ReturnsEmptyCache(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        await File.WriteAllTextAsync(_cachePath, json.Replace("$now", DateTime.UtcNow.ToString("O"))
            .Replace("$future", DateTime.UtcNow.AddHours(1).ToString("O")));
        await _cacheService.LoadAsync();
        _cacheService.GetAll().Should().BeEmpty();
    }

    [TestMethod]
    public void Get_NonExistentProvider_ReturnsNull()
    {
        // Act
        var result = _cacheService.Get("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [TestMethod]
    public void Set_ThenGet_ReturnsCachedData()
    {
        // Arrange
        var data = new UsageData
        {
            Provider = "claude",
            Plan = "Pro",
            FetchedAt = DateTime.UtcNow
        };

        // Act
        _cacheService.Set("claude", data);
        var result = _cacheService.Get("claude");

        // Assert
        result.Should().NotBeNull();
        result!.Provider.Should().Be("claude");
        result.Plan.Should().Be("Pro");
    }

    [TestMethod]
    public void Get_ExpiredCache_ReturnsNull()
    {
        // Arrange
        _mockSettingsService.Settings.CacheExpiryMinutes = 5;
        var oldData = new UsageData
        {
            Provider = "claude",
            FetchedAt = DateTime.UtcNow.AddMinutes(-6)
        };

        _cacheService.Set("claude", oldData);

        // Act
        var result = _cacheService.Get("claude");

        // Assert
        result.Should().BeNull();
    }

    [TestMethod]
    public void Get_NotExpiredCache_ReturnsData()
    {
        // Arrange
        _mockSettingsService.Settings.CacheExpiryMinutes = 5;
        var recentData = new UsageData
        {
            Provider = "claude",
            FetchedAt = DateTime.UtcNow.AddMinutes(-2)
        };

        _cacheService.Set("claude", recentData);

        // Act
        var result = _cacheService.Get("claude");

        // Assert
        result.Should().NotBeNull();
    }

    [TestMethod]
    public void Clear_RemovesAllCachedData()
    {
        // Arrange
        _cacheService.Set("claude", new UsageData { Provider = "claude", FetchedAt = DateTime.UtcNow });
        _cacheService.Set("codex", new UsageData { Provider = "codex", FetchedAt = DateTime.UtcNow });

        // Act
        _cacheService.Clear();

        // Assert
        _cacheService.Get("claude").Should().BeNull();
        _cacheService.Get("codex").Should().BeNull();
    }

    [TestMethod]
    public void GetAll_ReturnsOnlyNonExpiredData()
    {
        // Arrange
        _mockSettingsService.Settings.CacheExpiryMinutes = 5;

        var recentData = new UsageData { Provider = "claude", FetchedAt = DateTime.UtcNow };
        var oldData = new UsageData { Provider = "codex", FetchedAt = DateTime.UtcNow.AddMinutes(-10) };

        _cacheService.Set("claude", recentData);
        _cacheService.Set("codex", oldData);

        // Act
        var result = _cacheService.GetAll();

        // Assert
        result.Should().HaveCount(1);
        result.Should().ContainKey("claude");
        result.Should().NotContainKey("codex");
    }

    [TestMethod]
    public void GetAll_EmptyCache_ReturnsEmptyDictionary()
    {
        // Act
        var result = _cacheService.GetAll();

        // Assert
        result.Should().BeEmpty();
    }

    [TestMethod]
    public void Set_OverwritesExistingData()
    {
        // Arrange
        var originalData = new UsageData { Provider = "claude", Plan = "Free", FetchedAt = DateTime.UtcNow };
        var updatedData = new UsageData { Provider = "claude", Plan = "Pro", FetchedAt = DateTime.UtcNow };

        // Act
        _cacheService.Set("claude", originalData);
        _cacheService.Set("claude", updatedData);
        var result = _cacheService.Get("claude");

        // Assert
        result.Should().NotBeNull();
        result!.Plan.Should().Be("Pro");
    }

    [TestMethod]
    public void Get_CacheExpiryBoundary_ReturnsDataJustBeforeExpiry()
    {
        // Arrange
        _mockSettingsService.Settings.CacheExpiryMinutes = 5;
        var almostExpiredData = new UsageData
        {
            Provider = "claude",
            FetchedAt = DateTime.UtcNow.AddMinutes(-4).AddSeconds(-59)
        };

        _cacheService.Set("claude", almostExpiredData);

        // Act
        var result = _cacheService.Get("claude");

        // Assert
        result.Should().NotBeNull();
    }

    [TestMethod]
    public void Get_NullSettingsService_UsesDefaultExpiry()
    {
        // Arrange
        var cacheServiceWithoutSettings = new CacheService(_mockLogger.Object, settingsService: null);
        var data = new UsageData
        {
            Provider = "claude",
            FetchedAt = DateTime.UtcNow.AddMinutes(-4)
        };

        cacheServiceWithoutSettings.Set("claude", data);

        // Act
        var result = cacheServiceWithoutSettings.Get("claude");

        // Assert
        result.Should().NotBeNull();
    }

    [TestMethod]
    public void GetAll_MultipleProviders_ReturnsAllNonExpired()
    {
        // Arrange
        _mockSettingsService.Settings.CacheExpiryMinutes = 10;

        _cacheService.Set("claude", new UsageData { Provider = "claude", FetchedAt = DateTime.UtcNow });
        _cacheService.Set("codex", new UsageData { Provider = "codex", FetchedAt = DateTime.UtcNow });
        _cacheService.Set("gemini", new UsageData { Provider = "gemini", FetchedAt = DateTime.UtcNow });

        // Act
        var result = _cacheService.GetAll();

        // Assert
        result.Should().HaveCount(3);
        result.Keys.Should().Contain(["claude", "codex", "gemini"]);
    }
}
