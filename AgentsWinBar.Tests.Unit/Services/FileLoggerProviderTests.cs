using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
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
    public void DescribeException_WhenThrownWithInner_ReturnsTypesAndFramesWithoutMessages()
    {
        Exception caught;
        try { ThrowWrapped(); throw new UnreachableException(); }
        catch (Exception ex) { caught = ex; }

        var text = RollingLogFile.DescribeException(caught);

        text.Should().StartWith(" (System.InvalidOperationException)");
        text.Should().Contain("inner (System.IO.IOException)");
        text.Should().Contain(nameof(ThrowWrapped)).And.Contain(nameof(ThrowInner));
        text.Should().NotContain("synthetic outer message").And.NotContain(@"C:\synthetic\private\path");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWrapped()
    {
        try { ThrowInner(); }
        catch (IOException inner) { throw new InvalidOperationException("synthetic outer message", inner); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInner() => throw new IOException(@"C:\synthetic\private\path could not be read");

    [TestMethod]
    public void Log_WhenExceptionIsAttached_WritesItsTypeAndOmitsItsMessage()
    {
        using var provider = new FileLoggerProvider(_directory.FullName);
        var logger = provider.CreateLogger("Test.Category");
        var exception = new InvalidOperationException("synthetic private response");

        logger.LogWarning(exception, "Formatted warning");

        var line = File.ReadAllText(Path.Combine(_directory.FullName, "app.log"));
        line.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T");
        line.Should().Contain("[Warning] Test.Category: Formatted warning")
            .And.Contain(nameof(InvalidOperationException)).And.NotContain(exception.Message);
    }

    [TestMethod]
    [DataRow(@"C:\Users\synthetic-user\AppData\Local\AgentsWinBar\costs.json")]
    [DataRow("WSL is not running on this synthetic machine")]
    [DataRow("synthetic_identifier_that_is_longer_than_forty_chars")]
    public void Log_WhenArgumentIsPathSentenceOrLongText_WritesOmitted(string argument)
    {
        var line = LogWarning("Could not read {Path} for {Provider}", argument, "claude");

        line.Should().EndWith(": Could not read [omitted] for claude" + Environment.NewLine)
            .And.NotContain(argument);
    }

    [TestMethod]
    public void Log_WhenArgumentsAreProviderNameIntAndEnum_WritesThem()
    {
        var line = LogWarning("{Provider} answered {Status} ({Kind}) after {Seconds:0.0} s; detail {Detail}",
            "codex-native", 429, HttpStatusCode.TooManyRequests, 1.5, null);

        line.Should().EndWith(": codex-native answered 429 (TooManyRequests) after 1.5 s; detail [omitted]" + Environment.NewLine);
    }

    [TestMethod]
    public void Log_WhenMessageHasNoPlaceholders_WritesItAsIs()
    {
        var line = LogWarning("WSL is not installed");

        line.Should().EndWith("[Warning] Test.Category: WSL is not installed" + Environment.NewLine);
    }

    [TestMethod]
    public void Log_WhenStateHasNoTemplate_WritesOnlyCategoryAndLevel()
    {
        using var provider = new FileLoggerProvider(_directory.FullName);

        provider.CreateLogger("Test.Category").Log(LogLevel.Error, default, @"synthetic free text C:\Users\synthetic-user", null,
            (message, _) => message);

        File.ReadAllText(Path.Combine(_directory.FullName, "app.log"))
            .Should().EndWith(" [Error] Test.Category" + Environment.NewLine);
    }

    private string LogWarning(string template, params object?[] arguments)
    {
        using var provider = new FileLoggerProvider(_directory.FullName);
#pragma warning disable CA2254 // The template is the input under test.
        provider.CreateLogger("Test.Category").LogWarning(template, arguments);
#pragma warning restore CA2254
        return File.ReadAllText(Path.Combine(_directory.FullName, "app.log"));
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
