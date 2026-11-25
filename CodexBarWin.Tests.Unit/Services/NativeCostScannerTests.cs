using System.Runtime.InteropServices;
using System.Text;
using AwesomeAssertions;
using CodexBarWin.Services.Native.Cost;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public sealed partial class NativeCostScannerTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "cost-test-" + Guid.NewGuid());
    private string Claude => Path.Combine(_directory, "claude");
    private string Codex => Path.Combine(_directory, "codex");
    private string IndexDirectory => Path.Combine(_directory, "index");
    private string IndexPath => Path.Combine(IndexDirectory, "index.sqlite");

    [TestInitialize]
    public void Initialize()
    {
        Directory.CreateDirectory(Claude);
        Directory.CreateDirectory(Codex);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private NativeCostScanner Scanner(TimeZoneInfo? zone = null, ILogger<NativeCostScanner>? logger = null) =>
        new(IndexDirectory, logger ?? Mock.Of<ILogger<NativeCostScanner>>(), zone ?? TimeZoneInfo.Utc);

    [TestMethod]
    public void Update_WhenLineIsPartial_OnlyCommitsAfterNewline()
    {
        var scanner = Scanner();
        var path = Path.Combine(Claude, "demo.jsonl");
        var first = ClaudeLine("1", 10);
        var next = ClaudeLine("2", 20);
        var cut = next.Length / 2;
        File.WriteAllText(path, first + "\n" + next[..cut]);
        scanner.Update(Claude, Codex).FilesRead.Should().Be(1);
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(10);
        scanner.Update(Claude, Codex).BytesRead.Should().Be(0);

        File.AppendAllText(path, next[cut..] + "\n");
        scanner = Scanner();
        scanner.Update(Claude, Codex).BytesRead.Should().Be(Encoding.UTF8.GetByteCount(next + "\n"));
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(30);
    }

    [TestMethod]
    public void Update_WhenCompleteLinesAreAppended_ReadsOnlyAppendedBytes()
    {
        var scanner = Scanner();
        var path = Path.Combine(Claude, "demo.jsonl");
        File.WriteAllText(path, ClaudeLine("1", 1) + "\n");
        scanner.Update(Claude, Codex);
        var append = ClaudeLine("2", 2) + "\r\n";
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                writer.Write(Encoding.UTF8.GetBytes(append));
            scanner.Update(Claude, Codex).BytesRead.Should().Be(Encoding.UTF8.GetByteCount(append));
        }
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(3);
        scanner.Update(Claude, Codex).FilesRead.Should().Be(0);
    }

    [TestMethod]
    public void Update_WhenFileTruncatesOrRotates_IndexesNewRowsAndRetainsHistory()
    {
        var scanner = Scanner();
        var path = Path.Combine(Claude, "demo.jsonl");
        File.WriteAllText(path, ClaudeLine("1", 10) + "\n" + ClaudeLine("2", 20) + "\n");
        scanner.Update(Claude, Codex);

        File.WriteAllText(path, ClaudeLine("3", 30) + "\n");
        scanner.Update(Claude, Codex).BytesRead.Should().Be(new FileInfo(path).Length);
        File.Move(path, Path.Combine(Claude, "demo.old"));
        File.WriteAllText(path, ClaudeLine("3", 30) + "\n" + ClaudeLine("4", 40) + "\n");
        scanner.Update(Claude, Codex).BytesRead.Should().Be(new FileInfo(path).Length);
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(100);
    }

    [TestMethod]
    public void Update_WhenClaudeChunksRepeatAcrossFiles_KeepsCompleteCanonicalUsage()
    {
        var scanner = Scanner();
        var parent = Path.Combine(Claude, "parent.jsonl");
        var subagents = Directory.CreateDirectory(Path.Combine(Claude, "subagents")).FullName;
        File.WriteAllText(Path.Combine(subagents, "mirror.jsonl"), ClaudeLine("1", 100) + "\n");
        File.WriteAllText(parent, ClaudeLine("1", 10) + "\n" + ClaudeLine("1", 20) + "\n");
        scanner.Update(Claude, Codex, workers: 2);
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(20);

        var final = ClaudeLine("1", 200).Replace("\"usage\":", "\"stop_reason\":null,\"usage\":");
        File.AppendAllText(parent, final + "\n");
        scanner.Update(Claude, Codex);
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(20);
    }

    [TestMethod]
    public void Update_WhenCodexRequestHasLegacyMirror_CountsOnlyTheRequestAcrossScans()
    {
        var scanner = Scanner();
        var path = Path.Combine(Codex, "demo.jsonl");
        File.WriteAllText(path, """
            {"type":"session_meta","payload":{"id":"thread-1"}}
            {"type":"turn_context","payload":{"model":"gpt-6.1-sol"}}
            {"type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"priority"}}}
            {"type":"token_usage_record","timestamp":"2026-11-25T09:00:00Z","payload":{"thread_id":"thread-1","response_id":"response-1","usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":10,"reasoning_output_tokens":2},"thread_token_usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":10,"reasoning_output_tokens":2}}}
            """ + "\n");
        scanner.Update(Claude, Codex);
        File.AppendAllText(path, """
            {"type":"event_msg","timestamp":"2026-11-25T09:00:01Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":10},"last_token_usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":10}}}}
            """ + "\n");
        Scanner().Update(Claude, Codex);
        var day = scanner.ReadReport(CostProvider.Codex, asOf: Today).Daily!.Should().ContainSingle().Subject;
        day.TotalTokens.Should().Be(110);
        day.InputTokens.Should().Be(100);
        day.CacheReadTokens.Should().Be(40);
        day.ReasoningTokens.Should().Be(2);
        day.TotalCost.Should().Be(.000448m);
    }

    [TestMethod]
    public void ReadReport_WhenTimestampsStraddleLocalMidnight_PutsUsageOnSeparateDays()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(9), "test", "test");
        var scanner = Scanner(zone);
        File.WriteAllText(Path.Combine(Claude, "demo.jsonl"),
            ClaudeLine("1", 10, "2026-11-24T14:59:59Z") + "\n" +
            ClaudeLine("2", 20, "2026-11-24T15:00:00Z") + "\n");
        scanner.Update(Claude, Codex);

        var daily = scanner.ReadReport(CostProvider.Claude, asOf: Today).Daily!;
        daily.Select(d => d.Date).Should().Equal(new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 25));
        daily.Select(d => d.TotalTokens).Should().Equal(10, 20);
    }

    [TestMethod]
    public void ReadReport_WhenRatesChange_RepricesWithoutAccessToLogs()
    {
        var scanner = Scanner();
        var path = Path.Combine(Claude, "demo.jsonl");
        File.WriteAllText(path, ClaudeLine("1", 100) + "\n");
        scanner.Update(Claude, Codex);
        var original = scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionCostUSD;
        File.Delete(path);

        var repriced = Scanner().ReadReport(CostProvider.Claude, new DoubleRates(), Today);
        original.Should().Be(.0003m);
        repriced.SessionCostUSD.Should().Be(original * 2);
        repriced.SessionTokens.Should().Be(100);
    }

    [TestMethod]
    public void Update_WhenCancelled_LeavesPreviouslyCommittedRowsReadable()
    {
        var scanner = Scanner();
        var path = Path.Combine(Claude, "demo.jsonl");
        File.WriteAllText(path, ClaudeLine("1", 10) + "\n");
        scanner.Update(Claude, Codex);
        File.AppendAllText(path, ClaudeLine("2", 20) + "\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => scanner.Update(Claude, Codex, cancellationToken: cancellation.Token);
        action.Should().Throw<OperationCanceledException>();
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(10);
        scanner.Update(Claude, Codex);
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(30);
    }

    [TestMethod]
    public void Update_WhenZoneChanges_RebucketsStoredUsageWithoutReadingLogs()
    {
        var korea = Scanner(TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"));
        var australia = Scanner(TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time"));
        var path = Path.Combine(Claude, "travel.jsonl");
        File.WriteAllText(path, ClaudeLine("1", 10, "2026-11-24T12:59:59Z") + "\n" +
            ClaudeLine("2", 20, "2026-11-24T13:00:00Z") + "\n");
        korea.Update(Claude, Codex);
        korea.ReadReport(CostProvider.Claude, asOf: Today).Daily.Should().ContainSingle()
            .Which.TotalTokens.Should().Be(30);
        File.Delete(path);

        var scan = australia.Update(Claude, Codex);
        scan.FilesRead.Should().Be(0);
        scan.BytesRead.Should().Be(0);
        var daily = australia.ReadReport(CostProvider.Claude, asOf: Today).Daily!;
        daily.Select(day => day.Date).Should().Equal(new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 25));
        daily.Select(day => day.TotalTokens).Should().Equal(10, 20);

        korea.Update(Claude, Codex).BytesRead.Should().Be(0);
        korea.ReadReport(CostProvider.Claude, asOf: Today).Daily.Should().ContainSingle()
            .Which.TotalTokens.Should().Be(30);
    }

    [TestMethod]
    [DataRow("old version")]
    [DataRow("missing trigger")]
    [DataRow("missing timestamp column")]
    [DataRow("corrupt file")]
    public void Update_WhenIndexIsInvalid_RebuildsFromLogs(string damage)
    {
        var scanner = Scanner();
        var path = Path.Combine(Claude, "fixture.jsonl");
        File.WriteAllText(path, ClaudeLine("1", 10) + "\n");
        scanner.Update(Claude, Codex);
        if (damage == "corrupt file")
            File.WriteAllText(IndexPath, "This fixture is not a SQLite database.");
        else
        {
            using var database = new CostIndexDatabase(IndexPath);
            database.Execute(damage switch
            {
                "old version" => "PRAGMA user_version=1",
                "missing trigger" => "DROP TRIGGER usage_delete",
                "missing timestamp column" => "ALTER TABLE usage RENAME COLUMN utc_seconds TO old_utc_seconds",
                _ => throw new ArgumentOutOfRangeException(nameof(damage))
            });
        }

        scanner.Update(Claude, Codex).BytesRead.Should().Be(new FileInfo(path).Length);
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(10);
        scanner.Update(Claude, Codex).FilesRead.Should().Be(0);
    }

    [TestMethod]
    public void ReadReport_WhenIndexIsMissing_ReturnsEmptyWithoutCreatingDirectory()
    {
        var report = Scanner().ReadReport(CostProvider.Claude, asOf: Today);

        report.Daily.Should().BeEmpty();
        report.SessionTokens.Should().Be(0);
        Directory.Exists(IndexDirectory).Should().BeFalse();
    }

    [TestMethod]
    public void ReadReport_WhenWriterHasUncommittedChanges_ReturnsCommittedRows()
    {
        var scanner = Scanner();
        File.WriteAllText(Path.Combine(Claude, "fixture.jsonl"), ClaudeLine("1", 10) + "\n");
        scanner.Update(Claude, Codex);
        using var writer = new CostIndexDatabase(IndexPath);
        writer.Execute("BEGIN IMMEDIATE; DELETE FROM days");

        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(10);

        writer.Execute("ROLLBACK");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Update_WhenUnkeyedFileIsReset_ReplacesOffsetUsageAndKeepsKeyedUsage(bool replace)
    {
        var scanner = Scanner();
        var path = Path.Combine(Claude, "rewritten.jsonl");
        var removed = Path.Combine(Claude, "removed.jsonl");
        var unkeyed = """
            {"type":"assistant","timestamp":"2026-11-25T09:00:00Z","message":{"model":"claude-sonnet-4-6","usage":{"input_tokens":10}}}
            """ + "\n";
        var original = unkeyed + ClaudeLine("keyed", 20) + "\n";
        File.WriteAllText(path, original);
        File.WriteAllText(removed, unkeyed);
        scanner.Update(Claude, Codex);
        var before = scanner.ReadReport(CostProvider.Claude, asOf: Today);
        before.SessionTokens.Should().Be(40);

        File.Move(removed, Path.ChangeExtension(removed, ".old"));
        if (replace)
        {
            var created = File.GetCreationTimeUtc(path);
            var modified = File.GetLastWriteTimeUtc(path);
            File.Move(path, Path.ChangeExtension(path, ".old"));
            File.WriteAllText(path, unkeyed.PadRight(original.Length));
            File.SetCreationTimeUtc(path, created);
            File.SetLastWriteTimeUtc(path, modified.AddSeconds(1));
        }
        else File.WriteAllText(path, unkeyed);

        scanner.Update(Claude, Codex).FilesRead.Should().Be(1);
        var after = scanner.ReadReport(CostProvider.Claude, asOf: Today);
        after.Daily.Should().BeEquivalentTo(before.Daily);
        after.Coverage.Should().BeEquivalentTo(before.Coverage);
    }

    [TestMethod]
    public void Update_WhenChangedFileIsLocked_ScansOtherFilesAndRetriesFromPreviousOffset()
    {
        var scanner = Scanner();
        var locked = Path.Combine(Claude, "locked.jsonl");
        File.WriteAllText(locked, ClaudeLine("1", 10) + "\n");
        scanner.Update(Claude, Codex);
        var append = ClaudeLine("2", 20) + "\n";
        File.AppendAllText(locked, append);
        File.WriteAllText(Path.Combine(Claude, "readable.jsonl"), ClaudeLine("3", 30) + "\n");
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var scan = scanner.Update(Claude, Codex, workers: 2);
            scan.SkippedFiles.Should().Be(1);
            scan.FilesRead.Should().Be(1);
            scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(40);
        }

        var resumed = scanner.Update(Claude, Codex);
        resumed.SkippedFiles.Should().Be(0);
        resumed.BytesRead.Should().Be(Encoding.UTF8.GetByteCount(append));
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(60);
    }

    [TestMethod]
    public void Update_WhenColdScanIsCancelled_ResumesFromCommittedBatch()
    {
        for (var i = 0; i < 105; i++)
            File.WriteAllText(Path.Combine(Claude, $"{i}.jsonl"), ClaudeLine(i.ToString(), 10) + "\n");

        using var cancellation = new CancellationTokenSource();
        var scanner = Scanner(logger: OnBatchCommitted(cancellation.Cancel));
        var scan = () => scanner.Update(Claude, Codex, workers: 2, cancellationToken: cancellation.Token);
        scan.Should().Throw<OperationCanceledException>();
        scanner.ReadReport(CostProvider.Claude, asOf: Today).SessionTokens.Should().Be(1000);

        scanner.Update(Claude, Codex, workers: 2).FilesRead.Should().Be(5);
        var uninterrupted = new NativeCostScanner(Path.Combine(_directory, "uninterrupted"),
            Mock.Of<ILogger<NativeCostScanner>>(), TimeZoneInfo.Utc);
        uninterrupted.Update(Claude, Codex, workers: 2).FilesRead.Should().Be(105);
        scanner.ReadReport(CostProvider.Claude, asOf: Today).Should()
            .BeEquivalentTo(uninterrupted.ReadReport(CostProvider.Claude, asOf: Today));
    }

    [TestMethod]
    public void Update_WhenCancelledAfterCommit_RestoresThreadPriority()
    {
        const int begin = 0x00010000;
        const int end = 0x00020000;
        const int alreadyBackground = 400;
        using var cancellation = new CancellationTokenSource();
        var thread = GetCurrentThread();
        var inBackground = false;
        var scanner = Scanner(logger: OnBatchCommitted(() =>
        {
            if (SetThreadPriority(thread, begin)) SetThreadPriority(thread, end);
            else inBackground = Marshal.GetLastPInvokeError() == alreadyBackground;
            cancellation.Cancel();
        }));
        File.WriteAllText(Path.Combine(Claude, "fixture.jsonl"), ClaudeLine("1", 10) + "\n");
        var update = () => scanner.Update(Claude, Codex, cancellationToken: cancellation.Token);

        update.Should().Throw<OperationCanceledException>();
        inBackground.Should().BeTrue();
        var restored = SetThreadPriority(thread, begin);
        if (restored) SetThreadPriority(thread, end);
        restored.Should().BeTrue();
    }

    private static ILogger<NativeCostScanner> OnBatchCommitted(Action action)
    {
        var logger = new Mock<ILogger<NativeCostScanner>>();
        logger.Setup(log => log.Log(LogLevel.Debug, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state != null && state.ToString()!.StartsWith("Committed cost scan batch", StringComparison.Ordinal)),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(action);
        return logger.Object;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(IntPtr thread, int priority);

    private static string ClaudeLine(string id, int input, string timestamp = "2026-11-25T09:00:00Z") =>
        $$$$"""
        {"type":"assistant","timestamp":"{{{{timestamp}}}}","requestId":"request-{{{{id}}}}","message":{"id":"message-{{{{id}}}}","model":"claude-sonnet-4-6","usage":{"input_tokens":{{{{input}}}}}}}
        """;

    private sealed class DoubleRates : CostPricing
    {
        public override CostRates? GetRates(CostProvider provider, string model, bool above200K, bool above272K,
            bool historical, int tier) => new(6, 30, .6m, 7.5m, 12);
    }
}
