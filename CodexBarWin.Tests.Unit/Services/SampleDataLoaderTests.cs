using AwesomeAssertions;
using CodexBarWin.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class SampleDataLoaderTests
{
    private Mock<ILogger<SampleDataLoader>> _mockLogger = null!;
    private SampleDataLoader _loader = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<SampleDataLoader>>();
        _loader = new SampleDataLoader(_mockLogger.Object);
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    [DataRow("gemini")]
    public void LoadSampleJson_WhenValidProviderAndFileExists_ReturnsJsonContent(string provider)
    {
        // Act
        var result = _loader.LoadSampleJson(provider);

        // Assert
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().Contain(provider);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => (Convert.ToString(v) ?? string.Empty).Contains("Loaded sample data")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    [DataRow("  claude  ", "claude")]
    [DataRow("CLAUDE", "claude")]
    [DataRow("Codex", "codex")]
    [DataRow("GEMINI", "gemini")]
    public void LoadSampleJson_WhenValidProviderWithWhitespaceOrCase_NormalizesAndReturnsContent(string input, string expectedSubstring)
    {
        // Act
        var result = _loader.LoadSampleJson(input);

        // Assert
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().Contain(expectedSubstring);
    }

    [TestMethod]
    public void LoadSampleJson_WhenValidProviderAndFileDoesNotExist_LogsWarningAndReturnsNull()
    {
        // Arrange - temporarily rename gemini.json to test missing file behavior
        var samplesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples");
        var filePath = Path.Combine(samplesDir, "gemini.json");
        var tempBackup = filePath + ".missing_test_backup";

        var fileExisted = File.Exists(filePath);
        if (fileExisted)
        {
            File.Move(filePath, tempBackup, overwrite: true);
        }

        try
        {
            // Act
            var result = _loader.LoadSampleJson("gemini");

            // Assert
            result.Should().BeNull();

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => (Convert.ToString(v) ?? string.Empty).Contains("Sample data file not found")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        finally
        {
            if (fileExisted && File.Exists(tempBackup))
            {
                File.Move(tempBackup, filePath, overwrite: true);
            }
        }
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("unknown")]
    [DataRow("copilot")]
    [DataRow("openai")]
    public void LoadSampleJson_WhenInvalidProvider_LogsErrorAndReturnsNull(string invalidProvider)
    {
        // Act
        var result = _loader.LoadSampleJson(invalidProvider);

        // Assert
        result.Should().BeNull();

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => (Convert.ToString(v) ?? string.Empty).Contains("Failed to load sample data")),
                It.IsAny<ArgumentException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void LoadSampleJson_WhenProviderNullOrWhitespace_LogsErrorAndReturnsNull(string? provider)
    {
        // Act
        var result = _loader.LoadSampleJson(provider!);

        // Assert
        result.Should().BeNull();

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => (Convert.ToString(v) ?? string.Empty).Contains("Failed to load sample data")),
                It.IsAny<ArgumentException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    public void LoadSampleJson_WhenFileReadThrowsException_LogsErrorAndReturnsNull()
    {
        // Arrange - lock the file exclusively to cause File.ReadAllText to throw IOException
        var samplesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples");
        var filePath = Path.Combine(samplesDir, "claude.json");

        if (!File.Exists(filePath))
        {
            Assert.Inconclusive("claude.json not present in output directory");
            return;
        }

        using var lockStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        var result = _loader.LoadSampleJson("claude");

        // Assert
        result.Should().BeNull();

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => (Convert.ToString(v) ?? string.Empty).Contains("Failed to load sample data")),
                It.IsAny<IOException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
