using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services.Native;
using CodexBarWin.Services.Native.Cost;
using CodexBarWin.Tests.Unit.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;
using static CodexBarWin.Tests.Unit.Services.Native.Cost.CostTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native;

[TestClass]
public sealed class NativeCostServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private string Claude => Path.Combine(_directory, "claude");
    private string Codex => Path.Combine(_directory, "codex");
    private string Archived => Path.Combine(_directory, "archived");
    private string Data => Path.Combine(_directory, "data");

    [TestInitialize]
    public void Initialize()
    {
        _directory = TempDirectory("cost-service");
        Directory.CreateDirectory(Claude);
        Directory.CreateDirectory(Codex);
        Directory.CreateDirectory(Data);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private NativeCostService Service(bool watch = false) => new(Data,
        [new(CostProvider.Claude, Claude), new(CostProvider.Codex, Codex), new(CostProvider.Codex, Archived)],
        NullLoggerFactory.Instance, new ManualClock(Now), TimeZoneInfo.Utc, watch, refreshPrices: false);

    private static CostDataDto Read(string? json) =>
        JsonSerializer.Deserialize(json!, AppJsonSerializerContext.Default.CostDataDto)!;

    [TestMethod]
    public async Task FetchCostJsonAsync_WhenLogsExist_ReturnsCliShapedReportsPerProvider()
    {
        File.WriteAllText(Path.Combine(Claude, "session.jsonl"), Lines(ClaudeLine("1", 1_000_000)));
        File.WriteAllText(Path.Combine(Codex, "rollout-a.jsonl"), Lines(CodexSession, CodexContext(), CodexCount(2_000_000, 2_000_000)));
        Directory.CreateDirectory(Archived);
        File.WriteAllBytes(Path.Combine(Archived, "rollout-old.jsonl.zst"),
            Compress(Lines(CodexContext(), CodexCount(500_000, 500_000, "2026-11-20T09:00:00Z"))));
        using var service = Service();

        var reports = await service.FetchCostJsonAsync(["Claude", "codex"], Now);

        var claude = Read(reports["claude"]);
        claude.Provider.Should().Be("claude");
        claude.HasReading.Should().BeTrue();
        claude.SessionTokens.Should().Be(1_000_000);
        claude.SessionCostUSD.Should().Be(new CostPricing().GetRates(CostProvider.Claude, "claude-sonnet-5-5", false, false, false, 0)!.Value.Input);
        claude.UnpricedTokens.Should().Be(0);
        var codex = Read(reports["codex"]);
        codex.Provider.Should().Be("codex");
        codex.SessionTokens.Should().Be(2_000_000);
        codex.Last30DaysTokens.Should().Be(2_500_000, "the compressed archived session counts too");
        codex.Daily.Should().HaveCount(2);
        File.Exists(Path.Combine(Data, NativeCostService.IndexFolderName, "index.sqlite")).Should().BeTrue();
    }

    [TestMethod]
    public async Task FetchCostJsonAsync_WhenReportIsMappedToCostData_CarriesUnpricedTokens()
    {
        File.WriteAllText(Path.Combine(Claude, "session.jsonl"),
            Lines(ClaudeLine("1", 100), ClaudeLine("2", 40, "vendor/unlisted-model")));
        using var service = Service();

        var reports = await service.FetchCostJsonAsync(["claude"], Now);
        var data = Read(reports["claude"]).ToCostData("claude", Now.UtcDateTime);

        data.UnpricedTokens.Should().Be(40);
        data.TodayTokens.Should().Be(140);
        data.Daily.Single().UnpricedTokens.Should().Be(40);
        data.FetchOutcome.Should().Be(FetchOutcome.Success);
    }

    [TestMethod]
    public async Task FetchCostJsonAsync_WhenProviderIsGemini_ReturnsTheCliEmptyReading()
    {
        using var service = Service();

        var reports = await service.FetchCostJsonAsync(["gemini"], Now);

        var gemini = Read(reports["gemini"]);
        gemini.Provider.Should().Be("antigravity");
        gemini.HasReading.Should().BeTrue();
        gemini.SessionTokens.Should().Be(0);
        gemini.Daily.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchCostJsonAsync_WhenNoLogFolderExists_ReturnsZeroReadings()
    {
        Directory.Delete(Claude);
        Directory.Delete(Codex);
        using var service = Service(watch: true);

        var reports = await service.FetchCostJsonAsync(["claude", "codex"], Now);

        Read(reports["claude"]).Last30DaysTokens.Should().Be(0);
        Read(reports["codex"]).HasReading.Should().BeTrue();
    }

    [TestMethod]
    public async Task FetchCostJsonAsync_WhenProviderIsNotAllowed_Throws()
    {
        using var service = Service();

        var act = () => service.FetchCostJsonAsync(["claude; rm -rf /"], Now);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task FetchCostJsonAsync_WhenCanceled_ThrowsAndTheNextCallStillLists()
    {
        File.WriteAllText(Path.Combine(Claude, "session.jsonl"), Lines(ClaudeLine("1", 100)));
        using var service = Service();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var act = () => service.FetchCostJsonAsync(["claude"], Now, canceled.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        Read((await service.FetchCostJsonAsync(["claude"], Now))["claude"]).SessionTokens.Should().Be(100);
    }

    [TestMethod]
    public async Task FetchCostJsonAsync_WhenLogChangesWithoutANotification_WaitsForTheNextListing()
    {
        // Watching is off here, so after the startup listing only the safety listing can see a change.
        var log = Path.Combine(Claude, "session.jsonl");
        File.WriteAllText(log, Lines(ClaudeLine("1", 100)));
        var clock = new ManualClock(Now);
        using var service = new NativeCostService(Data, [new(CostProvider.Claude, Claude)], NullLoggerFactory.Instance,
            clock, TimeZoneInfo.Utc, watch: false, refreshPrices: false);
        await service.FetchCostJsonAsync(["claude"], Now);
        File.AppendAllText(log, Lines(ClaudeLine("2", 50)));

        var unchanged = Read((await service.FetchCostJsonAsync(["claude"], Now))["claude"]);
        clock.Now = Now + CostLogWatcher.FullListingInterval;
        var listed = Read((await service.FetchCostJsonAsync(["claude"], Now))["claude"]);

        unchanged.SessionTokens.Should().Be(100);
        listed.SessionTokens.Should().Be(150);
    }

    [TestMethod]
    public async Task FetchCostJsonAsync_WhenWatchedLogIsAppended_NextReportIncludesIt()
    {
        var log = Path.Combine(Claude, "session.jsonl");
        File.WriteAllText(log, Lines(ClaudeLine("1", 100)));
        using var service = Service(watch: true);
        await service.FetchCostJsonAsync(["claude"], Now);

        File.AppendAllText(log, Lines(ClaudeLine("2", 50)));
        long? tokens = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (tokens != 150 && timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            tokens = Read((await service.FetchCostJsonAsync(["claude"], Now))["claude"]).SessionTokens;
            if (tokens != 150) await Task.Delay(50);
        }

        tokens.Should().Be(150);
    }

    // Review finding 3: a changed log that could not be read is retried at the next scan, not six hours later.
    [TestMethod]
    public async Task FetchCostJsonAsync_WhenWatchedFileIsTemporarilyLocked_RetriesItOnNextScan()
    {
        var log = Path.Combine(Claude, "session.jsonl");
        File.WriteAllText(log, Lines(ClaudeLine("1", 100)));
        using var service = Service();
        await service.FetchCostJsonAsync(["claude"], Now);

        long? whileLocked;
        using (var writer = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            writer.Write(System.Text.Encoding.UTF8.GetBytes(Lines(ClaudeLine("2", 50))));
            writer.Flush();
            service.Watcher.NotifyChanged(CostProvider.Claude, log, created: false);
            whileLocked = Read((await service.FetchCostJsonAsync(["claude"], Now))["claude"]).SessionTokens;
        }
        var afterUnlock = Read((await service.FetchCostJsonAsync(["claude"], Now))["claude"]).SessionTokens;

        whileLocked.Should().Be(100);
        afterUnlock.Should().Be(150, "the skipped file is queued again without a new write");
    }

    [TestMethod]
    public async Task FetchCostJsonAsync_WhenChangedFileStaysUnreadable_StopsRetryingAfterAFewScans()
    {
        var log = Path.Combine(Claude, "session.jsonl");
        File.WriteAllText(log, Lines(ClaudeLine("1", 100)));
        using var service = Service();
        await service.FetchCostJsonAsync(["claude"], Now);

        using var writer = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.None);
        writer.Write(System.Text.Encoding.UTF8.GetBytes(Lines(ClaudeLine("2", 50))));
        writer.Flush();
        service.Watcher.NotifyChanged(CostProvider.Claude, log, created: false);
        var pending = new List<int>();
        for (var scan = 0; scan < 6; scan++)
        {
            await service.FetchCostJsonAsync(["claude"], Now);
            pending.Add(service.Watcher.PendingPaths.Count);
        }

        pending[0].Should().Be(1, "the first failure is retried");
        pending[^1].Should().Be(0, "a file that never becomes readable is left to the periodic listing");
    }

    // Review finding 4: a deleted or damaged index is rebuilt at once, not after the six-hour listing.
    [TestMethod]
    [DataRow("deleted")]
    [DataRow("corrupt")]
    public async Task FetchCostJsonAsync_WhenIndexIsDeletedOrCorruptedBetweenScans_RebuildsImmediately(string damage)
    {
        File.WriteAllText(Path.Combine(Claude, "session.jsonl"), Lines(ClaudeLine("1", 100)));
        using var service = Service();
        await service.FetchCostJsonAsync(["claude"], Now);
        var index = Path.Combine(Data, NativeCostService.IndexFolderName, "index.sqlite");
        if (damage == "deleted") File.Delete(index);
        else File.WriteAllText(index, "not sqlite");

        var report = Read((await service.FetchCostJsonAsync(["claude"], Now))["claude"]);

        report.SessionTokens.Should().Be(100);
    }

    [TestMethod]
    public void DefaultRoots_WhenGivenAProfile_CoversClaudeProjectsAndBothCodexFolders()
    {
        var roots = NativeCostService.DefaultRoots(@"C:\Users\someone");

        roots.Should().Equal(
            new CostLogRoot(CostProvider.Claude, @"C:\Users\someone\.claude\projects"),
            new CostLogRoot(CostProvider.Codex, @"C:\Users\someone\.codex\sessions"),
            new CostLogRoot(CostProvider.Codex, @"C:\Users\someone\.codex\archived_sessions"));
    }
}
