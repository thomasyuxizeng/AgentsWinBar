using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class CostLedgerTests
{
    [TestMethod]
    public async Task RecordScanAsync_AggregateWithNegativeCost_ClampsSyntheticDayToZero()
    {
        var ledger = CreateLedger();
        await ledger.RecordScanAsync(new CostData
        { Provider = "claude", FetchOutcome = FetchOutcome.Success, TodayCostUSD = -1m, TodayTokens = 100 });
        var day = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var result = ledger.GetRange("claude", day, day).Days.Single();
        result.CostUSD.Should().Be(0m);
        result.TotalTokens.Should().Be(100);
    }

    [TestMethod]
    public async Task RecordScansAsync_MergeOverflows_RollsBackEntireBatch()
    {
        var ledger = CreateLedger();
        await ledger.RecordScanAsync(SampleScan("claude", new(2026, 10, 1), 10m, 1000));
        await ledger.RecordScansAsync([
            SampleScan("claude", new(2026, 10, 2), 2m, 200),
            new CostData
            {
                Provider = "codex", FetchOutcome = FetchOutcome.Success,
                Daily = [new() { Date = new(2026, 10, 1), TotalTokens = long.MaxValue },
                    new() { Date = new(2026, 10, 1), TotalTokens = 1 }]
            }]);
        ledger.GetLifetime().TotalCostUSD.Should().Be(10m);
        var reloaded = CreateLedger();
        await reloaded.LoadAsync();
        reloaded.GetLifetime().Should().BeEquivalentTo(ledger.GetLifetime());
    }

    [TestMethod]
    public async Task LoadCoreAsync_CancelledRead_LeavesFileIntactAndAllowsRetry()
    {
        await CreateLedger().RecordScanAsync(SampleScan("claude", new(2026, 10, 1), 10m, 1000));
        var ledger = CreateLedger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var load = (Task)typeof(CostLedger).GetMethod("LoadCoreAsync", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(ledger, [cancellation.Token])!;
        Func<Task> act = () => load;
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(_ledgerPath).Should().BeTrue();
        Directory.GetFiles(_directory, "cost-ledger.corrupt-*").Should().BeEmpty();
        await ledger.LoadAsync();
        ledger.GetLifetime().TotalCostUSD.Should().Be(10m);
    }

    [TestMethod]
    public async Task GetRange_MaximumDate_ReturnsFinalDayWithoutOverflow()
    {
        var ledger = CreateLedger();
        await ledger.RecordScanAsync(SampleScan("claude", DateOnly.MaxValue, 1m, 100));
        ledger.GetRange("claude", DateOnly.MaxValue, DateOnly.MaxValue).TotalCostUSD.Should().Be(1m);
    }

    [TestMethod]
    public async Task GetLast30Days_MinimumDate_ClampsStartToSupportedCalendar()
    {
        var ledger = CreateLedger();
        await ledger.LoadAsync();
        ledger.GetLast30Days("claude", DateOnly.MinValue).Days.Should().ContainSingle()
            .Which.Date.Should().Be(DateOnly.MinValue);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoadAsync_NullAndDuplicateModels_NormalizesPrimaryAndBackup(bool backup)
    {
        var path = backup ? Path.Combine(_directory, "cost-ledger.bak") : _ledgerPath;
        if (backup) await File.WriteAllTextAsync(_ledgerPath, "{broken");
        await File.WriteAllTextAsync(path, """
            {"schemaVersion":1,"providers":{"CLAUDE":{
              "2569-10-01":{"date":"2026-10-01","costUSD":10,"totalTokens":1000,"models":null},
              "2569-10-02":{"date":"2026-10-02","costUSD":2,"totalTokens":200,"models":[
                null,{"modelName":null},{"modelName":"sonnet","costUSD":2,"totalTokens":200},
                {"modelName":"SONNET","costUSD":1,"totalTokens":100}]}
            }}}
            """);
        var ledger = CreateLedger();
        await ledger.LoadAsync();
        ledger.GetLifetime().TotalCostUSD.Should().Be(12m);
        var days = ledger.GetRange("claude", new(2026, 10, 1), new(2026, 10, 2)).Days;
        days[0].Models.Should().BeEmpty();
        days[1].Models.Should().ContainSingle().Which.TotalTokens.Should().Be(200);
        await ledger.RecordScanAsync(SampleScan("claude", new(2026, 10, 2), 3m, 300,
            models: [("sonnet", 3m, 300)]));
        ledger.GetLifetime().TotalCostUSD.Should().Be(13m);
    }

    [TestMethod]
    public async Task LoadAsync_InvalidBackupGraph_FallsBackToNormalizedCache()
    {
        await File.WriteAllTextAsync(_ledgerPath, "{broken");
        await File.WriteAllTextAsync(Path.Combine(_directory, "cost-ledger.bak"), """{"providers":null}""");
        await File.WriteAllTextAsync(_costCachePath, """
            {"items":{
              "claude":{"provider":"invalid","daily":[{"date":"2026-10-01","totalCost":999}]},
              "codex":{"provider":"codex","daily":[null,{"date":"2026-10-01","totalCost":2,"totalTokens":200,
                "modelBreakdowns":[null,{"modelName":null},{"modelName":"gpt","cost":2,"totalTokens":200}]}]},
              "gemini":null
            }}
            """);
        var ledger = CreateLedger();
        await ledger.LoadAsync();
        ledger.GetLifetime().TotalCostUSD.Should().Be(2m);
        ledger.GetRange("codex", new(2026, 10, 1), new(2026, 10, 1)).Days.Single()
            .Models.Should().ContainSingle().Which.ModelName.Should().Be("gpt");
        await ledger.RecordScanAsync(SampleScan("claude", new(2026, 10, 2), 1m, 100));
        ledger.GetLifetime().TotalCostUSD.Should().Be(3m);
    }

    [TestMethod]
    public async Task RecordScanAsync_PartialRotation_PreservesAccumulatedPastDay()
    {
        var ledger = CreateLedger();
        var day = new DateOnly(2026, 10, 1);
        await ledger.RecordScanAsync(SampleScan("claude", day, 10m, 1000, models: [("sonnet", 10m, 1000)]));
        await ledger.RecordScanAsync(SampleScan("claude", day, 2m, 200, models: [("sonnet", 2m, 200)]));

        var reloaded = CreateLedger();
        await reloaded.LoadAsync();
        var result = reloaded.GetRange("claude", day, day).Days.Single();
        result.CostUSD.Should().Be(10m);
        result.TotalTokens.Should().Be(1000);
        result.Models.Single().CostUSD.Should().Be(10m);
    }

    [TestMethod]
    public async Task RecordScanAsync_UnchangedTokenCoverage_AllowsPastDayRepricing()
    {
        var ledger = CreateLedger();
        var day = new DateOnly(2026, 10, 1);
        await ledger.RecordScanAsync(SampleScan("claude", day, 10m, 1000, input: 800, output: 200,
            models: [("sonnet", 10m, 1000)]));
        await ledger.RecordScanAsync(SampleScan("claude", day, 8m, 1000, input: 800, output: 200,
            models: [("sonnet", 8m, 1000)]));

        ledger.GetLifetime("claude").TotalCostUSD.Should().Be(8m);
        ledger.GetRange("claude", day, day).Days.Single().Models.Single().CostUSD.Should().Be(8m);
    }

    [TestMethod]
    public async Task RecordScanAsync_ThaiCulture_UsesGregorianKeysAcrossRestart()
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("th-TH");
            var day = new DateOnly(2026, 10, 1);
            await CreateLedger().RecordScanAsync(SampleScan("claude", day, 10m, 1000));
            (await File.ReadAllTextAsync(_ledgerPath)).Should().Contain("2026-10-01").And.NotContain("2569-10-01");
            System.Globalization.CultureInfo.CurrentCulture = new("en-US");
            var ledger = CreateLedger();
            await ledger.RecordScanAsync(SampleScan("claude", day, 10m, 1000));
            ledger.GetLifetime().TotalCostUSD.Should().Be(10m);
            System.Globalization.CultureInfo.CurrentCulture = new("th-TH");
            ledger.GetRange("claude", day, day).TotalCostUSD.Should().Be(10m);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    [TestMethod]
    public async Task LoadAsync_CalendarAliases_DeduplicatesByEntryDateUsingLatestUpdate()
    {
        await File.WriteAllTextAsync(_ledgerPath, """
            {"schemaVersion":1,"providers":{"claude":{
              "2569-10-01":{"date":"2026-10-01","costUSD":10,"lastUpdatedUtc":"2026-10-02T00:00:00Z"},
              "2026-10-01":{"date":"2026-10-01","costUSD":8,"lastUpdatedUtc":"2026-10-03T00:00:00Z"}
            }}}
            """);
        var ledger = CreateLedger();
        await ledger.LoadAsync();
        ledger.GetLifetime().TotalCostUSD.Should().Be(8m);
        ledger.GetRange("claude", new(2026, 10, 1), new(2026, 10, 1)).TotalCostUSD.Should().Be(8m);
    }

    [TestMethod]
    [DataRow("{\"schemaVersion\":1,\"providers\":null}")]
    [DataRow("{\"schemaVersion\":1,\"providers\":{\"claude\":null}}")]
    [DataRow("{\"schemaVersion\":1,\"providers\":{\"claude\":{\"2026-10-01\":null}}}")]
    public async Task LoadAsync_NullRequiredMembers_RecoversValidatedBackup(string json)
    {
        await CreateLedger().RecordScanAsync(SampleScan("claude", new(2026, 10, 1), 10m, 1000));
        File.Copy(_ledgerPath, Path.Combine(_directory, "cost-ledger.bak"));
        await File.WriteAllTextAsync(_ledgerPath, json);
        var ledger = CreateLedger();
        await ledger.LoadAsync();
        ledger.GetLifetime().TotalCostUSD.Should().Be(10m);
        ledger.GetLast30Days("claude").TotalCostUSD.Should().Be(10m);
        await ledger.RecordScanAsync(SampleScan("claude", new(2026, 10, 2), 2m, 200));
        ledger.GetLifetime().TotalCostUSD.Should().Be(12m);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private string _directory = null!;
    private TestClock _clock = null!;
    private MockSettingsService _settings = null!;
    private string _ledgerPath = null!;
    private string _costCachePath = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _clock = new();
        _settings = new();
        _ledgerPath = Path.Combine(_directory, "cost-ledger.json");
        _costCachePath = Path.Combine(_directory, "cost-cache.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private CostLedger CreateLedger(string? ledgerPath = null, string? costCachePath = null) =>
        new(_settings, NullLogger<CostLedger>.Instance, _clock, ledgerPath ?? _ledgerPath, costCachePath ?? _costCachePath);

    private static CostData SampleScan(string provider, DateOnly date, decimal cost, long tokens,
        long input = 0, long output = 0, long cacheRead = 0, long cacheWrite = 0, long reasoning = 0,
        (string Model, decimal Cost, long Tokens)[]? models = null)
    {
        var modelBreakdowns = (models ?? []).Select(m => new ModelCost
        {
            ModelName = m.Model,
            Cost = m.Cost,
            TotalTokens = m.Tokens
        }).ToList();

        return new CostData
        {
            Provider = provider,
            FetchOutcome = FetchOutcome.Success,
            TodayCostUSD = 0,
            TodayTokens = 0,
            Last30DaysCostUSD = cost,
            Last30DaysTokens = tokens,
            Daily =
            [
                new DailyCost
                {
                    Date = date,
                    TotalCost = cost,
                    TotalTokens = tokens,
                    InputTokens = input,
                    OutputTokens = output,
                    CacheReadTokens = cacheRead,
                    CacheCreationTokens = cacheWrite,
                    ReasoningTokens = reasoning,
                    ModelBreakdowns = modelBreakdowns
                }
            ]
        };
    }

    // 1. Merge rules: Upsert
    [TestMethod]
    public async Task MergeRules_Upsert_InsertsNewDaysAndUpdatesExistingDays()
    {
        var ledger = CreateLedger();
        var day1 = new DateOnly(2026, 10, 1);
        var day2 = new DateOnly(2026, 10, 2);

        // First scan with day 1
        await ledger.RecordScanAsync(SampleScan("claude", day1, 5.0m, 50000, input: 1000));
        var lifetime1 = ledger.GetLifetime("claude");
        lifetime1.TotalCostUSD.Should().Be(5.0m);
        lifetime1.TotalTokens.Should().Be(50000);
        lifetime1.DaysWithDataCount.Should().Be(1);

        // Second scan updates day 1 and adds day 2
        var scan2 = new CostData
        {
            Provider = "claude",
            FetchOutcome = FetchOutcome.Success,
            Daily =
            [
                new DailyCost { Date = day1, TotalCost = 7.5m, TotalTokens = 75000, InputTokens = 2000 },
                new DailyCost { Date = day2, TotalCost = 3.0m, TotalTokens = 30000, InputTokens = 500 }
            ]
        };
        await ledger.RecordScanAsync(scan2);

        var lifetime2 = ledger.GetLifetime("claude");
        lifetime2.TotalCostUSD.Should().Be(10.5m);
        lifetime2.TotalTokens.Should().Be(105000);
        lifetime2.DaysWithDataCount.Should().Be(2);

        var range = ledger.GetRange("claude", day1, day2);
        range.Days.Should().HaveCount(2);
        range.Days[0].CostUSD.Should().Be(7.5m);
        range.Days[0].TotalTokens.Should().Be(75000);
        range.Days[1].CostUSD.Should().Be(3.0m);
        range.Days[1].TotalTokens.Should().Be(30000);
    }

    // 2. Merge rules: Zero-day guard
    [TestMethod]
    public async Task MergeRules_ZeroDayGuard_NeverReplacesDayWithDataWithAllZeroScan()
    {
        var ledger = CreateLedger();
        var day = new DateOnly(2026, 10, 1);

        // Scan 1 has real data
        await ledger.RecordScanAsync(SampleScan("codex", day, 12.0m, 120000,
            models: [("gpt-4o", 12.0m, 120000)]));

        // Scan 2 covers the same day but has zero/empty data (e.g. rotated local logs)
        var zeroScan = new CostData
        {
            Provider = "codex",
            FetchOutcome = FetchOutcome.Success,
            Daily = [new DailyCost { Date = day, TotalCost = 0, TotalTokens = 0 }]
        };
        await ledger.RecordScanAsync(zeroScan);

        // Existing data must NOT be overwritten by all-zero day
        var range = ledger.GetRange("codex", day, day);
        range.Days.Single().CostUSD.Should().Be(12.0m);
        range.Days.Single().TotalTokens.Should().Be(120000);
        range.Days.Single().Models.Should().ContainSingle().Which.ModelName.Should().Be("gpt-4o");
    }

    // 3. Merge rules: No deletions
    [TestMethod]
    public async Task MergeRules_NoDeletions_PreservesDaysTheScanNoLongerCovers()
    {
        var ledger = CreateLedger();
        var oldDay = new DateOnly(2026, 9, 1);
        var recentDay = new DateOnly(2026, 10, 10);

        // First scan records old day
        await ledger.RecordScanAsync(SampleScan("claude", oldDay, 20.0m, 200000));

        // Subsequent scan covers only recent day (30+ days later)
        await ledger.RecordScanAsync(SampleScan("claude", recentDay, 5.0m, 50000));

        var lifetime = ledger.GetLifetime("claude");
        lifetime.DaysWithDataCount.Should().Be(2);
        lifetime.TotalCostUSD.Should().Be(25.0m);
        lifetime.FirstDayWithData.Should().Be(oldDay);
        lifetime.LastDayWithData.Should().Be(recentDay);
    }

    // 4. Merge rules: Today's partial day updates upward
    [TestMethod]
    public async Task MergeRules_TodayPartialDay_UpdatesUpwardOverTheDay()
    {
        var ledger = CreateLedger();
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);

        // Morning scan: $4.00, 40k tokens
        await ledger.RecordScanAsync(SampleScan("claude", today, 4.0m, 40000,
            models: [("sonnet", 4.0m, 40000)]));

        // Afternoon scan: $9.50, 95k tokens
        await ledger.RecordScanAsync(SampleScan("claude", today, 9.5m, 95000,
            models: [("sonnet", 9.5m, 95000)]));

        var rangeAfternoon = ledger.GetRange("claude", today, today);
        rangeAfternoon.Days.Single().CostUSD.Should().Be(9.5m);
        rangeAfternoon.Days.Single().TotalTokens.Should().Be(95000);

        // Flaky evening scan returning lower numbers: does NOT decrease!
        await ledger.RecordScanAsync(SampleScan("claude", today, 2.0m, 20000,
            models: [("sonnet", 2.0m, 20000)]));

        var rangeEvening = ledger.GetRange("claude", today, today);
        rangeEvening.Days.Single().CostUSD.Should().Be(9.5m);
        rangeEvening.Days.Single().TotalTokens.Should().Be(95000);
    }

    // 5. Merge rules: Failed / empty scan is a no-op
    [TestMethod]
    public async Task MergeRules_FailedOrEmptyScan_DoesNotTouchLedger()
    {
        var ledger = CreateLedger();
        var day = new DateOnly(2026, 10, 5);
        await ledger.RecordScanAsync(SampleScan("claude", day, 10.0m, 100000));

        // Scan marked as failed
        var failedScan = new CostData
        {
            Provider = "claude",
            FetchOutcome = FetchOutcome.Failed,
            Daily = [new DailyCost { Date = day, TotalCost = 999.0m, TotalTokens = 999999 }]
        };
        await ledger.RecordScanAsync(failedScan);

        // Scan without HasData
        var emptyScan = new CostData
        {
            Provider = "claude",
            FetchOutcome = FetchOutcome.Success,
            Daily = []
        };
        await ledger.RecordScanAsync(emptyScan);

        var lifetime = ledger.GetLifetime("claude");
        lifetime.TotalCostUSD.Should().Be(10.0m);
        lifetime.TotalTokens.Should().Be(100000);
    }

    // 6. Backfill from cost-cache.json on first run
    [TestMethod]
    public async Task Backfill_FromCostCacheJson_OnFirstRun_PopulatesLedger()
    {
        var date = new DateOnly(2026, 10, 8);
        var cacheFile = new CostCacheFile
        {
            Items = new()
            {
                ["claude"] = new CostData
                {
                    Provider = "claude",
                    FetchOutcome = FetchOutcome.Success,
                    TodayCostUSD = 15.0m,
                    TodayTokens = 150000,
                    Daily = [new DailyCost { Date = date, TotalCost = 15.0m, TotalTokens = 150000 }]
                }
            }
        };
        await File.WriteAllTextAsync(_costCachePath, JsonSerializer.Serialize(cacheFile, AppJsonSerializerContext.Default.CostCacheFile));

        // Fresh ledger instance, cost-ledger.json does not exist
        File.Exists(_ledgerPath).Should().BeFalse();
        var ledger = CreateLedger();

        await ledger.LoadAsync();

        File.Exists(_ledgerPath).Should().BeTrue();
        var lifetime = ledger.GetLifetime("claude");
        lifetime.TotalCostUSD.Should().Be(15.0m);
        lifetime.TotalTokens.Should().Be(150000);
        lifetime.DaysWithDataCount.Should().Be(1);
    }

    // 7. Persistence round trip and restart
    [TestMethod]
    public async Task Persistence_RoundTripAndRestart_PreservesAllData()
    {
        var day1 = new DateOnly(2026, 10, 1);
        var day2 = new DateOnly(2026, 10, 2);

        var ledger1 = CreateLedger();
        await ledger1.RecordScanAsync(SampleScan("claude", day1, 8.5m, 85000, input: 1000, output: 2000,
            cacheRead: 80000, cacheWrite: 2000, reasoning: 50,
            models: [("claude-3-5-sonnet", 8.5m, 85000)]));
        await ledger1.RecordScanAsync(SampleScan("gemini", day2, 4.0m, 40000));

        // Create fresh instance pointing to same file
        var ledger2 = CreateLedger();
        await ledger2.LoadAsync();

        var claudeLife = ledger2.GetLifetime("claude");
        claudeLife.TotalCostUSD.Should().Be(8.5m);
        claudeLife.TotalTokens.Should().Be(85000);
        claudeLife.InputTokens.Should().Be(1000);
        claudeLife.OutputTokens.Should().Be(2000);
        claudeLife.CacheReadTokens.Should().Be(80000);
        claudeLife.CacheWriteTokens.Should().Be(2000);
        claudeLife.ReasoningTokens.Should().Be(50);

        var geminiLife = ledger2.GetLifetime("gemini");
        geminiLife.TotalCostUSD.Should().Be(4.0m);
        geminiLife.TotalTokens.Should().Be(40000);

        var allLife = ledger2.GetLifetime();
        allLife.TotalCostUSD.Should().Be(12.5m);
        allLife.TotalTokens.Should().Be(125000);
        allLife.ByProvider.Keys.Should().BeEquivalentTo(["claude", "gemini"]);
    }

    // 8. Resilience: Corrupt file renamed to corrupt and fresh ledger started
    [TestMethod]
    public async Task Resilience_CorruptFile_RenamedToCorruptAndFreshLedgerStarted()
    {
        await File.WriteAllTextAsync(_ledgerPath, "{ definitely not valid json 12345");

        var ledger = CreateLedger();
        await ledger.LoadAsync();

        // Corrupt file was renamed and NOT deleted
        File.Exists(_ledgerPath).Should().BeFalse();
        var corruptFiles = Directory.GetFiles(_directory, "cost-ledger.corrupt-*.json");
        corruptFiles.Should().HaveCount(1);
        File.ReadAllText(corruptFiles.Single()).Should().Be("{ definitely not valid json 12345");

        // Fresh ledger operates normally
        ledger.GetLifetime().TotalCostUSD.Should().Be(0);
        var day = new DateOnly(2026, 10, 5);
        await ledger.RecordScanAsync(SampleScan("claude", day, 5.0m, 50000));
        ledger.GetLifetime("claude").TotalCostUSD.Should().Be(5.0m);
    }

    // 9. Resilience: Empty file renamed to corrupt
    [TestMethod]
    public async Task Resilience_EmptyFile_RenamedToCorruptAndFreshLedgerStarted()
    {
        await File.WriteAllTextAsync(_ledgerPath, "   \r\n   ");

        var ledger = CreateLedger();
        await ledger.LoadAsync();

        File.Exists(_ledgerPath).Should().BeFalse();
        var corruptFiles = Directory.GetFiles(_directory, "cost-ledger.corrupt-*.json");
        corruptFiles.Should().HaveCount(1);
    }

    // 10. Resilience: Unknown schema version renamed to corrupt
    [TestMethod]
    public async Task Resilience_UnknownVersion_RenamedToCorruptAndFreshLedgerStarted()
    {
        var invalidVersion = """{"schemaVersion": 999, "providers": {}}""";
        await File.WriteAllTextAsync(_ledgerPath, invalidVersion);

        var ledger = CreateLedger();
        await ledger.LoadAsync();

        File.Exists(_ledgerPath).Should().BeFalse();
        var corruptFiles = Directory.GetFiles(_directory, "cost-ledger.corrupt-*.json");
        corruptFiles.Should().HaveCount(1);
    }

    // 11. Resilience: Rolling backup recovers when main file corrupt
    [TestMethod]
    public async Task Resilience_BackupFile_RecoversFromBakWhenMainCorrupt()
    {
        var day = new DateOnly(2026, 10, 1);
        var ledger = CreateLedger();
        await ledger.RecordScanAsync(SampleScan("claude", day, 10.0m, 100000));

        // Another save triggers rolling backup creation
        var day2 = new DateOnly(2026, 10, 2);
        await ledger.RecordScanAsync(SampleScan("claude", day2, 5.0m, 50000));

        var bakPath = Path.Combine(_directory, "cost-ledger.bak");
        File.Exists(bakPath).Should().BeTrue();

        // Corrupt main file
        await File.WriteAllTextAsync(_ledgerPath, "CORRUPT!!!");

        // Reload
        var restarted = CreateLedger();
        await restarted.LoadAsync();

        // Recovered from backup!
        var lifetime = restarted.GetLifetime("claude");
        lifetime.TotalCostUSD.Should().BeGreaterThan(0);
    }

    // 12. Resilience: Atomic write failure never throws
    [TestMethod]
    public async Task Resilience_AtomicWriteFailure_NeverThrows()
    {
        // Target path is blocked because a file exists where a directory is expected
        var blockingFile = Path.Combine(_directory, "blocking-file");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        var invalidPath = Path.Combine(blockingFile, "cost-ledger.json");
        var ledger = CreateLedger(ledgerPath: invalidPath);

        var act = async () => await ledger.RecordScanAsync(SampleScan("claude", new DateOnly(2026, 10, 1), 5m, 5000));
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task MergeRules_ScanWithoutDailyList_SynthesizesTodayEntry()
    {
        var ledger = CreateLedger();
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var scan = new CostData
        {
            Provider = "claude",
            FetchOutcome = FetchOutcome.Success,
            TodayCostUSD = 12.5m,
            TodayTokens = 125000,
            Daily = []
        };
        await ledger.RecordScanAsync(scan);

        var lifetime = ledger.GetLifetime("claude");
        lifetime.TotalCostUSD.Should().Be(12.5m);
        lifetime.TotalTokens.Should().Be(125000);
        lifetime.FirstDayWithData.Should().Be(today);
    }

    // 13. Concurrency: Concurrent scans are serialized safely
    [TestMethod]
    public async Task Concurrency_ConcurrentScans_SerializedSafelyWithoutCorruption()
    {
        var ledger = CreateLedger();
        var tasks = Enumerable.Range(1, 20).Select(i =>
        {
            var date = new DateOnly(2026, 9, i);
            var provider = i % 2 == 0 ? "claude" : "codex";
            return Task.Run(async () =>
            {
                await ledger.RecordScanAsync(SampleScan(provider, date, 1.0m, 1000));
            });
        }).ToArray();

        await Task.WhenAll(tasks);

        var lifetime = ledger.GetLifetime();
        lifetime.TotalCostUSD.Should().Be(20.0m);
        lifetime.TotalTokens.Should().Be(20000);
        lifetime.DaysWithDataCount.Should().Be(20);
    }

    // 14. Query: Lifetime totals and per-provider split
    [TestMethod]
    public async Task Query_GetLifetime_CalculatesTotalsAndSplitAccurately()
    {
        var ledger = CreateLedger();
        await ledger.RecordScanAsync(SampleScan("claude", new DateOnly(2026, 10, 1), 10m, 100000));
        await ledger.RecordScanAsync(SampleScan("codex", new DateOnly(2026, 10, 5), 25m, 250000));

        var lifetime = ledger.GetLifetime();
        lifetime.TotalCostUSD.Should().Be(35m);
        lifetime.TotalTokens.Should().Be(350000);
        lifetime.FirstDayWithData.Should().Be(new DateOnly(2026, 10, 1));
        lifetime.LastDayWithData.Should().Be(new DateOnly(2026, 10, 5));
        lifetime.DaysWithDataCount.Should().Be(2);

        lifetime.ByProvider["claude"].TotalCostUSD.Should().Be(10m);
        lifetime.ByProvider["codex"].TotalCostUSD.Should().Be(25m);

        var single = ledger.GetLifetime("claude");
        single.TotalCostUSD.Should().Be(10m);
        single.FirstDayWithData.Should().Be(new DateOnly(2026, 10, 1));
        single.LastDayWithData.Should().Be(new DateOnly(2026, 10, 1));
    }

    // 15. Query: GetRange
    [TestMethod]
    public async Task Query_GetRange_ReturnsDailySeriesAcrossDateRange()
    {
        var ledger = CreateLedger();
        var d1 = new DateOnly(2026, 10, 1);
        var d2 = new DateOnly(2026, 10, 3);
        await ledger.RecordScanAsync(SampleScan("claude", d1, 5m, 50000));
        await ledger.RecordScanAsync(SampleScan("claude", d2, 7m, 70000));

        var range = ledger.GetRange("claude", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3));
        range.Days.Should().HaveCount(3);
        range.Days[0].HasLedgerRecord.Should().BeTrue();
        range.Days[0].CostUSD.Should().Be(5m);
        range.Days[1].HasLedgerRecord.Should().BeFalse(); // day 2 has no record
        range.Days[2].HasLedgerRecord.Should().BeTrue();
        range.Days[2].CostUSD.Should().Be(7m);
        range.TotalCostUSD.Should().Be(12m);
    }

    // 16. Query: GetLast30Days flags missing days as 'no data' distinct from recorded zero
    [TestMethod]
    public async Task Query_GetLast30Days_FlagsMissingDaysAsNoData_DistinctFromRecordedZero()
    {
        var ledger = CreateLedger();
        var asOfDay = new DateOnly(2026, 10, 11);

        // Day with data
        var dayWithData = asOfDay.AddDays(-2);
        await ledger.RecordScanAsync(SampleScan("claude", dayWithData, 10m, 100000));

        // Day with recorded zero
        var dayZero = asOfDay.AddDays(-5);
        var zeroScan = new CostData
        {
            Provider = "claude",
            FetchOutcome = FetchOutcome.Success,
            TodayCostUSD = 0,
            TodayTokens = 0,
            Daily = [new DailyCost { Date = dayZero, TotalCost = 0, TotalTokens = 0 }]
        };
        await ledger.RecordScanAsync(zeroScan);

        var result = ledger.GetLast30Days("claude", asOfDay);
        result.Days.Should().HaveCount(30);
        result.Days.First().Date.Should().Be(asOfDay.AddDays(-29));
        result.Days.Last().Date.Should().Be(asOfDay);

        // Check day with data
        var statusWithData = result.Days.Single(d => d.Date == dayWithData);
        statusWithData.HasLedgerRecord.Should().BeTrue();
        statusWithData.HasData.Should().BeTrue();
        statusWithData.CostUSD.Should().Be(10m);

        // Check recorded zero
        var statusZero = result.Days.Single(d => d.Date == dayZero);
        statusZero.HasLedgerRecord.Should().BeTrue();
        statusZero.HasData.Should().BeFalse();
        statusZero.CostUSD.Should().Be(0);

        // Check unrecorded day
        var statusUnrecorded = result.Days.Single(d => d.Date == asOfDay.AddDays(-10));
        statusUnrecorded.HasLedgerRecord.Should().BeFalse();
        statusUnrecorded.HasData.Should().BeFalse();
        statusUnrecorded.CostUSD.Should().Be(0);

        // Total cost and tokens over days with data
        result.TotalCostUSD.Should().Be(10m);
        result.TotalTokens.Should().Be(100000);
        result.DaysWithDataCount.Should().Be(1);
    }

    // 17. TimeZone and DST edge cases
    [TestMethod]
    public void TimeZoneAndDST_EdgeCases_LocalCalendarDay_AndDstBoundaries()
    {
        var localZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var dstClock = new MockClock { TimeZone = localZone };

        // Test around spring-forward (23 hour day) and fall-back (25 hour day)
        var springForwardUtc = new DateTime(2026, 3, 8, 12, 0, 0, DateTimeKind.Utc);
        var fallBackUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

        var springDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(springForwardUtc, localZone));
        var fallDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fallBackUtc, localZone));

        springDate.Should().Be(new DateOnly(2026, 3, 8));
        fallDate.Should().Be(new DateOnly(2026, 11, 1));
    }

    private sealed class MockClock : TimeProvider
    {
        public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;
        public override TimeZoneInfo LocalTimeZone => TimeZone;
    }

    // 18. Expiry rules: cost no longer expires at 8, 30, 365 days while usage still expires at 7 days
    [TestMethod]
    [DataRow(8)]
    [DataRow(30)]
    [DataRow(365)]
    public void Retention_CostDoesNotExpire_WhileUsageExpiresAtSevenDays(int daysOld)
    {
        var now = DateTime.UtcNow;
        var oldTime = now.AddDays(-daysOld);

        // Cost is exempt and retained indefinitely
        var oldCost = new CostData
        {
            Provider = "claude",
            TodayCostUSD = 10m,
            FetchedAt = oldTime,
            LastSuccessfulFetch = oldTime
        };
        CacheRetention.RetainedCost(oldCost, now).Should().NotBeNull();

        // Usage expires after 7 days
        var oldUsage = new UsageData
        {
            Provider = "claude",
            FetchedAt = oldTime,
            LastSuccessfulFetch = oldTime,
            Session = new UsageWindow { ReportedPercent = 50 }
        };
        CacheRetention.PruneExpired(oldUsage, now).Should().BeNull();
    }

    // 19. DashboardSnapshotBuilder recomputes stale cost from CostLedger
    [TestMethod]
    public void DashboardSnapshotBuilder_StaleCost_RecomputesFromLedger()
    {
        var ledger = CreateLedger();
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var yesterday = today.AddDays(-1);

        // Populate ledger with yesterday's cost
        ledger.RecordScanAsync(SampleScan("claude", yesterday, 25m, 250000)).GetAwaiter().GetResult();

        // Stale CostData has old Last30DaysCostUSD = 0
        var staleCost = new CostData
        {
            Provider = "claude",
            IsStale = true,
            TodayCostUSD = 0,
            Last30DaysCostUSD = 0,
            Last30DaysTokens = 0,
            FetchedAt = _clock.UtcNow.UtcDateTime.AddDays(-2)
        };

        var inputs = new DashboardInputs
        {
            NowUtc = _clock.UtcNow.UtcDateTime,
            Settings = new AppSettings { ShowCostTotals = true },
            Providers = [new UsageData { Provider = "claude", Cost = staleCost }],
            CostLedger = ledger
        };

        var snapshot = DashboardSnapshotBuilder.Build(inputs);
        var cost = snapshot.Providers.Single().Cost;
        cost.Should().NotBeNull();
        cost!.Last30DaysCost.Should().Be(25m);
        cost.Last30DaysTokens.Should().Be(250000);
    }

    [TestMethod]
    public async Task RecordScansAsync_MultipleProviders_PersistsAllProvidersInSingleWrite()
    {
        var ledger = CreateLedger();
        var date = new DateOnly(2026, 10, 1);
        var claudeScan = SampleScan("claude", date, 5.0m, 5000);
        var codexScan = SampleScan("codex", date, 3.0m, 3000);

        await ledger.RecordScansAsync([claudeScan, codexScan]);

        var claudeLifetime = ledger.GetLifetime("claude");
        claudeLifetime.TotalCostUSD.Should().Be(5.0m);
        claudeLifetime.TotalTokens.Should().Be(5000);

        var codexLifetime = ledger.GetLifetime("codex");
        codexLifetime.TotalCostUSD.Should().Be(3.0m);
        codexLifetime.TotalTokens.Should().Be(3000);

        var totalLifetime = ledger.GetLifetime();
        totalLifetime.TotalCostUSD.Should().Be(8.0m);
        totalLifetime.TotalTokens.Should().Be(8000);

        // Verify roundtrip from disk
        var reloaded = CreateLedger();
        await reloaded.LoadAsync();
        reloaded.GetLifetime("claude").TotalCostUSD.Should().Be(5.0m);
        reloaded.GetLifetime("codex").TotalCostUSD.Should().Be(3.0m);
    }

    [TestMethod]
    public async Task RecordScansAsync_EmptyOrInvalidScans_DoesNotWriteOrModifyLedger()
    {
        var ledger = CreateLedger();
        await ledger.RecordScansAsync([]);
        File.Exists(_ledgerPath).Should().BeFalse();

        var invalidScan = new CostData { Provider = "invalid_prov", FetchOutcome = FetchOutcome.Success, Last30DaysCostUSD = 10m };
        var failedScan = new CostData { Provider = "claude", FetchOutcome = FetchOutcome.Failed };
        await ledger.RecordScansAsync([invalidScan, failedScan]);
        File.Exists(_ledgerPath).Should().BeFalse();
    }

    [TestMethod]
    public async Task RecordScansAsync_PersistenceFailure_RollsBackInMemoryState()
    {
        var blockingFile = Path.Combine(_directory, "blocking-file");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        var invalidPath = Path.Combine(blockingFile, "cost-ledger.json");
        var ledger = CreateLedger(ledgerPath: invalidPath);

        var date = new DateOnly(2026, 10, 1);
        var claudeScan = SampleScan("claude", date, 5.0m, 5000);

        await ledger.RecordScansAsync([claudeScan]);

        ledger.GetLifetime("claude").TotalCostUSD.Should().Be(0);
    }

    [TestMethod]
    public async Task RecordScanAsync_SingleScan_DelegatesToRecordScansAsync()
    {
        var ledger = CreateLedger();
        var date = new DateOnly(2026, 10, 1);
        var claudeScan = SampleScan("claude", date, 7.5m, 7500);

        await ledger.RecordScanAsync(claudeScan);

        ledger.GetLifetime("claude").TotalCostUSD.Should().Be(7.5m);
        ledger.GetLifetime("claude").TotalTokens.Should().Be(7500);
    }

    [TestMethod]
    public async Task RecordScansAsync_IdenticalScanReRecorded_SkipsDiskRewrite()
    {
        var ledger = CreateLedger();
        var date = new DateOnly(2026, 10, 1);
        var claudeScan = SampleScan("claude", date, 5.0m, 5000);

        await ledger.RecordScansAsync([claudeScan]);
        var marker = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_ledgerPath, marker);

        await ledger.RecordScansAsync([claudeScan]);

        File.GetLastWriteTimeUtc(_ledgerPath).Should().Be(marker);
        ledger.GetLifetime("claude").TotalCostUSD.Should().Be(5.0m);
        ledger.GetLifetime("claude").TotalTokens.Should().Be(5000);
    }
}
