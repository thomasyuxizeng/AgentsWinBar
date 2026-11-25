using System.Text;
using AwesomeAssertions;
using CodexBarWin.Services.Native.Cost;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public sealed class NativeCostScannerTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "cost-test-" + Guid.NewGuid());
    private string Claude => Path.Combine(_directory, "claude");
    private string Codex => Path.Combine(_directory, "codex");

    [TestInitialize]
    public void Initialize()
    {
        Directory.CreateDirectory(Claude);
        Directory.CreateDirectory(Codex);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private NativeCostScanner Scanner(TimeZoneInfo? zone = null) =>
        new(Path.Combine(_directory, "index"), Mock.Of<ILogger<NativeCostScanner>>(), zone ?? TimeZoneInfo.Utc);

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
