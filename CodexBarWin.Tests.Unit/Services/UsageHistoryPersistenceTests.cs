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
        var persistence = Create(store, clock); await persistence.SaveAsync();
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
        Directory.CreateDirectory(persistence.FilePath); // The target is a directory, so atomic replace fails.
        await persistence.SaveAsync(); store.Samples.Should().ContainSingle();
        var cts = new CancellationTokenSource(); cts.Cancel();
        await persistence.LoadAsync(cts.Token); await persistence.SaveAsync(ct: cts.Token);
        store.Samples.Should().ContainSingle();
    }
}
