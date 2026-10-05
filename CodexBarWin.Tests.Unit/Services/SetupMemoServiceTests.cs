using AwesomeAssertions;
using CodexBarWin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class SetupMemoServiceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SetupMemoService Create(Clock clock, string? filePath = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "CodexBarWin-SetupMemoTests", Guid.NewGuid().ToString("N"));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.SettingsFilePath).Returns(Path.Combine(folder, "settings.json"));
        return new SetupMemoService(settings.Object, NullLogger<SetupMemoService>.Instance, clock, filePath);
    }

    [TestMethod]
    public async Task SaveAsync_FreshCompatibleVersion_RoundTripsAtomicallyBesideSettings()
    {
        var clock = new Clock();
        var memo = Create(clock);

        await memo.SaveAsync("codexbar 0.17.0");

        Path.GetFileName(memo.FilePath).Should().Be("setup-ok.json");
        File.Exists(memo.FilePath).Should().BeTrue();
        File.Exists(memo.FilePath + ".tmp").Should().BeFalse();
        clock.Now += TimeSpan.FromHours(23);
        (await Create(clock, memo.FilePath).IsValidAsync()).Should().BeTrue();
    }

    [TestMethod]
    [DataRow(1439, true)]
    [DataRow(1440, false)]
    [DataRow(1441, false)]
    [DataRow(-1, false)]
    public async Task IsValidAsync_AgeBoundary_RequiresLessThan24Hours(int ageMinutes, bool expected)
    {
        var clock = new Clock();
        var memo = Create(clock);
        await memo.SaveAsync("0.17.0");

        clock.Now += TimeSpan.FromMinutes(ageMinutes);

        (await memo.IsValidAsync()).Should().Be(expected);
    }

    [TestMethod]
    [DataRow("0.16.9", false)]
    [DataRow("0.17.0", true)]
    [DataRow("codexbar 1.0.0", true)]
    [DataRow("unknown", false)]
    [DataRow("unparseable", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public async Task IsValidAsync_Version_RequiresKnownMinimumVersion(string? version, bool expected)
    {
        var memo = Create(new Clock());
        await memo.SaveAsync(version);

        (await memo.IsValidAsync()).Should().Be(expected);
    }

    [TestMethod]
    [DataRow("not JSON")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"cliVersion\":\"0.17.0\",\"checkedAtUtc\":\"invalid\"}")]
    public async Task IsValidAsync_CorruptMemo_ReturnsFalse(string json)
    {
        var memo = Create(new Clock());
        Directory.CreateDirectory(Path.GetDirectoryName(memo.FilePath)!);
        await File.WriteAllTextAsync(memo.FilePath, json);

        (await memo.IsValidAsync()).Should().BeFalse();
    }

    [TestMethod]
    public async Task IsValidAsync_MissingMemo_ReturnsFalse()
    {
        (await Create(new Clock()).IsValidAsync()).Should().BeFalse();
    }

    [TestMethod]
    public async Task InvalidateAsync_PreviousSuccess_DeletesMemo()
    {
        var memo = Create(new Clock());
        await memo.SaveAsync("0.17.0");

        await memo.InvalidateAsync();
        await memo.InvalidateAsync();

        File.Exists(memo.FilePath).Should().BeFalse();
        (await memo.IsValidAsync()).Should().BeFalse();
    }

    [TestMethod]
    public async Task CheckAsync_CommandNotFound_DeletesSuccessfulMemo()
    {
        var memo = Create(new Clock());
        await memo.SaveAsync("0.17.0");
        var wsl = new Mocks.MockWslService();
        wsl.SetCommandResult("codexbar --version", new WslResult { ExitCode = 127 });
        var checker = new SetupChecker(wsl, NullLogger<SetupChecker>.Instance, memo);

        (await checker.CheckAsync()).IsReady.Should().BeFalse();

        File.Exists(memo.FilePath).Should().BeFalse();
    }

    [TestMethod]
    public async Task SaveAsync_FilesystemFailureOrCancellation_DoesNotThrow()
    {
        var memo = Create(new Clock());
        Directory.CreateDirectory(memo.FilePath);
        await memo.SaveAsync("0.17.0");
        (await memo.IsValidAsync()).Should().BeFalse();
        await memo.InvalidateAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await memo.SaveAsync("0.17.0", cts.Token);
        await memo.InvalidateAsync(cts.Token);
        (await memo.IsValidAsync(cts.Token)).Should().BeFalse();
    }
}
