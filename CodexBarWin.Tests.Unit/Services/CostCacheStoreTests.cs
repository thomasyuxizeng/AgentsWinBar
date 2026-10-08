using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class CostCacheStoreTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 6, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private MockSettingsService _settings = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settings = new MockSettingsService();
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private CostCacheStore Store() =>
        new(_settings, NullLogger<CostCacheStore>.Instance, new FixedClock(Now), Path.Combine(_directory, "cost-cache.json"));

    private static CostData Cost(string provider, long tokens, DateTimeOffset fetchedAt) => new()
    {
        Provider = provider, TodayCostUSD = 1.5m, Last30DaysCostUSD = 280.04m, Last30DaysTokens = tokens,
        FetchedAt = fetchedAt.UtcDateTime,
        Daily = [new DailyCost { Date = new DateOnly(2026, 10, 8), TotalCost = 1.5m, TotalTokens = tokens,
            ModelBreakdowns = [new ModelCost { ModelName = "claude-opus", TotalTokens = tokens, Cost = 1.5m }] }]
    };

    [TestMethod]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsFullResult()
    {
        var store = Store();
        (await store.SaveAsync(new Dictionary<string, CostData> { ["claude"] = Cost("claude", 790_206_937, Now.AddMinutes(-5)) }))
            .Should().BeTrue();

        var loaded = await Store().LoadAsync();

        var claude = loaded.Should().ContainSingle().Which.Value;
        claude.Last30DaysTokens.Should().Be(790_206_937);
        claude.Last30DaysCostUSD.Should().Be(280.04m);
        claude.TodayCostUSD.Should().Be(1.5m);
        claude.Daily.Should().ContainSingle().Which.ModelBreakdowns.Should().ContainSingle()
            .Which.ModelName.Should().Be("claude-opus");
        File.Exists(store.FilePath + ".tmp").Should().BeFalse();
    }

    [TestMethod]
    public async Task LoadAsync_DropsOldFutureInvalidAndEmptyEntries()
    {
        await Store().SaveAsync(new Dictionary<string, CostData>
        {
            ["codex"] = Cost("codex", 10, Now.AddDays(-6)),
            ["claude"] = Cost("claude", 10, Now.AddDays(-8)),
            ["gemini"] = Cost("gemini", 10, Now.AddHours(1)),
            ["nope; rm -rf /"] = Cost("claude", 10, Now),
        });

        var loaded = await Store().LoadAsync();

        loaded.Keys.Should().Equal("codex");
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("[]")]
    [DataRow("{\"items\":{\"claude\":null}}")]
    public async Task LoadAsync_CorruptFile_ReturnsEmpty(string content)
    {
        var store = Store();
        await File.WriteAllTextAsync(store.FilePath, content);
        (await store.LoadAsync()).Should().BeEmpty();
    }

    [TestMethod]
    public async Task SaveAsync_SkipsEntriesWithoutData()
    {
        var store = Store();
        await store.SaveAsync(new Dictionary<string, CostData>
        {
            ["claude"] = new CostData { Provider = "claude", FetchedAt = Now.UtcDateTime },
            ["codex"] = Cost("codex", 5, Now)
        });
        (await store.LoadAsync()).Keys.Should().Equal("codex");
    }

    [TestMethod]
    public async Task SaveAsync_TargetIsDirectory_ReturnsFalseAndAllowsRetryAfterRepair()
    {
        var store = Store();
        Directory.CreateDirectory(store.FilePath);
        var costs = new Dictionary<string, CostData> { ["claude"] = Cost("claude", 5, Now) };

        (await store.SaveAsync(costs)).Should().BeFalse();
        Directory.Delete(store.FilePath);
        (await store.SaveAsync(costs)).Should().BeTrue();
        (await store.LoadAsync()).Should().ContainSingle().Which.Value.Last30DaysTokens.Should().Be(5);
    }

    [TestMethod]
    public async Task DeveloperMode_NeitherReadsNorWritesTheFile()
    {
        var store = Store();
        await store.SaveAsync(new Dictionary<string, CostData> { ["codex"] = Cost("codex", 5, Now) });
        _settings.Settings.DeveloperModeEnabled = true;

        (await store.LoadAsync()).Should().BeEmpty();
        (await store.SaveAsync(new Dictionary<string, CostData> { ["claude"] = Cost("claude", 9, Now) }))
            .Should().BeFalse();

        _settings.Settings.DeveloperModeEnabled = false;
        (await store.LoadAsync()).Keys.Should().Equal("codex");
    }
}
