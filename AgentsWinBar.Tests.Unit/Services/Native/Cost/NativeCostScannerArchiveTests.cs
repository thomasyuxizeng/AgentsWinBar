using AwesomeAssertions;
using AgentsWinBar.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentsWinBar.Tests.Unit.Services.Native.Cost.CostTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native.Cost;

/// <summary>Compressed Codex logs (.jsonl.zst) and updates limited to changed paths.</summary>
[TestClass]
public sealed class NativeCostScannerArchiveTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private string Claude => Path.Combine(_directory, "claude");
    private string Codex => Path.Combine(_directory, "codex");
    private CostLogRoot[] Roots => [new(CostProvider.Claude, Claude), new(CostProvider.Codex, Codex)];

    [TestInitialize]
    public void Initialize()
    {
        _directory = TempDirectory("cost-archive");
        Directory.CreateDirectory(Claude);
        Directory.CreateDirectory(Codex);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private NativeCostScanner Scanner() =>
        new(Path.Combine(_directory, "index"), NullLogger<NativeCostScanner>.Instance, TimeZoneInfo.Utc);

    private static long Tokens(NativeCostScanner scanner, CostProvider provider = CostProvider.Codex) =>
        scanner.ReadReport(provider, asOf: Today).Last30DaysTokens ?? 0;

    // Without a session_meta record the rows are keyed by file position, the case a second count would not dedupe.
    private static string Log(params int[] requests)
    {
        var lines = new List<string> { CodexContext() };
        var total = 0;
        foreach (var request in requests)
        {
            total += request;
            lines.Add(CodexCount(total, request));
        }
        return Lines([.. lines]);
    }

    [TestMethod]
    public void Update_WhenCodexLogIsCompressed_IndexesItsUsage()
    {
        File.WriteAllBytes(Path.Combine(Codex, "rollout-a.jsonl.zst"), Compress(Log(100, 50)));
        var scanner = Scanner();

        var result = scanner.Update(Roots);

        result.FilesRead.Should().Be(1);
        result.UnavailableFiles.Should().Be(0);
        Tokens(scanner).Should().Be(150);
    }

    [TestMethod]
    public void Update_WhenIndexedLogIsLaterCompressed_DoesNotCountItTwice()
    {
        var plain = Path.Combine(Codex, "rollout-a.jsonl");
        var text = Log(100, 50);
        File.WriteAllText(plain, text);
        var scanner = Scanner();
        scanner.Update(Roots);

        File.Delete(plain);
        File.WriteAllBytes(plain + ".zst", Compress(text));
        scanner.Update(Roots);
        var again = scanner.Update(Roots);

        Tokens(scanner).Should().Be(150);
        again.FilesRead.Should().Be(0, "an unchanged archive is not opened again");
    }

    [TestMethod]
    public void Update_WhenLogGrewBeforeItWasCompressed_CountsOnlyTheNewRequests()
    {
        var plain = Path.Combine(Codex, "rollout-a.jsonl");
        File.WriteAllText(plain, Log(100, 50));
        var scanner = Scanner();
        scanner.Update(Roots);

        File.Delete(plain);
        File.WriteAllBytes(plain + ".zst", Compress(Log(100, 50, 25)));
        scanner.Update(Roots);

        Tokens(scanner).Should().Be(175);
    }

    [TestMethod]
    public void Update_WhenPlainLogAndArchiveBothExist_ReadsOnlyThePlainLog()
    {
        var plain = Path.Combine(Codex, "rollout-a.jsonl");
        File.WriteAllText(plain, Log(100));
        File.WriteAllBytes(plain + ".zst", Compress(Log(100, 900)));
        var scanner = Scanner();

        var result = scanner.Update(Roots);

        result.DiscoveredFiles.Should().Be(1);
        Tokens(scanner).Should().Be(100);
    }

    [TestMethod]
    public void Update_WhenArchiveIsShorterThanTheIndexedLog_IndexesItFromTheStart()
    {
        var plain = Path.Combine(Codex, "rollout-a.jsonl");
        File.WriteAllText(plain, Log(100, 50, 25));
        var scanner = Scanner();
        scanner.Update(Roots);

        File.Delete(plain);
        File.WriteAllBytes(plain + ".zst", Compress(Log(40)));
        scanner.Update(Roots);

        Tokens(scanner).Should().Be(40);
    }

    [TestMethod]
    public void Update_WhenArchiveIsCorrupt_SkipsItAndKeepsOtherFiles()
    {
        File.WriteAllBytes(Path.Combine(Codex, "rollout-bad.jsonl.zst"), [0x28, 0xB5, 0x2F, 0xFD, 0xFF, 0x01, 0x02, 0x03]);
        File.WriteAllText(Path.Combine(Codex, "rollout-good.jsonl"), Log(70));
        var scanner = Scanner();

        var result = scanner.Update(Roots);

        result.UnavailableFiles.Should().Be(1);
        Tokens(scanner).Should().Be(70);
    }

    [TestMethod]
    public void Update_WhenClaudeFolderHoldsACompressedFile_IgnoresIt()
    {
        File.WriteAllBytes(Path.Combine(Claude, "session.jsonl.zst"), Compress(Lines(ClaudeLine("1", 500))));
        File.WriteAllText(Path.Combine(Claude, "session-2.jsonl"), Lines(ClaudeLine("2", 30)));
        var scanner = Scanner();

        var result = scanner.Update(Roots);

        result.DiscoveredFiles.Should().Be(1);
        Tokens(scanner, CostProvider.Claude).Should().Be(30);
    }

    [TestMethod]
    public void Update_WhenArchiveHoldsKeyedSessionAlreadyIndexedFromAnotherFolder_CountsItOnce()
    {
        // A session moved to archived_sessions keeps its thread id, so its requests keep their keys.
        var text = Lines(CodexSession, CodexContext(), CodexCount(100, 100), CodexCount(160, 60, "2026-11-25T09:05:00Z"));
        File.WriteAllText(Path.Combine(Codex, "rollout-a.jsonl"), text);
        var archived = Path.Combine(_directory, "archived");
        Directory.CreateDirectory(archived);
        File.WriteAllBytes(Path.Combine(archived, "rollout-a.jsonl.zst"), Compress(text));
        var scanner = Scanner();

        scanner.Update([.. Roots, new(CostProvider.Codex, archived)]);

        Tokens(scanner).Should().Be(160);
    }

    [TestMethod]
    public void Update_WhenChangedPathsIsEmpty_DoesNothing()
    {
        File.WriteAllText(Path.Combine(Codex, "rollout-a.jsonl"), Log(100));
        var scanner = Scanner();

        var result = scanner.Update(Roots, changedPaths: []);

        result.Should().Be(new CostScanResult(0, 0, 0, 0, 0, 0, 0));
        Directory.Exists(Path.Combine(_directory, "index")).Should().BeFalse("no index is opened when nothing changed");
    }

    [TestMethod]
    public void Update_WhenChangedPathsNamesOneFile_ReadsOnlyThatFile()
    {
        var first = Path.Combine(Codex, "rollout-a.jsonl");
        var second = Path.Combine(Codex, "rollout-b.jsonl");
        File.WriteAllText(first, Log(100));
        File.WriteAllText(second, Log(10));
        var scanner = Scanner();
        scanner.Update(Roots);
        File.AppendAllText(first, CodexCount(150, 50) + "\n");
        File.AppendAllText(second, CodexCount(15, 5) + "\n");

        var result = scanner.Update(Roots, changedPaths: [first.ToUpperInvariant()]);

        result.DiscoveredFiles.Should().Be(1);
        result.FilesRead.Should().Be(1);
        Tokens(scanner).Should().Be(160, "the other file waits for its own notification or the next listing");

        scanner.Update(Roots);
        Tokens(scanner).Should().Be(165);
    }

    [TestMethod]
    public void Update_WhenChangedPathIsADirectory_ListsThatDirectory()
    {
        var moved = Path.Combine(Codex, "2026", "11");
        Directory.CreateDirectory(moved);
        File.WriteAllText(Path.Combine(moved, "rollout-a.jsonl"), Log(100));
        File.WriteAllBytes(Path.Combine(moved, "rollout-b.jsonl.zst"), Compress(Log(20)));
        File.WriteAllText(Path.Combine(Codex, "rollout-elsewhere.jsonl"), Log(7));
        var scanner = Scanner();

        var result = scanner.Update(Roots, changedPaths: [Path.Combine(Codex, "2026")]);

        result.FilesRead.Should().Be(2);
        Tokens(scanner).Should().Be(120);
    }

    [TestMethod]
    public void Update_WhenChangedPathIsOutsideTheRootsOrGone_IgnoresIt()
    {
        var outside = Path.Combine(_directory, "elsewhere.jsonl");
        File.WriteAllText(outside, Log(100));
        var scanner = Scanner();

        var result = scanner.Update(Roots, changedPaths:
            [outside, Path.Combine(Codex, "deleted.jsonl"), Path.Combine(Codex, "notes.txt"), Codex + "-sibling\\x.jsonl"]);

        result.DiscoveredFiles.Should().Be(0);
        Tokens(scanner).Should().Be(0);
    }

    [TestMethod]
    public void Update_WhenArchiveIsNamedInChangedPaths_ReplacesThePlainLogEntry()
    {
        var plain = Path.Combine(Codex, "rollout-a.jsonl");
        var text = Log(100, 50);
        File.WriteAllText(plain, text);
        var scanner = Scanner();
        scanner.Update(Roots);
        File.Delete(plain);
        File.WriteAllBytes(plain + ".zst", Compress(text));

        scanner.Update(Roots, changedPaths: [plain + ".zst"]);
        var listing = scanner.Update(Roots);

        Tokens(scanner).Should().Be(150);
        listing.FilesRead.Should().Be(0);
    }

    // Review finding 12: the plain log wins over its archive also when only the archive is named in a notification.
    [TestMethod]
    public void Update_WhenChangedPathsNamesArchiveWhilePlainLogExists_PrefersPlainLog()
    {
        var plain = Path.Combine(Codex, "rollout-a.jsonl");
        File.WriteAllText(plain, Log(100));
        var scanner = Scanner();
        scanner.Update(Roots);
        File.WriteAllBytes(plain + ".zst", Compress(Log(40)));

        scanner.Update(Roots, changedPaths: [plain + ".zst"]);
        var listing = scanner.Update(Roots);

        Tokens(scanner).Should().Be(100);
        listing.FilesRead.Should().Be(0);
    }

    // Review finding 12: an archive replaced by a different one is not resumed mid-line with the old cursor.
    [TestMethod]
    public void Update_WhenArchiveIsReplacedByDifferentArchive_IndexesItFromTheStart()
    {
        var archive = Path.Combine(Codex, "rollout-a.jsonl.zst");
        File.WriteAllBytes(archive, Compress(Log(100, 50)));
        var scanner = Scanner();
        scanner.Update(Roots);
        var replacement = Lines(CodexContext("gpt-6.2-sol-with-a-much-longer-model-name-than-before"),
            CodexCount(7, 7), CodexCount(9, 2), CodexCount(30, 21), CodexCount(41, 11));
        File.WriteAllBytes(archive, Compress(replacement));

        var result = scanner.Update(Roots);

        Tokens(scanner).Should().Be(41);
        result.MalformedLines.Should().Be(0);
    }

    // Review finding 12: decompression stops at a cap, so a damaged or hostile archive cannot run away.
    [TestMethod]
    public void Update_WhenArchiveDecompressesBeyondCap_CountsOnlyLinesWithinTheCap()
    {
        var text = Log(100, 50, 25);
        var lines = text.Split('\n');
        var withinCap = System.Text.Encoding.UTF8.GetByteCount(lines[0] + "\n" + lines[1] + "\n") + 5;
        File.WriteAllBytes(Path.Combine(Codex, "rollout-a.jsonl.zst"), Compress(text));
        var scanner = new NativeCostScanner(Path.Combine(_directory, "index"), NullLogger<NativeCostScanner>.Instance, TimeZoneInfo.Utc)
        {
            MaxArchiveBytes = withinCap
        };

        scanner.Update(Roots);

        Tokens(scanner).Should().Be(100);
    }

    // Review finding 19: notifications about files that are not logs do not open the index.
    [TestMethod]
    public void Update_WhenChangedPathsContainsOnlyNonLogFiles_DoesNotOpenIndex()
    {
        var notes = Path.Combine(Claude, "notes.md");
        var temp = Path.Combine(Codex, "temp.tmp");
        File.WriteAllText(notes, "synthetic");
        File.WriteAllText(temp, "synthetic");
        var scanner = Scanner();

        var result = scanner.Update(Roots, changedPaths: [notes, temp, Path.Combine(Codex, "gone.jsonl")]);

        result.Should().Be(new CostScanResult(0, 0, 0, 0, 0, 0, 0));
        Directory.Exists(Path.Combine(_directory, "index")).Should().BeFalse();
    }

    // Review finding 20.4: a changed path is normalized before the root check.
    [TestMethod]
    public void Update_WhenChangedPathUsesParentTraversal_IgnoresFileOutsideRoot()
    {
        File.WriteAllText(Path.Combine(_directory, "elsewhere.jsonl"), Log(100));
        var scanner = Scanner();
        scanner.Update(Roots);

        var result = scanner.Update(Roots, changedPaths: [Path.Combine(Codex, "..", "elsewhere.jsonl")]);

        result.DiscoveredFiles.Should().Be(0);
        Tokens(scanner).Should().Be(0);
    }

    [TestMethod]
    public void ReadReport_WhenModelHasNoPrice_ReportsItsTokensAsUnpriced()
    {
        File.WriteAllText(Path.Combine(Claude, "session.jsonl"), Lines(
            ClaudeLine("1", 1_000, "claude-sonnet-5-5"),
            ClaudeLine("2", 400, "vendor/unlisted-model:free"),
            ClaudeLine("3", 50, "vendor/unlisted-model:free", "2026-11-24T09:00:00Z")));
        var scanner = Scanner();
        scanner.Update(Roots);

        var report = scanner.ReadReport(CostProvider.Claude, asOf: Today);

        report.UnpricedTokens.Should().Be(450);
        report.Last30DaysTokens.Should().Be(1_450);
        report.Last30DaysCostUSD.Should().BePositive();
        report.Daily!.Single(day => day.Date == new DateOnly(2026, 11, 25)).UnpricedTokens.Should().Be(400);
        report.Daily!.Single(day => day.Date == new DateOnly(2026, 11, 24)).UnpricedTokens.Should().Be(50);
        var unlisted = report.Daily!.SelectMany(day => day.ModelBreakdowns).Where(model => model.ModelName == "vendor/unlisted-model:free");
        unlisted.Should().OnlyContain(model => model.Cost == null);
        report.Coverage!.Unpriced.Should().Be(2);
    }

    [TestMethod]
    public void ReadReport_WhenEveryModelIsPriced_ReportsNoUnpricedTokens()
    {
        File.WriteAllText(Path.Combine(Codex, "rollout-a.jsonl"), Log(100));
        var scanner = Scanner();
        scanner.Update(Roots);

        var report = scanner.ReadReport(CostProvider.Codex, asOf: Today);

        report.UnpricedTokens.Should().Be(0);
        report.Daily!.Should().OnlyContain(day => day.UnpricedTokens == 0);
    }
}
