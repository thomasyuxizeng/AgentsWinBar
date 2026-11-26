using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Services;
using AgentsWinBar.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class LegacyDataImporterTests
{
    private string _root = null!;
    private string _localAppData = null!;
    private string _target = null!;
    private List<(string Source, Exception Error)> _reported = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentswinbar-import-tests", Guid.NewGuid().ToString("N"));
        _localAppData = Path.Combine(_root, "Local");
        _target = Path.Combine(_localAppData, "AgentsWinBar");
        _reported = [];
        Directory.CreateDirectory(_localAppData);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string PackagedSource(string packageFamily = "CodexBarWin_w05y65rx3mcz6") =>
        Path.Combine(_localAppData, "Packages", packageFamily, "LocalCache", "Local", "CodexBarWin");

    private string UnpackagedSource => Path.Combine(_localAppData, "CodexBarWin");

    private static readonly string IndexRelative = Path.Combine("cost-index", "index.sqlite");

    /// <summary>
    /// A synthetic old data folder with settings, caches, a ledger and its backup, and a SQLite index with one row.
    /// </summary>
    private static void WriteOldData(string folder, string tag, DateTime? written = null)
    {
        Directory.CreateDirectory(Path.Combine(folder, "cost-index"));
        var files = new[]
        {
            "settings.json", "cache.json", "usage-history.json", "cost-ledger.json", "cost-ledger.json.bak",
            "early-resets.json", "claude-cooldown.json",
            IndexRelative,
        };
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file);
            if (file == IndexRelative) WriteIndex(path, tag);
            else File.WriteAllText(path, $"{tag}:{file}");
            if (written is { } time) File.SetLastWriteTimeUtc(path, time);
        }
    }

    private static void WriteIndex(string path, string tag)
    {
        using var database = new CostIndexDatabase(path);
        database.Execute("CREATE TABLE tag (value TEXT)");
        using var insert = database.Prepare("INSERT INTO tag (value) VALUES (?)");
        insert.Bind(1, tag);
        insert.Write();
    }

    private static string ReadIndexTag(string path)
    {
        using var database = new CostIndexDatabase(path, readOnly: true);
        using var read = database.Prepare("SELECT value FROM tag");
        return read.Step() ? read.Text(0) : "";
    }

    private LegacyDataImporter Importer(IEnumerable<string>? sources = null) =>
        new(NullLogger<LegacyDataImporter>.Instance, _target,
            () => sources ?? LegacyDataImporter.DefaultSources(_localAppData),
            (source, error) => _reported.Add((source, error)));

    private string Target(string relative) => Path.Combine(_target, relative);

    [TestMethod]
    public void Run_PackagedSourceHasSettings_CopiesEveryFileAndWritesMarker()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");
        var before = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.ReadAllText(f), File.GetLastWriteTimeUtc(f)));

        var result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.Imported);
        result.SourceDirectory.Should().Be(Path.GetFullPath(source));
        result.Copied.Should().Be(8);
        result.FailedFiles.Should().BeEmpty();
        result.IndexSkipped.Should().BeFalse();
        File.ReadAllText(Target("settings.json")).Should().Be("packaged:settings.json");
        File.ReadAllText(Target("cost-ledger.json.bak")).Should().Be("packaged:cost-ledger.json.bak");
        ReadIndexTag(Target(IndexRelative)).Should().Be("packaged", "the index is copied as a snapshot");
        File.Exists(Target(Path.Combine("cost-index", "index.sqlite-wal"))).Should().BeFalse("the snapshot is complete without it");
        File.Exists(Target(Path.Combine("cost-index", "index.sqlite-shm"))).Should().BeFalse();
        File.Exists(Target(LegacyDataImporter.MarkerFileName)).Should().BeTrue();
        Directory.EnumerateFiles(_target, "*.import.tmp", SearchOption.AllDirectories).Should().BeEmpty();
        // The source is never changed or removed.
        Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.ReadAllText(f), File.GetLastWriteTimeUtc(f)))
            .Should().BeEquivalentTo(before);
        _reported.Should().BeEmpty();
    }

    [TestMethod]
    public void Run_OnlyUnpackagedSource_CopiesFromUnpackagedFolder()
    {
        WriteOldData(UnpackagedSource, "unpackaged");

        var result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.Imported);
        result.SourceDirectory.Should().Be(Path.GetFullPath(UnpackagedSource));
        File.ReadAllText(Target("settings.json")).Should().Be("unpackaged:settings.json");
        File.ReadAllText(Target("cache.json")).Should().Be("unpackaged:cache.json");
    }

    [TestMethod]
    public void Run_PackagedAndUnpackagedSources_PrefersPackaged()
    {
        WriteOldData(UnpackagedSource, "unpackaged", DateTime.UtcNow);
        WriteOldData(PackagedSource(), "packaged", DateTime.UtcNow.AddDays(-30));

        Importer().Run().Outcome.Should().Be(LegacyImportOutcome.Imported);

        File.ReadAllText(Target("settings.json")).Should().Be("packaged:settings.json");
    }

    [TestMethod]
    public void Run_PackagedFolderWithoutSettings_FallsBackToUnpackaged()
    {
        Directory.CreateDirectory(PackagedSource());
        File.WriteAllText(Path.Combine(PackagedSource(), "cache.json"), "packaged:cache.json");
        WriteOldData(UnpackagedSource, "unpackaged");

        Importer().Run().Outcome.Should().Be(LegacyImportOutcome.Imported);

        File.ReadAllText(Target("cache.json")).Should().Be("unpackaged:cache.json");
    }

    [TestMethod]
    public void DefaultSources_SeveralPackages_ListsMostRecentlyWrittenFirstThenUnpackaged()
    {
        WriteOldData(PackagedSource("CodexBarWin_older"), "older", DateTime.UtcNow.AddDays(-10));
        WriteOldData(PackagedSource("CodexBarWin_newer"), "newer", DateTime.UtcNow.AddDays(-1));
        WriteOldData(PackagedSource("CodexBarWinTest_dev"), "dev", DateTime.UtcNow);

        var sources = LegacyDataImporter.DefaultSources(_localAppData).ToList();

        sources.Should().Equal(PackagedSource("CodexBarWin_newer"), PackagedSource("CodexBarWin_older"), UnpackagedSource);
    }

    [TestMethod]
    public void Run_NoOldData_ReturnsNoSourceAndWritesMarker()
    {
        var result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.NoSource);
        File.Exists(Target("settings.json")).Should().BeFalse();
        File.Exists(Target(LegacyDataImporter.MarkerFileName)).Should().BeTrue();
        _reported.Should().BeEmpty();
    }

    [TestMethod]
    public void Run_TargetAlreadyHasSettings_CopiesNothing()
    {
        WriteOldData(PackagedSource(), "packaged");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Target("settings.json"), "current");

        var result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.TargetHasSettings);
        File.ReadAllText(Target("settings.json")).Should().Be("current");
        File.Exists(Target("cache.json")).Should().BeFalse();
    }

    [TestMethod]
    public void Run_TargetFileAlreadyPresent_IsNotOverwritten()
    {
        WriteOldData(PackagedSource(), "packaged");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Target("cache.json"), "current");

        var result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.Imported);
        result.Skipped.Should().Be(1);
        result.Copied.Should().Be(7);
        File.ReadAllText(Target("cache.json")).Should().Be("current");
    }

    [TestMethod]
    public void Run_OneFileCannotBeCopied_ReturnsPartialAndReportsIt()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");

        LegacyImportResult result;
        using (new FileStream(Path.Combine(source, "usage-history.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.Partial);
        result.FailedFiles.Should().Equal("usage-history.json");
        result.Copied.Should().Be(7);
        File.ReadAllText(Target("settings.json")).Should().Be("packaged:settings.json");
        File.Exists(Target("usage-history.json")).Should().BeFalse();
        File.Exists(Target(LegacyDataImporter.MarkerFileName)).Should().BeTrue();
        _reported.Should().ContainSingle().Which.Error.Message.Should().Contain("usage-history.json")
            .And.NotContain("packaged:");
    }

    [TestMethod]
    public void Run_SettingsCannotBeCopied_FailsWithoutMarkerAndRetriesNextStart()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");

        LegacyImportResult first;
        using (new FileStream(Path.Combine(source, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            first = Importer().Run();

        first.Outcome.Should().Be(LegacyImportOutcome.Failed);
        File.Exists(Target(LegacyDataImporter.MarkerFileName)).Should().BeFalse();
        _reported.Should().ContainSingle();

        var second = Importer().Run();

        second.Outcome.Should().Be(LegacyImportOutcome.Imported);
        second.Copied.Should().Be(1);
        second.Skipped.Should().Be(7);
        File.ReadAllText(Target("settings.json")).Should().Be("packaged:settings.json");
    }

    [TestMethod]
    public void Run_MarkerPresent_DoesNotImportAgain()
    {
        WriteOldData(PackagedSource(), "packaged");
        Importer().Run().Outcome.Should().Be(LegacyImportOutcome.Imported);
        File.Delete(Target("settings.json"));
        File.Delete(Target("cache.json"));

        var result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.AlreadyDone);
        File.Exists(Target("settings.json")).Should().BeFalse();
        File.Exists(Target("cache.json")).Should().BeFalse();
    }

    [TestMethod]
    public void Run_SourceListThrows_ReturnsFailedWithoutThrowing()
    {
        var importer = new LegacyDataImporter(NullLogger<LegacyDataImporter>.Instance, _target,
            () => throw new UnauthorizedAccessException("denied"), (source, error) => _reported.Add((source, error)));

        var run = () => importer.Run();

        run.Should().NotThrow().Which.Outcome.Should().Be(LegacyImportOutcome.Failed);
        _reported.Should().ContainSingle();
        File.Exists(Target(LegacyDataImporter.MarkerFileName)).Should().BeFalse();
    }

    [TestMethod]
    public void Run_SourceIsTheTargetFolder_IsIgnored()
    {
        Directory.CreateDirectory(_target);

        var result = Importer([_target]).Run();

        result.Outcome.Should().Be(LegacyImportOutcome.NoSource);
    }

    private JsonElement ReadMarker(string name = LegacyDataImporter.MarkerFileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Target(name)));
        return document.RootElement.Clone();
    }

    private static string[] FailedInMarker(JsonElement marker) =>
        marker.TryGetProperty("failedFiles", out var names)
            ? [.. names.EnumerateArray().Select(name => name.GetString()!)]
            : [];

    /// <summary>Runs an import while the old usage-history.json is locked, which makes it fail to copy.</summary>
    private LegacyImportResult RunWithLockedUsageHistory(string source)
    {
        using (new FileStream(Path.Combine(source, "usage-history.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            return Importer().Run();
    }

    [TestMethod]
    public void Run_OldAppHasAnOpenWriteTransaction_CopiesTheCommittedRowsOnly()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");
        using var oldApp = new CostIndexDatabase(Path.Combine(source, IndexRelative));
        oldApp.Execute("BEGIN IMMEDIATE");
        oldApp.Execute("INSERT INTO tag (value) VALUES ('uncommitted')");

        var result = Importer().Run();
        oldApp.Execute("ROLLBACK");

        result.Outcome.Should().Be(LegacyImportOutcome.Imported);
        result.IndexSkipped.Should().BeFalse();
        using var snapshot = new CostIndexDatabase(Target(IndexRelative), readOnly: true);
        using var count = snapshot.Prepare("SELECT COUNT(*) FROM tag");
        count.Step().Should().BeTrue();
        count.Number(0).Should().Be(1);
    }

    [TestMethod]
    public void Run_OldAppHasCommittedRowsInWal_IncludesThemInTheStandaloneSnapshot()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");
        var index = Path.Combine(source, IndexRelative);
        using var oldApp = new CostIndexDatabase(index);
        oldApp.Execute("PRAGMA journal_mode=WAL");
        oldApp.Execute("INSERT INTO tag (value) VALUES ('committed in WAL')");
        File.Exists(index + "-wal").Should().BeTrue();

        Importer().Run().IndexSkipped.Should().BeFalse();

        File.Exists(Target(IndexRelative) + "-wal").Should().BeFalse();
        File.Exists(Target(IndexRelative) + "-shm").Should().BeFalse();
        using var snapshot = new CostIndexDatabase(Target(IndexRelative), readOnly: true);
        using var read = snapshot.Prepare("SELECT value FROM tag ORDER BY rowid");
        read.Step().Should().BeTrue();
        read.Text(0).Should().Be("packaged");
        read.Step().Should().BeTrue();
        read.Text(0).Should().Be("committed in WAL");
    }

    [TestMethod]
    public void Run_OldIndexCannotBeSnapshotted_SkipsItAndSaysSo()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");
        File.WriteAllText(Path.Combine(source, IndexRelative), "not a database");

        var result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.Imported);
        result.IndexSkipped.Should().BeTrue("the scanner rebuilds the index from the logs");
        result.FailedFiles.Should().BeEmpty("a skipped index is not retried");
        File.Exists(Target(IndexRelative)).Should().BeFalse();
        File.ReadAllText(Target("cache.json")).Should().Be("packaged:cache.json");
        Directory.EnumerateFiles(_target, "*.import.tmp", SearchOption.AllDirectories).Should().BeEmpty();
        ReadMarker().GetProperty("indexSkipped").GetBoolean().Should().BeTrue();
        File.ReadAllText(Path.Combine(source, IndexRelative)).Should().Be("not a database", "the old folder is never changed");
    }

    [TestMethod]
    public void Run_RollbackIndexHasStaleWalSidecars_LeavesTheSourceFilesIntact()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");
        var index = Path.Combine(source, IndexRelative);
        File.WriteAllText(index + "-wal", "stale WAL");
        File.WriteAllText(index + "-shm", "stale shared memory");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Target("cache.json.import.tmp"), "existing temporary file");

        var result = Importer().Run();

        result.IndexSkipped.Should().BeTrue();
        File.Exists(Target(IndexRelative)).Should().BeFalse();
        File.Exists(Target(IndexRelative) + "-wal").Should().BeFalse();
        File.Exists(Target(IndexRelative) + "-shm").Should().BeFalse();
        File.ReadAllText(index + "-wal").Should().Be("stale WAL");
        File.ReadAllText(index + "-shm").Should().Be("stale shared memory");
        File.ReadAllText(Target("cache.json.import.tmp")).Should().Be("existing temporary file");
    }

    [TestMethod]
    public void Run_TargetIndexAlreadyExists_IsNotOverwritten()
    {
        WriteOldData(PackagedSource(), "packaged");
        Directory.CreateDirectory(Path.GetDirectoryName(Target(IndexRelative))!);
        File.WriteAllText(Target(IndexRelative), "current");

        var result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.Imported);
        result.IndexSkipped.Should().BeFalse();
        result.Skipped.Should().Be(1);
        File.ReadAllText(Target(IndexRelative)).Should().Be("current");
    }

    [TestMethod]
    public void Run_PartialImport_RecordsTheFailedNamesInTheMarker()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");

        var result = RunWithLockedUsageHistory(source);

        result.Outcome.Should().Be(LegacyImportOutcome.Partial);
        var marker = ReadMarker();
        marker.GetProperty("outcome").GetString().Should().Be("Partial");
        FailedInMarker(marker).Should().Equal("usage-history.json");
    }

    [TestMethod]
    public void Run_AfterPartialImport_RetriesOnlyTheFailedFiles()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");
        RunWithLockedUsageHistory(source).Outcome.Should().Be(LegacyImportOutcome.Partial);
        File.Delete(Target("cache.json"));
        File.WriteAllText(Target("early-resets.json"), "changed locally");
        var originalMarker = ReadMarker().GetRawText();

        var second = Importer().Run();

        second.Outcome.Should().Be(LegacyImportOutcome.Imported);
        second.Copied.Should().Be(1);
        second.FailedFiles.Should().BeEmpty();
        File.ReadAllText(Target("usage-history.json")).Should().Be("packaged:usage-history.json");
        File.Exists(Target("cache.json")).Should().BeFalse("only the failed files are retried");
        File.ReadAllText(Target("early-resets.json")).Should().Be("changed locally");
        ReadMarker().GetRawText().Should().Be(originalMarker);
        var retryMarker = ReadMarker(LegacyDataImporter.RetryMarkerPrefix + "1.json");
        retryMarker.GetProperty("outcome").GetString().Should().Be("Imported");
        FailedInMarker(retryMarker).Should().BeEmpty();
        File.Delete(Target("usage-history.json"));
        Importer().Run().Outcome.Should().Be(LegacyImportOutcome.AlreadyDone);
        File.Exists(Target("usage-history.json")).Should().BeFalse();
    }

    [TestMethod]
    public void Run_RetryStillFails_KeepsTheNameForTheNextStart()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");
        RunWithLockedUsageHistory(source);

        var second = RunWithLockedUsageHistory(source);

        second.Outcome.Should().Be(LegacyImportOutcome.Partial);
        second.FailedFiles.Should().Equal("usage-history.json");
        FailedInMarker(ReadMarker()).Should().Equal("usage-history.json");
        Importer().Run().Outcome.Should().Be(LegacyImportOutcome.Imported);
        File.Exists(Target("usage-history.json")).Should().BeTrue();
    }

    [TestMethod]
    public void Run_FailedFileIsGoneFromTheSource_IsDroppedFromTheRetry()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");
        RunWithLockedUsageHistory(source);
        File.Delete(Path.Combine(source, "usage-history.json"));

        var second = Importer().Run();

        second.Outcome.Should().Be(LegacyImportOutcome.Imported);
        second.Copied.Should().Be(0);
        File.Exists(Target("usage-history.json")).Should().BeFalse();
        FailedInMarker(ReadMarker(LegacyDataImporter.RetryMarkerPrefix + "1.json")).Should().BeEmpty();
    }

    [TestMethod]
    public void Run_PartialMarkerWithoutNames_IsTreatedAsDone()
    {
        WriteOldData(PackagedSource(), "packaged");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Target(LegacyDataImporter.MarkerFileName), """{"outcome":"Partial","copied":3,"failed":1}""");

        var result = Importer().Run();

        result.Outcome.Should().Be(LegacyImportOutcome.AlreadyDone);
        File.Exists(Target("settings.json")).Should().BeFalse();
    }

    [TestMethod]
    public void Run_MarkerNamesAPathOutsideTheDataFolder_IgnoresIt()
    {
        var source = PackagedSource();
        WriteOldData(source, "packaged");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(source)!, "outside.txt"), "outside");
        Directory.CreateDirectory(_target);
        var marker = new System.Text.StringBuilder()
            .Append("{\"outcome\":\"Partial\",\"source\":").Append(JsonSerializer.Serialize(source))
            .Append(",\"failedFiles\":[\"..\\\\outside.txt\",\"..\\\\..\\\\..\\\\..\\\\..\\\\..\\\\..\\\\x.txt\",\"C:\\\\Windows\\\\win.ini\"]}");
        File.WriteAllText(Target(LegacyDataImporter.MarkerFileName), marker.ToString());

        var result = Importer().Run();

        result.Copied.Should().Be(0);
        File.Exists(Path.Combine(_localAppData, "outside.txt")).Should().BeFalse();
        Directory.EnumerateFiles(_target).Select(Path.GetFileName).Should()
            .BeEquivalentTo([LegacyDataImporter.MarkerFileName, LegacyDataImporter.RetryMarkerPrefix + "1.json"]);
    }
}
