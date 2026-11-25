using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class EarlyResetStoreTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset CurrentTime { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => CurrentTime;
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _workItems = new();
        private readonly object _lock = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_lock) _workItems.Enqueue((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            d(state);
        }

        public void RunPending()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_lock)
                {
                    if (_workItems.Count == 0) break;
                    item = _workItems.Dequeue();
                }
                item.Callback(item.State);
            }
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private MockSettingsService _settings = null!;
    private FixedClock _clock = null!;
    private readonly List<EarlyResetStore> _stores = [];

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-early-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settings = new MockSettingsService();
        _clock = new FixedClock(Now);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var store in _stores) await store.FlushAsync();
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private EarlyResetStore CreateStore()
    {
        var store = new EarlyResetStore(_settings, NullLogger<EarlyResetStore>.Instance, _clock, Path.Combine(_directory, "early-resets.json"));
        _stores.Add(store);
        return store;
    }

    private static EarlyReset MakeEvent(
        string provider = "codex",
        string window = "session",
        double prev = 80,
        double cur = 10,
        DateTime? prevReset = null,
        DateTime? detectedAt = null) =>
        new(
            Provider: provider,
            WindowKey: window,
            WindowTitle: "Session",
            PreviousPercent: prev,
            CurrentPercent: cur,
            PreviousResetAtUtc: prevReset ?? Now.UtcDateTime.AddHours(2),
            NewResetAtUtc: Now.UtcDateTime.AddHours(5),
            DetectedAtUtc: detectedAt ?? Now.UtcDateTime);

    [TestMethod]
    public async Task Persistence_RoundTrip_PreservesEventsAcrossInstances()
    {
        var store1 = CreateStore();
        var ev = MakeEvent("codex", "session", 79, 3, Now.UtcDateTime.AddHours(2), Now.UtcDateTime);

        store1.Record(ev).Should().BeTrue();
        store1.MarkNotified(ev).Should().BeTrue();
        await store1.SaveAsync();

        var store2 = CreateStore();
        await store2.LoadAsync();

        var active = store2.GetActive(Now.UtcDateTime);
        active.Should().ContainSingle();
        var loaded = active[0];
        loaded.Provider.Should().Be("codex");
        loaded.WindowKey.Should().Be("session");
        loaded.PreviousPercent.Should().Be(79);
        loaded.CurrentPercent.Should().Be(3);
        loaded.PreviousResetAtUtc.Should().Be(Now.UtcDateTime.AddHours(2));

        store2.IsNotified(ev).Should().BeTrue();
        File.Exists(store1.FilePath + ".tmp").Should().BeFalse();
    }

    [TestMethod]
    public async Task Dedupe_SameEventRecordedOnlyOnce_EvenAcrossRestarts()
    {
        var store1 = CreateStore();
        var resetAt = Now.UtcDateTime.AddHours(3);
        var ev = MakeEvent("claude", "session", 85, 10, resetAt);

        store1.Record(ev).Should().BeTrue();
        // Same event again in same session
        store1.Record(ev).Should().BeFalse();
        await store1.SaveAsync();

        // Simulate app restart
        var store2 = CreateStore();
        await store2.LoadAsync();

        // Same provider, window, and previousResetAt
        var replay = MakeEvent("claude", "session", 80, 5, resetAt, Now.UtcDateTime.AddMinutes(5));
        store2.Record(replay).Should().BeFalse();
    }

    [TestMethod]
    public void Expiry_GetActiveFiltersOver24Hours_PruneDropsOver7Days()
    {
        var store = CreateStore();

        // 1. Event from 2 hours ago (active < 24h)
        var recent = MakeEvent("codex", "session", 70, 10, Now.UtcDateTime.AddHours(3), Now.UtcDateTime.AddHours(-2));
        // 2. Event from 30 hours ago (> 24h, but < 7d)
        var older = MakeEvent("codex", "weekly", 80, 20, Now.UtcDateTime.AddDays(2), Now.UtcDateTime.AddHours(-30));
        // 3. Event from 8 days ago (> 7d)
        var expired = MakeEvent("claude", "session", 90, 5, Now.UtcDateTime.AddDays(1), Now.UtcDateTime.AddDays(-8));

        store.Record(recent).Should().BeTrue();
        store.Record(older).Should().BeTrue();
        store.Record(expired).Should().BeTrue();

        // GetActive only returns events within 24 hours
        var active = store.GetActive(Now.UtcDateTime);
        active.Should().ContainSingle();
        active[0].WindowKey.Should().Be("session");

        // GetRecent for weekly since 2 days ago includes older
        store.GetRecent("codex", "weekly", Now.UtcDateTime.AddDays(-2)).Should().ContainSingle();

        // Expired (8 days ago) was pruned immediately during Record
        store.GetRecent("claude", "session", Now.UtcDateTime.AddDays(-10)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task CorruptFile_RenamedAsideAndLoadsEmptyWithoutThrowing()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(store.FilePath, "{ invalid json content !!!");

        // Must not throw
        await store.LoadAsync();

        store.GetActive(Now.UtcDateTime).Should().BeEmpty();

        // Corrupt file was renamed aside
        var files = Directory.GetFiles(_directory, "early-resets.json.corrupt.*");
        files.Should().ContainSingle();
    }

    [TestMethod]
    public void BoundedSize_KeepsMaximum50Events()
    {
        var store = CreateStore();

        for (int i = 0; i < 60; i++)
        {
            var ev = MakeEvent(
                provider: "codex",
                window: $"win-{i}",
                prev: 80,
                cur: 10,
                prevReset: Now.UtcDateTime.AddHours(i + 1),
                detectedAt: Now.UtcDateTime.AddMinutes(i));
            store.Record(ev).Should().BeTrue();
        }

        var active = store.GetActive(Now.UtcDateTime.AddMinutes(60));
        active.Count.Should().Be(50);
        // Oldest 10 (win-0 through win-9) were pruned, win-59 is newest
        active.Select(e => e.WindowKey).Should().Contain("win-59");
        active.Select(e => e.WindowKey).Should().NotContain("win-0");
    }

    [TestMethod]
    public void ConcurrentRecord_MultipleThreads_ThreadSafeAndConsistent()
    {
        var store = CreateStore();
        var threads = new List<Thread>();
        var recordedCount = 0;

        for (int i = 0; i < 20; i++)
        {
            var index = i;
            var thread = new Thread(() =>
            {
                var ev = MakeEvent("codex", $"win-{index}", 80, 10, Now.UtcDateTime.AddHours(index + 1));
                if (store.Record(ev))
                {
                    Interlocked.Increment(ref recordedCount);
                }
            });
            threads.Add(thread);
            thread.Start();
        }

        foreach (var t in threads) t.Join();

        recordedCount.Should().Be(20);
        store.GetActive(Now.UtcDateTime).Count.Should().Be(20);
    }

    [TestMethod]
    public void SingleThreadSynchronizationContext_QueriesNeverDeadlockOrBlock()
    {
        var store = CreateStore();
        store.Record(MakeEvent("codex", "session", 70, 10));

        var context = new SingleThreadSynchronizationContext();
        var prevContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);

            // Synchronous in-memory queries from the synchronization context must return immediately
            var active = store.GetActive(Now.UtcDateTime);
            active.Should().ContainSingle();

            var recent = store.GetRecent("codex", "session", Now.UtcDateTime.AddHours(-1));
            recent.Should().ContainSingle();

            store.Record(MakeEvent("claude", "weekly", 80, 10, Now.UtcDateTime.AddHours(5))).Should().BeTrue();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevContext);
        }
    }

    private sealed class PersistenceClock(int callerThread) : TimeProvider
    {
        public int CallerReads;
        public int BackgroundReads;
        public override DateTimeOffset GetUtcNow()
        {
            if (Environment.CurrentManagedThreadId == callerThread) Interlocked.Increment(ref CallerReads);
            else Interlocked.Increment(ref BackgroundReads);
            return Now;
        }
    }

    [TestMethod]
    public void RecordThenFlush_SingleThreadContextWritesOnPoolWithoutDeadlock()
    {
        var clock = new PersistenceClock(Environment.CurrentManagedThreadId);
        var store = new EarlyResetStore(_settings, NullLogger<EarlyResetStore>.Instance, clock, Path.Combine(_directory, "early-resets.json"));
        _stores.Add(store);
        var context = new SingleThreadSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            store.Record(MakeEvent()).Should().BeTrue();
            var flush = store.FlushAsync();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!flush.IsCompleted && DateTime.UtcNow < deadline)
            {
                context.RunPending();
                Thread.Sleep(1);
            }
            flush.IsCompletedSuccessfully.Should().BeTrue();
            // Record reads the clock once for its in-memory update. Save reads it BEFORE
            // serialization and Directory.CreateDirectory, which must happen on a pool thread.
            clock.CallerReads.Should().Be(1);
            clock.BackgroundReads.Should().BeGreaterThan(0);
            File.Exists(store.FilePath).Should().BeTrue();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    [TestMethod]
    public async Task RecordThenFlush_EventIsDurableAndRestartRejectsDuplicate()
    {
        var store = CreateStore();
        var reset = MakeEvent();
        store.Record(reset).Should().BeTrue();
        await store.FlushAsync();
        var restarted = CreateStore();
        await restarted.LoadAsync();
        restarted.GetActive(Now.UtcDateTime).Should().ContainSingle();
        restarted.Record(reset).Should().BeFalse();
    }

    [TestMethod]
    public async Task LoadAsync_EmptyOrCorruptFile_RenamesCorruptWithoutThrowing()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, "");

        await store.LoadAsync();

        store.GetActive(Now.UtcDateTime).Should().BeEmpty();
        File.Exists(store.FilePath).Should().BeFalse();
    }
}
