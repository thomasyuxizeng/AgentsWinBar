using AgentsWinBar.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class FileLoggerProviderTests
{
    private DirectoryInfo _directory = null!;

    [TestInitialize]
    public void Setup() => _directory = Directory.CreateTempSubdirectory("fake-diagnostics-");

    [TestCleanup]
    public void Cleanup() => _directory.Delete(recursive: true);

    [TestMethod]
    public void Log_WhenExceptionIsAttached_WritesItsTypeAndOmitsItsMessage()
    {
        using var provider = new FileLoggerProvider(_directory.FullName);
        var logger = provider.CreateLogger("Test.Category");
        var exception = new InvalidOperationException("synthetic private response");

        logger.Log(LogLevel.Warning, default, "Formatted warning", exception,
            (message, error) => error == null ? message : $"{message} {error}");

        var line = File.ReadAllText(Path.Combine(_directory.FullName, "app.log"));
        line.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T");
        line.Should().Contain("[Warning] Test.Category").And.Contain("Formatted warning")
            .And.Contain(nameof(InvalidOperationException)).And.NotContain(exception.Message);
    }

    [TestMethod]
    public void Log_WhenBelowWarning_DoesNotCreateAFile()
    {
        using var provider = new FileLoggerProvider(_directory.FullName);

        provider.CreateLogger("Test.Category").LogInformation("Ignored information");

        _directory.GetFiles().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("app.log")]
    [DataRow("crash.log")]
    public void Append_WhenLogWouldExceedLimit_ReplacesTheOneBackup(string fileName)
    {
        var path = Path.Combine(_directory.FullName, fileName);
        File.WriteAllText(path, new string('x', RollingLogFile.MaxBytes + 1));
        File.WriteAllText(path + ".1", "Previous backup");

        RollingLogFile.Append(path, "New entry");

        File.ReadAllText(path).Should().Be("New entry" + Environment.NewLine);
        new FileInfo(path + ".1").Length.Should().Be(RollingLogFile.MaxBytes);
        _directory.GetFiles().Select(f => f.Name).Should().BeEquivalentTo(fileName, fileName + ".1");
    }
}
