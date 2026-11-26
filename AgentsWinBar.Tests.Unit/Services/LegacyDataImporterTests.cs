using AwesomeAssertions;
using AgentsWinBar.Services;
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

    /// <summary>A synthetic old data folder: settings, caches, ledger and its backup, and the cost index files.</summary>
    private static void WriteOldData(string folder, string tag, DateTime? written = null)
    {
        Directory.CreateDirectory(Path.Combine(folder, "cost-index"));
        var files = new[]
        {
            "settings.json", "cache.json", "usage-history.json", "cost-ledger.json", "cost-ledger.json.bak",
            "early-resets.json", "claude-cooldown.json",
            Path.Combine("cost-index", "index.sqlite"), Path.Combine("cost-index", "index.sqlite-wal"),
            Path.Combine("cost-index", "index.sqlite-shm"),
        };
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file);
            File.WriteAllText(path, $"{tag}:{file}");
            if (written is { } time) File.SetLastWriteTimeUtc(path, time);
        }
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
        result.Copied.Should().Be(10);
        result.FailedFiles.Should().BeEmpty();
        File.ReadAllText(Target("settings.json")).Should().Be("packaged:settings.json");
        File.ReadAllText(Target("cost-ledger.json.bak")).Should().Be("packaged:cost-ledger.json.bak");
        File.ReadAllText(Target(Path.Combine("cost-index", "index.sqlite-wal")))
            .Should().Be("packaged:" + Path.Combine("cost-index", "index.sqlite-wal"));
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
        result.Copied.Should().Be(9);
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
        result.Copied.Should().Be(9);
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
        second.Skipped.Should().Be(9);
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
}
