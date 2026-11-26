using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class UsageAlertEvaluatorPersistenceTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long BucketTicks = TimeSpan.FromMinutes(30).Ticks;

    private string _dir = string.Empty;
    private string FilePath => Path.Combine(_dir, UsageAlertEvaluator.FileName);

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AgentsWinBar.Alerts." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private UsageAlertEvaluator Create(ILogger<UsageAlertEvaluator>? logger = null, string? path = null) =>
        new(path ?? FilePath, logger ?? NullLogger<UsageAlertEvaluator>.Instance);

    /// <summary>A Codex weekly window at <paramref name="used"/> percent that resets <paramref name="minutesLeft"/> from <see cref="Now"/>.</summary>
    private static UsageData Weekly(double used, int minutesLeft) => new()
    {
        Provider = "codex",
        Weekly = new UsageWindow
        {
            Used = (int)used,
            Limit = 100,
            WindowMinutes = 10080,
            ResetAt = Now.AddMinutes(minutesLeft)
        }
    };

    private static string Key(string slot, long bucket, string level = "near") => $"codex|{slot}|{bucket}|{level}";

    private static long BucketOf(DateTime time) => time.ToUniversalTime().Ticks / BucketTicks;

    private static string FileJson(params (string Key, DateTime ReportedAt)[] entries) =>
        JsonSerializer.Serialize(
            new AlertsSentFile { Entries = entries.Select(e => new AlertSentEntry { Key = e.Key, ReportedAtUtc = e.ReportedAt }).ToList() },
            AppJsonSerializerContext.Default.AlertsSentFile);

    private string[] SavedKeys() =>
        JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonSerializerContext.Default.AlertsSentFile)!
            .Entries.Select(e => e.Key).ToArray();

    [TestMethod]
    public void Evaluate_SecondEvaluatorOnSameFile_DoesNotRepeatReportedAlert()
    {
        var data = Weekly(92, 3 * 24 * 60);

        var first = Create().Evaluate([data], Now);
        var afterRestart = Create().Evaluate([data], Now.AddMinutes(5));

        first.Should().ContainSingle();
        first[0].Title.Should().Be("Codex weekly usage");
        first[0].Message.Should().Contain("92% used");
        afterRestart.Should().BeEmpty("the same situation in the same window was already reported before the restart");
    }

    [TestMethod]
    public void Evaluate_NewWindowAfterRestart_AlertsAgain()
    {
        Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now).Should().ContainSingle();

        var alerts = Create().Evaluate([Weekly(92, 10 * 24 * 60)], Now.AddDays(7));

        alerts.Should().ContainSingle("a different reset time is a new usage window");
    }

    [TestMethod]
    public void Evaluate_DifferentLevelAfterRestart_AlertsAgain()
    {
        Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now).Should().ContainSingle();

        var alerts = Create().Evaluate([Weekly(100, 3 * 24 * 60)], Now.AddMinutes(10));

        alerts.Should().ContainSingle().Which.Title.Should().Be("Codex weekly limit reached");
    }

    [TestMethod]
    public void Evaluate_WhenNothingNewIsReported_DoesNotWriteFile()
    {
        Create().Evaluate([Weekly(20, 3 * 24 * 60)], Now).Should().BeEmpty();

        File.Exists(FilePath).Should().BeFalse();
    }

    [TestMethod]
    public void Evaluate_AfterReporting_WritesFileAndNoTempFile()
    {
        Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now);

        File.Exists(FilePath).Should().BeTrue();
        File.Exists(FilePath + ".tmp").Should().BeFalse();
        SavedKeys().Should().ContainSingle().Which.Should().StartWith("codex|weekly|").And.EndWith("|near");
    }

    [TestMethod]
    public void Evaluate_MissingFolder_CreatesItAndSaves()
    {
        var nested = Path.Combine(_dir, "a", "b", UsageAlertEvaluator.FileName);

        Create(path: nested).Evaluate([Weekly(92, 3 * 24 * 60)], Now).Should().ContainSingle();

        File.Exists(nested).Should().BeTrue();
    }

    [TestMethod]
    public void Evaluate_CorruptFile_StartsEmptyAndStillAlerts()
    {
        File.WriteAllText(FilePath, "{ this is not json");

        var alerts = Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now);

        alerts.Should().ContainSingle();
        SavedKeys().Should().ContainSingle("the corrupt file is replaced by a valid one");
    }

    [TestMethod]
    public void Evaluate_EmptyFile_StartsEmptyAndStillAlerts()
    {
        File.WriteAllBytes(FilePath, []);

        var alerts = Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now);

        alerts.Should().ContainSingle();
    }

    [TestMethod]
    public void Evaluate_JsonNullFile_StartsEmptyAndStillAlerts()
    {
        File.WriteAllText(FilePath, "null");

        var alerts = Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now);

        alerts.Should().ContainSingle();
    }

    [TestMethod]
    public void Evaluate_OversizedFile_StartsEmptyAndStillAlerts()
    {
        // Would suppress this alert if it were read, but it is over 256 KB
        var resetAt = Now.AddMinutes(3 * 24 * 60);
        var json = FileJson((Key("weekly", BucketOf(resetAt)), Now));
        File.WriteAllText(FilePath, json + new string(' ', (int)UsageAlertEvaluator.MaxFileBytes));
        new FileInfo(FilePath).Length.Should().BeGreaterThan(UsageAlertEvaluator.MaxFileBytes);

        var alerts = Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now);

        alerts.Should().ContainSingle();
        new FileInfo(FilePath).Length.Should().BeLessThan(UsageAlertEvaluator.MaxFileBytes);
    }

    [TestMethod]
    public void Evaluate_FileJustUnderLimit_IsStillRead()
    {
        var resetAt = Now.AddMinutes(3 * 24 * 60);
        var json = FileJson((Key("weekly", BucketOf(resetAt)), Now));
        File.WriteAllText(FilePath, json + new string(' ', (int)UsageAlertEvaluator.MaxFileBytes - json.Length - 10));

        Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now).Should().BeEmpty();
    }

    [TestMethod]
    public void Evaluate_UnreadableFile_LogsAndStartsEmpty()
    {
        var logger = new Mock<ILogger<UsageAlertEvaluator>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        File.WriteAllText(FilePath, "{ nope");

        Create(logger.Object).Evaluate([Weekly(92, 3 * 24 * 60)], Now).Should().ContainSingle();

        VerifyLogged(logger, LogLevel.Warning);
    }

    [TestMethod]
    public void Evaluate_WhenSaveFails_StillReturnsAlertsLogsWarningAndDoesNotThrow()
    {
        // The "folder" of the file is a regular file, so creating it fails
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var logger = new Mock<ILogger<UsageAlertEvaluator>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var evaluator = Create(logger.Object, Path.Combine(blocker, UsageAlertEvaluator.FileName));
        var data = Weekly(92, 3 * 24 * 60);

        var alerts = evaluator.Evaluate([data], Now);
        var again = evaluator.Evaluate([data], Now.AddMinutes(1));

        alerts.Should().ContainSingle();
        again.Should().BeEmpty("the in-memory memory still works");
        VerifyLogged(logger, LogLevel.Warning);
    }

    [TestMethod]
    public void Evaluate_ReportedBeforeRestart_SurvivesSeveralRestarts()
    {
        var data = Weekly(92, 3 * 24 * 60);
        Create().Evaluate([data], Now).Should().ContainSingle();

        for (var i = 1; i <= 3; i++)
        {
            Create().Evaluate([data], Now.AddHours(i)).Should().BeEmpty();
        }
    }

    [TestMethod]
    public void Evaluate_ExpiredWindowKeys_AreDroppedOnLoad()
    {
        var expired = Key("weekly", BucketOf(Now.AddDays(-2)));
        var recentlyEnded = Key("session", BucketOf(Now.AddHours(-3)));
        var current = Key("weekly", BucketOf(Now.AddDays(3)));
        File.WriteAllText(FilePath, FileJson((expired, Now.AddDays(-9)), (recentlyEnded, Now.AddHours(-8)), (current, Now.AddHours(-1))));

        Create().Evaluate([], Now);

        SavedKeys().Should().BeEquivalentTo([recentlyEnded, current]);
    }

    [TestMethod]
    public void Evaluate_KeysWithoutResetTime_AreDroppedAfterSevenDays()
    {
        var old = Key("weekly", 0);
        var fresh = Key("session", 0);
        File.WriteAllText(FilePath, FileJson((old, Now.AddDays(-7).AddMinutes(-1)), (fresh, Now.AddDays(-6))));

        Create().Evaluate([], Now);

        SavedKeys().Should().BeEquivalentTo([fresh]);
    }

    [TestMethod]
    public void Evaluate_KeyWithoutResetTime_KeepsFirstReportedTimeAcrossRepeats()
    {
        var data = new UsageData { Provider = "codex", Weekly = new UsageWindow { Used = 92, Limit = 100, WindowMinutes = 10080 } };
        Create().Evaluate([data], Now).Should().ContainSingle();

        // Still suppressed after 6 days (repeats do not refresh the time), reported again after 8
        Create().Evaluate([data], Now.AddDays(6)).Should().BeEmpty();
        Create().Evaluate([data], Now.AddDays(6).AddHours(23)).Should().BeEmpty();
        Create().Evaluate([data], Now.AddDays(8)).Should().ContainSingle();
    }

    [TestMethod]
    public void Evaluate_MoreThanMaxKeys_KeepsTheNewest()
    {
        var future = BucketOf(Now.AddDays(30));
        var entries = Enumerable.Range(0, UsageAlertEvaluator.MaxKeys + 100)
            .Select(i => (Key("weekly", future + i), Now.AddMinutes(-1000 + i)))
            .ToArray();
        File.WriteAllText(FilePath, FileJson(entries));

        Create().Evaluate([], Now);

        var saved = SavedKeys();
        saved.Should().HaveCount(UsageAlertEvaluator.MaxKeys);
        saved.Should().NotContain(entries[0].Item1).And.NotContain(entries[99].Item1);
        saved.Should().Contain(entries[100].Item1).And.Contain(entries[^1].Item1);
    }

    [TestMethod]
    public void Evaluate_AtMaxKeys_NewAlertEvictsTheOldestKey()
    {
        var future = BucketOf(Now.AddDays(30));
        var entries = Enumerable.Range(0, UsageAlertEvaluator.MaxKeys)
            .Select(i => (Key("weekly", future + i), Now.AddMinutes(-1000 + i)))
            .ToArray();
        File.WriteAllText(FilePath, FileJson(entries));

        Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now).Should().ContainSingle();

        var saved = SavedKeys();
        saved.Should().HaveCount(UsageAlertEvaluator.MaxKeys);
        saved.Should().NotContain(entries[0].Item1);
        saved.Should().Contain(k => k.StartsWith("codex|weekly|") && k.EndsWith("|near") && k.Contains(BucketOf(Now.AddDays(3)).ToString()));
    }

    [TestMethod]
    public void Evaluate_FileWithGarbageEntries_IgnoresThem()
    {
        var resetAt = Now.AddMinutes(3 * 24 * 60);
        var good = Key("weekly", BucketOf(resetAt));
        File.WriteAllText(FilePath,
            "{\"version\":1,\"entries\":[null,{\"key\":\"\"},{\"key\":\"no-bucket\",\"reportedAtUtc\":\"2026-10-01T00:00:00Z\"}," +
            $"{{\"key\":\"{good}\",\"reportedAtUtc\":\"2026-10-01T23:00:00Z\"}}]}}");

        Create().Evaluate([Weekly(92, 3 * 24 * 60)], Now).Should().BeEmpty();
    }

    [TestMethod]
    public void Evaluate_ParameterlessEvaluator_WritesNoFileAndForgetsOnRestart()
    {
        var data = Weekly(92, 3 * 24 * 60);
        var before = Directory.GetFileSystemEntries(_dir);

        new UsageAlertEvaluator().Evaluate([data], Now).Should().ContainSingle();
        new UsageAlertEvaluator().Evaluate([data], Now).Should().ContainSingle("nothing is shared between in-memory evaluators");

        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo(before);
        File.Exists(FilePath).Should().BeFalse();
    }

    [TestMethod]
    public void CreatePersistent_WithSettingsPath_PutsFileBesideSettings()
    {
        var settingsPath = Path.Combine(_dir, "settings.json");
        var evaluator = UsageAlertEvaluator.CreatePersistent(settingsPath, NullLogger<UsageAlertEvaluator>.Instance);

        evaluator.Evaluate([Weekly(92, 3 * 24 * 60)], Now).Should().ContainSingle();

        File.Exists(Path.Combine(_dir, "alerts-sent.json")).Should().BeTrue();
    }

    [TestMethod]
    public async Task Evaluate_ConcurrentCalls_ReportEachSituationOnce()
    {
        var evaluator = Create();
        var data = Weekly(92, 3 * 24 * 60);

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => evaluator.Evaluate([data], Now).Count)));

        results.Sum().Should().Be(1);
        SavedKeys().Should().ContainSingle();
    }

    private static void VerifyLogged(Mock<ILogger<UsageAlertEvaluator>> logger, LogLevel level) =>
        logger.Verify(l => l.Log(
            level,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce);
}
