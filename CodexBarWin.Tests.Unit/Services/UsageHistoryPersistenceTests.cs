using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class UsageHistoryPersistenceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private UsageHistoryPersistence Create(UsageHistoryStore store, Clock clock, string? folder = null)
    {
        // Leave scratch directories: this environment prohibits deleting TEMP paths.
        folder ??= Path.Combine(Path.GetTempPath(), "CodexBarWin-HistoryTests", Guid.NewGuid().ToString("N"));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.SettingsFilePath).Returns(Path.Combine(folder, "settings.json"));
        return new(store, settings.Object, NullLogger<UsageHistoryPersistence>.Instance, clock);
    }

    private static void Add(UsageHistoryStore store, Clock clock, double percent) =>
        store.Add(new UsageHistorySample { Timestamp = clock.Now.UtcDateTime, Provider = "claude", WindowKey = "session", UsedPercent = percent }, clock.Now.UtcDateTime);

    [TestMethod]
    public async Task SaveAsync_WhenChanged_RoundTripsAtomicallyBesideSettings()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); Add(store, clock, 10);
        var persistence = Create(store, clock);
        await persistence.LoadAsync();
        await persistence.SaveAsync();
        File.Exists(persistence.FilePath).Should().BeTrue();
        File.Exists(persistence.FilePath + ".tmp").Should().BeFalse();
        var loadedStore = new UsageHistoryStore();
        await Create(loadedStore, clock, Path.GetDirectoryName(persistence.FilePath)).LoadAsync();
        loadedStore.Samples.Should().Equal(store.Samples);
    }

    [TestMethod]
    public async Task SaveAsync_WhenThrottled_PreservesPreviousFileAndFlushesLatestAfterMinuteOrOnExit()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); Add(store, clock, 10);
        var persistence = Create(store, clock); await persistence.LoadAsync(); await persistence.SaveAsync();
        var original = await File.ReadAllTextAsync(persistence.FilePath);
        clock.Now += TimeSpan.FromSeconds(30); Add(store, clock, 20); await persistence.SaveAsync();
        (await File.ReadAllTextAsync(persistence.FilePath)).Should().Be(original);
        await persistence.SaveAsync(force: true);
        JsonSerializer.Deserialize(await File.ReadAllTextAsync(persistence.FilePath), AppJsonSerializerContext.Default.ListUsageHistorySample)!.Count.Should().Be(2);
        clock.Now += TimeSpan.FromMinutes(1); Add(store, clock, 30); await persistence.SaveAsync();
        JsonSerializer.Deserialize(await File.ReadAllTextAsync(persistence.FilePath), AppJsonSerializerContext.Default.ListUsageHistorySample)!.Count.Should().Be(3);
    }

    [TestMethod]
    public async Task LoadAsync_WhenMissingCorruptOrExpired_DegradesQuietlyAndPrunes()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); var persistence = Create(store, clock);
        await persistence.LoadAsync(); store.Samples.Should().BeEmpty();
        Directory.CreateDirectory(Path.GetDirectoryName(persistence.FilePath)!);
        await File.WriteAllTextAsync(persistence.FilePath, "not JSON");
        await persistence.LoadAsync(); store.Samples.Should().BeEmpty();
        Add(store, clock, 10); await persistence.SaveAsync(force: true);
        clock.Now += TimeSpan.FromDays(15);
        var loaded = new UsageHistoryStore();
        await Create(loaded, clock, Path.GetDirectoryName(persistence.FilePath)).LoadAsync();
        loaded.Samples.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SaveAsync_WhenFilesystemFailsOrCancelled_DoesNotThrowOrLoseMemory()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); Add(store, clock, 10);
        var persistence = Create(store, clock);
        await persistence.LoadAsync();
        Directory.CreateDirectory(persistence.FilePath); // The target is a directory, so atomic replace fails.
        await persistence.SaveAsync(); store.Samples.Should().ContainSingle();
        var cts = new CancellationTokenSource(); cts.Cancel();
        await persistence.LoadAsync(cts.Token); await persistence.SaveAsync(ct: cts.Token);
        store.Samples.Should().ContainSingle();
    }

    [TestMethod]
    public async Task SaveAsync_WhenNotLoaded_DoesNotWriteOrOverwriteExistingHistoryFile()
    {
        var clock = new Clock(); var store = new UsageHistoryStore();
        var persistence = Create(store, clock);
        Directory.CreateDirectory(Path.GetDirectoryName(persistence.FilePath)!);
        var original = "[{\"provider\":\"claude\",\"windowKey\":\"session\",\"usedPercent\":42.0}]";
        await File.WriteAllTextAsync(persistence.FilePath, original);
        Add(store, clock, 99);

        persistence.IsLoaded.Should().BeFalse();
        await persistence.SaveAsync();
        await persistence.SaveAsync(force: true);

        persistence.IsLoaded.Should().BeFalse();
        (await File.ReadAllTextAsync(persistence.FilePath)).Should().Be(original);
    }

    [TestMethod]
    public async Task LoadAsync_WhenCalled_SetsIsLoadedAndAllowsSubsequentSaves()
    {
        var clock = new Clock(); var store = new UsageHistoryStore();
        var persistence = Create(store, clock);
        persistence.IsLoaded.Should().BeFalse();

        await persistence.LoadAsync();
        persistence.IsLoaded.Should().BeTrue();

        Add(store, clock, 50);
        await persistence.SaveAsync();
        File.Exists(persistence.FilePath).Should().BeTrue();
        (await File.ReadAllTextAsync(persistence.FilePath)).Should().Contain("50");
    }

    private static List<UsageHistorySample> ReadWithArrayLoader(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonSerializerContext.Default.ListUsageHistorySample)!;

    [TestMethod]
    public async Task SaveAsync_WhenFileIsIntact_AppendsNewSamplesAndKeepsExistingBytes()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); Add(store, clock, 10);
        var persistence = Create(store, clock); await persistence.LoadAsync(); await persistence.SaveAsync();
        var original = await File.ReadAllTextAsync(persistence.FilePath);

        clock.Now += TimeSpan.FromMinutes(2); Add(store, clock, 20); await persistence.SaveAsync();
        clock.Now += TimeSpan.FromMinutes(2); Add(store, clock, 30); await persistence.SaveAsync();

        var appended = await File.ReadAllTextAsync(persistence.FilePath);
        appended.Should().StartWith(original[..(original.LastIndexOf('}') + 1)]);
        appended.Split('\n').Should().HaveCount(5, "the array is written one sample per line");
        ReadWithArrayLoader(persistence.FilePath).Select(s => s.UsedPercent).Should().Equal(10, 20, 30);
    }

    [TestMethod]
    public async Task SaveAsync_WhenReloaded_AppendsToTheLoadedFile()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); Add(store, clock, 10);
        var persistence = Create(store, clock); await persistence.LoadAsync(); await persistence.SaveAsync();
        var original = await File.ReadAllTextAsync(persistence.FilePath);

        var reloaded = new UsageHistoryStore();
        var second = Create(reloaded, clock, Path.GetDirectoryName(persistence.FilePath));
        await second.LoadAsync();
        clock.Now += TimeSpan.FromMinutes(2); Add(reloaded, clock, 20); await second.SaveAsync();

        (await File.ReadAllTextAsync(persistence.FilePath)).Should().StartWith(original[..(original.LastIndexOf('}') + 1)]);
        ReadWithArrayLoader(persistence.FilePath).Select(s => s.UsedPercent).Should().Equal(10, 20);
    }

    [TestMethod]
    public async Task SaveAsync_WhenCompactionIntervalPassed_RewritesWithoutExpiredSamples()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); Add(store, clock, 10);
        var persistence = Create(store, clock); await persistence.LoadAsync(); await persistence.SaveAsync();

        clock.Now += TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1); Add(store, clock, 20); await persistence.SaveAsync();

        ReadWithArrayLoader(persistence.FilePath).Select(s => s.UsedPercent).Should().Equal(20);
        File.Exists(persistence.FilePath + ".tmp").Should().BeFalse();
    }

    [TestMethod]
    public async Task SaveAsync_WhenWithinCompactionInterval_AppendsInsteadOfRewriting()
    {
        var clock = new Clock(); var store = new UsageHistoryStore();
        var persistence = Create(store, clock); await persistence.LoadAsync();
        Add(store, clock, 10); await persistence.SaveAsync();
        var original = await File.ReadAllTextAsync(persistence.FilePath);

        clock.Now += TimeSpan.FromHours(23); Add(store, clock, 20); await persistence.SaveAsync();

        (await File.ReadAllTextAsync(persistence.FilePath)).Should().StartWith(original[..(original.LastIndexOf('}') + 1)]);
        ReadWithArrayLoader(persistence.FilePath).Should().HaveCount(2);
    }

    [TestMethod]
    public async Task LoadAsync_WhenTailWasCutOff_KeepsCompleteSamplesAndRewritesOnNextSave()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); Add(store, clock, 10);
        clock.Now += TimeSpan.FromMinutes(2); Add(store, clock, 20);
        var persistence = Create(store, clock); await persistence.LoadAsync(); await persistence.SaveAsync();
        var full = await File.ReadAllTextAsync(persistence.FilePath);
        await File.WriteAllTextAsync(persistence.FilePath, full[..^12]);   // an append interrupted mid-sample

        var loaded = new UsageHistoryStore();
        var second = Create(loaded, clock, Path.GetDirectoryName(persistence.FilePath));
        await second.LoadAsync();
        loaded.Samples.Select(s => s.UsedPercent).Should().Equal(10);

        clock.Now += TimeSpan.FromMinutes(2); Add(loaded, clock, 30); await second.SaveAsync();
        ReadWithArrayLoader(persistence.FilePath).Select(s => s.UsedPercent).Should().Equal(10, 30);
    }

    [TestMethod]
    public async Task LoadAsync_WhenLegacyIndentedFile_ReadsItAndAppendsInArrayLoaderFormat()
    {
        var clock = new Clock();
        var folder = Path.Combine(Path.GetTempPath(), "CodexBarWin-HistoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        IReadOnlyList<UsageHistorySample> legacy =
        [
            new() { Timestamp = clock.Now.UtcDateTime.AddHours(-2), Provider = "claude", WindowKey = "session", UsedPercent = 5 },
            new() { Timestamp = clock.Now.UtcDateTime.AddHours(-1), Provider = "codex", WindowKey = "weekly", UsedPercent = 7 }
        ];
        var legacyJson = JsonSerializer.Serialize(legacy, AppJsonSerializerContext.Default.IReadOnlyListUsageHistorySample);
        await File.WriteAllTextAsync(Path.Combine(folder, "usage-history.json"), legacyJson);

        var store = new UsageHistoryStore(); var persistence = Create(store, clock, folder);
        await persistence.LoadAsync();
        store.Samples.Should().HaveCount(2);
        Add(store, clock, 40); await persistence.SaveAsync();

        var text = await File.ReadAllTextAsync(persistence.FilePath);
        text.Should().StartWith(legacyJson[..legacyJson.LastIndexOf(']')].TrimEnd(), "the legacy file is appended to, not migrated");
        ReadWithArrayLoader(persistence.FilePath).Select(s => s.UsedPercent).Should().Equal(5, 7, 40);
    }

    [TestMethod]
    public async Task SaveAsync_WhenFileNoLongerEndsLikeAnArray_RewritesWholeHistory()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); Add(store, clock, 10);
        var persistence = Create(store, clock); await persistence.LoadAsync(); await persistence.SaveAsync();
        await File.WriteAllTextAsync(persistence.FilePath, "[{\"provider\":\"claude\"");

        clock.Now += TimeSpan.FromMinutes(2); Add(store, clock, 20); await persistence.SaveAsync();

        ReadWithArrayLoader(persistence.FilePath).Select(s => s.UsedPercent).Should().Equal(10, 20);
    }

    [TestMethod]
    public async Task SaveAsync_WhenStoreRestored_RewritesInsteadOfAppending()
    {
        var clock = new Clock(); var store = new UsageHistoryStore(); Add(store, clock, 10);
        var persistence = Create(store, clock); await persistence.LoadAsync(); await persistence.SaveAsync();

        clock.Now += TimeSpan.FromMinutes(2);
        store.Restore([new UsageHistorySample { Timestamp = clock.Now.UtcDateTime, Provider = "codex", WindowKey = "session", UsedPercent = 50 }], clock.Now.UtcDateTime);
        await persistence.SaveAsync();

        ReadWithArrayLoader(persistence.FilePath).Select(s => s.Provider).Should().Equal("codex");
    }

    [TestMethod]
    public void Format_WhenSamples_WritesOneCompactSamplePerLineThatParsesBack()
    {
        var at = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        var samples = new List<UsageHistorySample>
        {
            new() { Timestamp = at, Provider = "claude", WindowKey = "session", UsedPercent = 1.5, ResetsAt = at.AddHours(5) },
            new() { Timestamp = at, Provider = "codex", WindowKey = "weekly", UsedPercent = 2 }
        };

        var text = System.Text.Encoding.UTF8.GetString(UsageHistoryPersistence.Format(samples));

        var lines = text.Split('\n');
        lines.Should().HaveCount(4);
        lines[0].Should().Be("[");
        lines[1].Should().StartWith("{").And.EndWith("},");
        lines[2].Should().StartWith("{").And.EndWith("}");
        lines[3].Should().Be("]");
        UsageHistoryPersistence.Parse(text, out var intact).Should().Equal(samples);
        intact.Should().BeTrue();
        UsageHistoryPersistence.Parse(System.Text.Encoding.UTF8.GetString(UsageHistoryPersistence.Format([])), out _).Should().BeEmpty();
    }

    [TestMethod]
    public void Parse_WhenNotAnArray_ReturnsEmptyAndNotIntact()
    {
        UsageHistoryPersistence.Parse("not JSON", out var intact).Should().BeEmpty();
        intact.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(100, 100, false)]
    [DataRow(150, 100, false)]
    [DataRow(151, 100, true)]
    [DataRow(12000, 10000, true)]
    [DataRow(10900, 10000, false)]
    public void ShouldCompact_WhenDeadWeightComparedWithThreshold_ReturnsExpected(int inFile, int kept, bool expected) =>
        UsageHistoryPersistence.ShouldCompact(inFile, kept).Should().Be(expected);

    [TestMethod]
    public async Task SaveAndLoadAsync_LargeHistoryExceedingChunkThreshold_RoundTripsViaStreamingUtf8()
    {
        var clock = new Clock();
        var store = new UsageHistoryStore();
        var start = clock.Now.UtcDateTime.AddHours(-12);
        for (var i = 0; i < 600; i++)
        {
            store.Add(new UsageHistorySample
            {
                Timestamp = start.AddMinutes(i),
                Provider = i % 2 == 0 ? "claude" : "codex",
                WindowKey = "extra:model-quota",
                UsedPercent = (i % 100) + 0.5
            }, clock.Now.UtcDateTime);
        }

        var persistence = Create(store, clock);
        await persistence.LoadAsync();
        await persistence.SaveAsync(force: true);

        var reloaded = new UsageHistoryStore();
        await Create(reloaded, clock, Path.GetDirectoryName(persistence.FilePath)).LoadAsync();
        reloaded.Count.Should().Be(600);
        reloaded.Samples.Should().Equal(store.Samples);
    }

    [TestMethod]
    public async Task ParseStreamAsync_WithUtf8BomAndCutOffTail_KeepsCompleteSamplesAndMarksNotIntact()
    {
        var at = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        var bytes = UsageHistoryPersistence.Format([
            new() { Timestamp = at, Provider = "claude", WindowKey = "session", UsedPercent = 12.5 },
            new() { Timestamp = at.AddMinutes(1), Provider = "codex", WindowKey = "weekly", UsedPercent = 34.0 }
        ]);
        var cut = bytes[..^10];
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(cut).ToArray();
        using var stream = new MemoryStream(withBom);

        var (samples, intact) = await UsageHistoryPersistence.ParseStreamAsync(stream, CancellationToken.None);

        intact.Should().BeFalse();
        samples.Should().ContainSingle();
        samples[0].Provider.Should().Be("claude");
        samples[0].UsedPercent.Should().Be(12.5);
    }
}
