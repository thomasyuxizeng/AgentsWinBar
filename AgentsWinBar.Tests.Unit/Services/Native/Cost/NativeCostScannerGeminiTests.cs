using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services.Native;
using AgentsWinBar.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentsWinBar.Tests.Unit.Services.Native.Cost.AntigravityFixtures;
using static AgentsWinBar.Tests.Unit.Services.Native.Cost.CostTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native.Cost;

/// <summary>Antigravity (agy) conversation databases in the cost index: days, windows, dedupe, prices and rescans.</summary>
[TestClass]
public sealed class NativeCostScannerGeminiTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private string Home => Path.Combine(_directory, ".gemini");
    private string Cli => Path.Combine(Home, "antigravity-cli", "conversations");
    private string App => Path.Combine(Home, "antigravity", "conversations");
    private IReadOnlyList<CostLogRoot> Roots => NativeCostService.GeminiRoots(Home);

    [TestInitialize]
    public void Initialize()
    {
        _directory = TempDirectory("cost-gemini");
        Directory.CreateDirectory(Cli);
        Directory.CreateDirectory(App);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private NativeCostScanner Scanner(TimeZoneInfo? zone = null) =>
        new(Path.Combine(_directory, "index"), NullLogger<NativeCostScanner>.Instance, zone ?? TimeZoneInfo.Utc);

    /// <summary>Fixed list prices, so the expected costs do not move with the bundled snapshot.</summary>
    private static CostPricing Pricing() => new(Catalog(CatalogJson(
        Model("claude-sonnet-5-5", "\"input\":2,\"output\":10,\"cache_read\":0.1,\"cache_write\":2.5"),
        Model("gpt-6.2-sol", "\"input\":5,\"output\":30"),
        googleModels: string.Join(",",
            Model("gemini-4-neon", "\"input\":3,\"output\":15,\"cache_read\":0.15"),
            Model("gemini-3.1-pro-preview", "\"input\":2,\"output\":12,\"cache_read\":0.2"),
            Model("gemini-3.7-flash", "\"input\":0.75,\"output\":3.75")))));

    private IReadOnlyDictionary<CostProvider, CostDataDto> Reports(NativeCostScanner scanner) =>
        scanner.UpdateAndReadReports(Roots, 1, [CostProvider.Gemini], Pricing(), Today, 30, CancellationToken.None).Reports;

    [TestMethod]
    public void ReadReport_WhenCallsStraddleLocalMidnight_PutsEachOnItsLocalDay()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+9", TimeSpan.FromHours(9), "test+9", "test+9");
        var beforeMidnight = new DateTimeOffset(2026, 11, 24, 14, 59, 59, TimeSpan.Zero);
        Create(Path.Combine(Cli, "conversation.db"),
            [
                new Call { Input = 1, Time = beforeMidnight },
                new Call { Input = 20, StepId = "step-a" }
            ],
            [new StepRow(0, beforeMidnight.AddSeconds(1), "step-a")]);
        var scanner = Scanner(zone);
        scanner.Update(Roots);

        var report = scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: new DateTimeOffset(2026, 11, 25, 3, 0, 0, TimeSpan.Zero));

        report.Daily!.Select(day => (day.Date, day.TotalTokens)).Should().Equal(
            (new DateOnly(2026, 11, 24), 1L), (new DateOnly(2026, 11, 25), 20L));
        report.SessionTokens.Should().Be(20, "the call at local midnight belongs to today");
        var utc = Scanner(TimeZoneInfo.Utc);
        utc.Update(Roots).FilesRead.Should().Be(0);
        utc.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today).Daily!.Single().TotalTokens
            .Should().Be(21, "both calls fall on one UTC day, and a new zone re-buckets the stored calls without reading again");
    }

    [TestMethod]
    public void ReadReport_WhenACallIsOlderThanThirtyDays_LeavesItOut()
    {
        Create(Path.Combine(Cli, "conversation.db"),
        [
            new Call { Input = 1, Time = Today.AddDays(-30) },
            new Call { Input = 10, Time = Today.AddDays(-29) },
            new Call { Input = 100, Time = Today }
        ]);
        var scanner = Scanner();
        scanner.Update(Roots);

        var report = scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today);

        report.Last30DaysTokens.Should().Be(110);
        report.SessionTokens.Should().Be(100);
        report.Daily!.Select(day => day.Date).Should().Equal(new DateOnly(2026, 10, 27), new DateOnly(2026, 11, 25));
    }

    [TestMethod]
    public void Update_WhenADatabaseIsCopiedIntoAnotherRoot_CountsItsCallsOnce()
    {
        var original = Path.Combine(Cli, "conversation.db");
        Create(original,
        [
            new Call { Input = 100, ResponseId = "response-1", Time = Today },
            new Call { Input = 50, Time = Today }
        ]);
        File.Copy(original, Path.Combine(App, "conversation.db"));
        var scanner = Scanner();

        var result = scanner.Update(Roots);

        result.FilesRead.Should().Be(2);
        result.Gemini!.Calls.Should().Be(4);
        scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today).Last30DaysTokens.Should().Be(150);
    }

    [TestMethod]
    public void ReadReport_WhenCallsUseGeminiAndClaudeModels_PricesEachAtItsVendorsListRate()
    {
        Create(Path.Combine(Cli, "conversation.db"),
        [
            new Call { Input = 1_000_000, Output = 60_000, Thinking = 40_000, CacheRead = 1_000_000, Model = "gemini-4-neon", Time = Today },
            new Call { Input = 1_000_000, Output = 100_000, Model = "claude-sonnet-5-5", Time = Today },
            new Call { Input = 1_000_000, Model = "gemini-3.1-pro-high", Time = Today },
            new Call { Input = 1_000_000, Model = "gemini-4-neon-thinking", Time = Today },
            new Call { Input = 1_000_000, CacheRead = 1_000_000, Model = "gemini-3.7-flash", Time = Today },
            new Call { Input = 1_000_000, Model = "gpt-oss-120b-medium", Time = Today }
        ]);
        var scanner = Scanner();
        scanner.Update(Roots);

        var report = scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today);

        var costs = report.Daily!.Single().ModelBreakdowns!.ToDictionary(model => model.ModelName!, model => model.Cost);
        costs["gemini-4-neon"].Should().Be(3m + 1.5m + 0.15m, "thinking tokens bill at the output rate");
        costs["claude-sonnet-5-5"].Should().Be(2m + 1m);
        costs["gemini-3.1-pro-high"].Should().Be(2m, "a routing variant bills as its catalogued base model");
        costs["gemini-4-neon-thinking"].Should().Be(3m);
        costs["gemini-3.7-flash"].Should().Be(0.75m + 0.75m, "a catalog row without a cache rate charges cached input at the input rate");
        costs["gpt-oss-120b-medium"].Should().Be(0.09m, "the bundled overlay prices a model no vendor catalog lists");
        report.UnpricedTokens.Should().Be(0);
        report.Provenance.Should().Be("native-sqlite");
        report.Provider.Should().Be("antigravity");
    }

    [TestMethod]
    public void ReadReport_WhenAModelIsUnknown_LeavesItsTokensUnpriced()
    {
        Create(Path.Combine(Cli, "conversation.db"),
        [
            new Call { Input = 1_000_000, Model = "gemini-4-neon", Time = Today },
            new Call { Input = 700, Model = "mystery-model-9", Time = Today },
            new Call { Input = 300, Model = null, Time = Today }
        ]);
        var scanner = Scanner();
        scanner.Update(Roots);

        var report = scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today);

        report.UnpricedTokens.Should().Be(1000);
        report.Coverage!.Unpriced.Should().Be(2);
        report.Coverage.Priced.Should().Be(1);
        report.SessionCostUSD.Should().Be(3m);
        var breakdowns = report.Daily!.Single().ModelBreakdowns!;
        breakdowns.Single(model => model.ModelName == "mystery-model-9").Cost.Should().BeNull("no price is not a price of zero");
        breakdowns.Single(model => model.ModelName == CostPricing.UnattributedModel).Cost.Should().BeNull();
    }

    [TestMethod]
    public void ReadReport_WhenACallHasNoModelAndNoTokens_CountsItWithoutAModelRow()
    {
        Create(Path.Combine(Cli, "conversation.db"),
            [new Call { Model = null, Time = Today }, new Call { Input = 5, Model = "gemini-4-neon", Time = Today }]);
        var scanner = Scanner();
        scanner.Update(Roots);

        var report = scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today);

        report.Coverage!.Priced.Should().Be(2);
        report.Coverage.Unpriced.Should().Be(0);
        report.Daily!.Single().ModelBreakdowns!.Select(model => model.ModelName).Should().Equal("gemini-4-neon");
    }

    [TestMethod]
    public void UpdateAndReadReports_WhenNoDatabaseExists_ReturnsNoGeminiReport()
    {
        Directory.Delete(Home, recursive: true);

        Reports(Scanner()).Should().NotContainKey(CostProvider.Gemini);
    }

    [TestMethod]
    public void UpdateAndReadReports_WhenOnlyOtherDatabasesExist_ReturnsNoGeminiReport()
    {
        using (var connection = Open(Path.Combine(Cli, "other.db")))
            Execute(connection, "CREATE TABLE other(idx INTEGER, data BLOB)");
        Directory.CreateDirectory(Path.Combine(Cli, "nested"));
        Create(Path.Combine(Cli, "nested", "conversation.db"), [new Call { Input = 1, Time = Today }]);
        Create(Path.Combine(Cli, ".hidden.db"), [new Call { Input = 1, Time = Today }]);
        var scanner = Scanner();

        var reports = Reports(scanner);

        reports.Should().NotContainKey(CostProvider.Gemini, "a database without gen_metadata is not Antigravity history");
        scanner.Update(Roots, changedPaths: [Path.Combine(Cli, "nested", "conversation.db")]).FilesRead
            .Should().Be(0, "only databases directly inside a root count");
    }

    [TestMethod]
    public void UpdateAndReadReports_WhenHistoryHasNoUsableCall_ReturnsAnEmptyReport()
    {
        Create(Path.Combine(Cli, "conversation.db"), [new Call { Input = 4 }]);

        var report = Reports(Scanner())[CostProvider.Gemini];

        report.Last30DaysTokens.Should().Be(0);
        report.Daily.Should().BeEmpty();
    }

    [TestMethod]
    public void Update_WhenADatabaseIsLocked_SkipsItAndReadsItOnceUnlocked()
    {
        var path = Path.Combine(Cli, "conversation.db");
        Create(path, [new Call { Input = 9, Time = Today }]);
        var scanner = Scanner();

        CostScanResult whileLocked;
        IReadOnlyDictionary<CostProvider, CostDataDto> reportsWhileLocked;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            whileLocked = scanner.Update(Roots);
            reportsWhileLocked = Reports(scanner);
        }
        var afterUnlock = scanner.Update(Roots, changedPaths: [path]);

        whileLocked.UnavailableFiles.Should().Be(1);
        whileLocked.SkippedPaths.Should().Equal(path);
        reportsWhileLocked.Should().NotContainKey(CostProvider.Gemini);
        afterUnlock.FilesRead.Should().Be(1);
        scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today).SessionTokens.Should().Be(9);
    }

    [TestMethod]
    public void Update_WhenOneDatabaseIsDamaged_ReadsTheOthers()
    {
        File.WriteAllBytes(Path.Combine(Cli, "damaged.db"), Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        Create(Path.Combine(Cli, "conversation.db"), [new Call { Input = 9, Time = Today }]);
        var scanner = Scanner();

        var result = scanner.Update(Roots);

        result.UnavailableFiles.Should().Be(1);
        result.FilesRead.Should().Be(1);
        scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today).SessionTokens.Should().Be(9);
    }

    [TestMethod]
    public void Update_WhenNothingChanged_ReadsNoDatabase()
    {
        var path = Path.Combine(Cli, "conversation.db");
        Create(path, [new Call { Input = 9, Time = Today }]);
        var scanner = Scanner();
        scanner.Update(Roots).FilesRead.Should().Be(1);

        var listing = scanner.Update(Roots);
        var notified = scanner.Update(Roots, changedPaths: [path]);

        listing.FilesRead.Should().Be(0);
        listing.Gemini.Should().BeNull();
        notified.FilesRead.Should().Be(0);
        notified.BytesRead.Should().Be(0);
    }

    [TestMethod]
    public void Update_WhenTheWriteAheadLogGrows_ReadsTheDatabaseAgainWithoutCountingTwice()
    {
        var path = Path.Combine(Cli, "conversation.db");
        var scanner = Scanner();
        var writer = Open(path);
        try
        {
            Execute(writer, "PRAGMA wal_autocheckpoint=0");
            CreateSchema(writer);
            Insert(writer, [(0, new Call { Input = 10, Time = Today })], []);

            var first = scanner.Update(Roots);
            first.FilesRead.Should().Be(1);
            first.RecheckPaths.Should().Equal(path);

            Insert(writer, [(1, new Call { Input = 20, Time = Today })], []);
            scanner.Update(Roots, changedPaths: [path]).FilesRead.Should().Be(1);
            scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today).SessionTokens.Should().Be(30);
            scanner.Update(Roots, changedPaths: [path]).FilesRead.Should().Be(0, "neither the file nor its log changed");
        }
        finally
        {
            // Closing checkpoints the log into the main file and deletes it, as agy does when a conversation ends.
            writer.Dispose();
        }
        File.Exists(path + "-wal").Should().BeFalse();

        scanner.Update(Roots, changedPaths: [path]).FilesRead.Should().Be(1);
        scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today).SessionTokens.Should().Be(30);
    }

    [TestMethod]
    public void Update_WhenAgyRewritesACall_ReplacesItsRowInsteadOfAddingOne()
    {
        var path = Path.Combine(Cli, "conversation.db");
        Create(path, [new Call { Input = 10, Time = Today }, new Call { Input = 1, ResponseId = "response-2", Time = Today }]);
        var scanner = Scanner();
        scanner.Update(Roots);

        // The first call is completed in place: it gains its response id and its final count.
        using (var connection = Open(path))
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE gen_metadata SET data=$data WHERE idx=0";
            update.Parameters.AddWithValue("$data", Encode(new Call { Input = 12, ResponseId = "response-1", Time = Today }));
            update.ExecuteNonQuery();
        }
        scanner.Update(Roots, changedPaths: [path]).FilesRead.Should().Be(1);

        scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today).SessionTokens.Should().Be(13);
    }

    [TestMethod]
    public void Update_WhenACopyIsReadAgain_StillCountsTheSharedCallsOnce()
    {
        var original = Path.Combine(Cli, "conversation.db");
        Create(original, [new Call { Input = 100, ResponseId = "response-1", Time = Today }]);
        var copy = Path.Combine(App, "conversation.db");
        File.Copy(original, copy);
        var scanner = Scanner();
        scanner.Update(Roots);

        using (var connection = Open(copy))
            Insert(connection, [(1, new Call { Input = 5, ResponseId = "response-2", Time = Today })], []);
        scanner.Update(Roots, changedPaths: [copy]).FilesRead.Should().Be(1);
        using (var connection = Open(original))
            Insert(connection, [(1, new Call { Input = 5, ResponseId = "response-2", Time = Today })], []);
        scanner.Update(Roots, changedPaths: [original]).FilesRead.Should().Be(1);

        scanner.ReadReport(CostProvider.Gemini, Pricing(), asOf: Today).SessionTokens.Should().Be(105);
    }

    [TestMethod]
    public void ReadReport_WhenACallsInputPassesTheLongContextTier_UsesTheLongContextRates()
    {
        Create(Path.Combine(Cli, "conversation.db"),
        [
            new Call { Input = 150_000, CacheRead = 100_000, Output = 10_000, Model = "gemini-3.1-pro-high", Time = Today },
            new Call { Input = 100_000, CacheRead = 50_000, Output = 10_000, Model = "gemini-3.1-pro-high", Time = Today }
        ]);
        var scanner = Scanner();
        scanner.Update(Roots);
        var pricing = new CostPricing(Catalog(CatalogJson("", "", googleModels: Model("gemini-3.1-pro-preview",
            "\"input\":2,\"output\":12,\"cache_read\":0.2,\"context_over_200k\":{\"input\":4,\"output\":18,\"cache_read\":0.4}"))));

        var report = scanner.ReadReport(CostProvider.Gemini, pricing, asOf: Today);

        // Uncached plus cached input over 200K (as upstream's resolver counts it): 0.60 + 0.04 + 0.18 at the long
        // rates; the smaller call at the base rates: 0.20 + 0.01 + 0.12.
        report.SessionCostUSD.Should().Be(0.82m + 0.33m);
    }

    [TestMethod]
    public void Update_WhenDatabasesAreRead_ReportsHowEachCallWasDated()
    {
        Create(Path.Combine(Cli, "conversation.db"),
            [
                new Call { Input = 1, Time = Today },
                new Call { Input = 2, StepId = "step-a", BotId = "bot-1" },
                new Call { Input = 3, StepId = "step-b" },
                new Call { Input = 4, StepId = "step-missing" }
            ],
            [new StepRow(0, Today, "step-a", "bot-1"), new StepRow(1, Today, "step-b")]);

        var statistics = Scanner().Update(Roots).Gemini!;

        statistics.Databases.Should().Be(1);
        statistics.HistoryDatabases.Should().Be(1);
        statistics.Rows.Should().Be(4);
        statistics.Calls.Should().Be(3);
        statistics.DatedByOwnTime.Should().Be(1);
        statistics.DatedByBotId.Should().Be(1);
        statistics.DatedByPosition.Should().Be(1);
        statistics.Undated.Should().Be(1);
    }

    [TestMethod]
    public void Update_WhenADatabaseIsRead_ChangesNoFileInTheRoot()
    {
        var path = Path.Combine(Cli, "conversation.db");
        Create(path, [new Call { Input = 9, Time = Today }], [new StepRow(0, Today, "step-a", PayloadBytes: 1 << 16)]);
        var before = Snapshot(Cli);

        Scanner().Update(Roots).FilesRead.Should().Be(1);

        Snapshot(Cli).Should().BeEquivalentTo(before);
    }
}
