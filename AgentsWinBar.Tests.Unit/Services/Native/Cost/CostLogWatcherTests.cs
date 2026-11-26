using System.Diagnostics;
using AwesomeAssertions;
using AgentsWinBar.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentsWinBar.Tests.Unit.Services.Native.Cost.CostTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native.Cost;

[TestClass]
public sealed class CostLogWatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    private string _directory = null!;
    private ManualClock _clock = null!;
    private string Claude => Path.Combine(_directory, "claude");
    private string Codex => Path.Combine(_directory, "codex");

    [TestInitialize]
    public void Initialize()
    {
        _directory = TempDirectory("cost-watch");
        Directory.CreateDirectory(Claude);
        Directory.CreateDirectory(Codex);
        _clock = new ManualClock(Start);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private CostLogWatcher Watcher(bool watch = false) =>
        new([new(CostProvider.Claude, Claude), new(CostProvider.Codex, Codex)], NullLogger<CostLogWatcher>.Instance, _clock, watch);

    /// <summary>A watcher whose startup listing has completed.</summary>
    private CostLogWatcher Settled(bool watch = false)
    {
        var watcher = Watcher(watch);
        watcher.Complete(watcher.TakePlan(), succeeded: true);
        return watcher;
    }

    [TestMethod]
    public void TakePlan_WhenFirstCalled_ListsEverything()
    {
        using var watcher = Watcher();

        watcher.TakePlan().FullListing.Should().BeTrue();
    }

    [TestMethod]
    public void TakePlan_WhenNothingChangedSinceTheListing_HasNoWork()
    {
        using var watcher = Settled();

        var plan = watcher.TakePlan();

        plan.FullListing.Should().BeFalse();
        plan.ChangedPaths.Should().BeEmpty();
    }

    [TestMethod]
    public void TakePlan_WhenLogsWereWrittenOrCreated_NamesEachOnce()
    {
        using var watcher = Settled();
        var log = Path.Combine(Claude, "project", "session.jsonl");
        var archive = Path.Combine(Codex, "rollout-a.jsonl.zst");

        watcher.NotifyChanged(CostProvider.Claude, log, created: true);
        watcher.NotifyChanged(CostProvider.Claude, log, created: false);
        watcher.NotifyChanged(CostProvider.Claude, log.ToUpperInvariant(), created: false);
        watcher.NotifyChanged(CostProvider.Codex, archive, created: true);
        var plan = watcher.TakePlan();

        plan.FullListing.Should().BeFalse();
        plan.ChangedPaths.Should().BeEquivalentTo([log, archive]);
        watcher.TakePlan().ChangedPaths.Should().BeEmpty("a plan hands its work out once");
    }

    [TestMethod]
    public void NotifyChanged_WhenWriteIsNotToALog_IsIgnored()
    {
        using var watcher = Settled();

        // A directory's write time changes with every file created in it; that file has its own notification.
        watcher.NotifyChanged(CostProvider.Claude, Path.Combine(Claude, "project"), created: false);
        watcher.NotifyChanged(CostProvider.Claude, Path.Combine(Claude, "project", "notes.txt"), created: false);
        watcher.NotifyChanged(CostProvider.Claude, Path.Combine(Claude, "archive.jsonl.zst"), created: false);

        watcher.TakePlan().ChangedPaths.Should().BeEmpty();
    }

    // Review finding 19: creating or renaming a file that is not a log queues nothing.
    [TestMethod]
    public void NotifyChanged_WhenNonLogFileIsCreatedOrRenamed_IsIgnored()
    {
        using var watcher = Settled();
        var notes = Path.Combine(Claude, "project", "memory.md");
        var temp = Path.Combine(Codex, "session.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(notes)!);
        File.WriteAllText(notes, "synthetic");
        File.WriteAllText(temp, "synthetic");

        watcher.NotifyChanged(CostProvider.Claude, notes, created: true);
        watcher.NotifyRenamed(temp);

        watcher.TakePlan().ChangedPaths.Should().BeEmpty();
    }

    [TestMethod]
    public void NotifyRenamed_WhenFileIsRenamedToALog_QueuesIt()
    {
        using var watcher = Settled();
        var log = Path.Combine(Codex, "rollout-a.jsonl");

        watcher.NotifyRenamed(log);

        watcher.TakePlan().ChangedPaths.Should().BeEquivalentTo([log]);
    }

    [TestMethod]
    public void NotifyChanged_WhenDirectoryIsCreatedOrRenamed_QueuesItForListing()
    {
        using var watcher = Settled();
        var created = Path.Combine(Claude, "moved-in-project");
        var renamed = Path.Combine(Codex, "2026-renamed");
        // The notification arrives once the directory exists.
        Directory.CreateDirectory(created);
        Directory.CreateDirectory(renamed);

        watcher.NotifyChanged(CostProvider.Claude, created, created: true);
        watcher.NotifyRenamed(renamed);

        watcher.TakePlan().ChangedPaths.Should().BeEquivalentTo([created, renamed]);
    }

    [TestMethod]
    public void TakePlan_WhenNotificationsWereLost_ListsEverythingOnce()
    {
        using var watcher = Settled();
        watcher.NotifyChanged(CostProvider.Claude, Path.Combine(Claude, "a.jsonl"), created: false);

        watcher.NotifyLost();
        watcher.NotifyChanged(CostProvider.Claude, Path.Combine(Claude, "b.jsonl"), created: false);
        var plan = watcher.TakePlan();
        watcher.Complete(plan, succeeded: true);

        plan.FullListing.Should().BeTrue();
        plan.ChangedPaths.Should().BeEmpty();
        watcher.TakePlan().FullListing.Should().BeFalse();
    }

    [TestMethod]
    public void TakePlan_WhenTooManyPathsArePending_FallsBackToOneListing()
    {
        using var watcher = Settled();

        for (var i = 0; i <= CostLogWatcher.MaxChangedPaths; i++)
            watcher.NotifyChanged(CostProvider.Claude, Path.Combine(Claude, $"session-{i}.jsonl"), created: true);

        watcher.TakePlan().FullListing.Should().BeTrue();
    }

    [TestMethod]
    public void TakePlan_WhenSafetyIntervalHasPassed_ListsEverythingAgain()
    {
        using var watcher = Settled();

        _clock.Now = Start + CostLogWatcher.FullListingInterval - TimeSpan.FromMinutes(1);
        watcher.TakePlan().FullListing.Should().BeFalse();

        _clock.Now = Start + CostLogWatcher.FullListingInterval;
        var plan = watcher.TakePlan();
        watcher.Complete(plan, succeeded: true);

        plan.FullListing.Should().BeTrue();
        watcher.TakePlan().FullListing.Should().BeFalse("the interval restarts at the completed listing");
    }

    [TestMethod]
    public void Complete_WhenUpdateFailed_KeepsTheWorkForTheNextPlan()
    {
        using var watcher = Settled();
        var log = Path.Combine(Claude, "a.jsonl");
        var later = Path.Combine(Claude, "b.jsonl");
        watcher.NotifyChanged(CostProvider.Claude, log, created: false);

        var failed = watcher.TakePlan();
        watcher.NotifyChanged(CostProvider.Claude, later, created: false);
        watcher.Complete(failed, succeeded: false);

        watcher.TakePlan().ChangedPaths.Should().BeEquivalentTo([log, later]);
    }

    [TestMethod]
    public void Complete_WhenListingFailed_ListsAgain()
    {
        using var watcher = Watcher();

        watcher.Complete(watcher.TakePlan(), succeeded: false);

        watcher.TakePlan().FullListing.Should().BeTrue();
    }

    [TestMethod]
    public void TakePlan_WhenFileIsAppendedUnderAWatchedRoot_NamesThatFile()
    {
        var nested = Path.Combine(Codex, "2026", "11", "25");
        Directory.CreateDirectory(nested);
        var log = Path.Combine(nested, "rollout-a.jsonl");
        File.WriteAllText(log, "{}\n");
        using var watcher = Settled(watch: true);

        File.AppendAllText(log, "{}\n");
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var timer = Stopwatch.StartNew();
        while (!changed.Contains(log) && timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            var plan = watcher.TakePlan();
            plan.FullListing.Should().BeFalse();
            changed.UnionWith(plan.ChangedPaths);
            if (!changed.Contains(log)) Thread.Sleep(50);
        }

        changed.Should().Contain(log);
    }

    [TestMethod]
    public void TakePlan_WhenARootAppearsLater_ListsOnceThenWatchesIt()
    {
        var late = Path.Combine(_directory, "late-root");
        using var watcher = new CostLogWatcher([new(CostProvider.Codex, late)], NullLogger<CostLogWatcher>.Instance, _clock);
        watcher.Complete(watcher.TakePlan(), succeeded: true);
        watcher.TakePlan().FullListing.Should().BeFalse("a missing folder has nothing to list");

        Directory.CreateDirectory(late);
        var afterCreation = watcher.TakePlan();
        watcher.Complete(afterCreation, succeeded: true);

        afterCreation.FullListing.Should().BeTrue("logs written before the watcher started need a listing");
        watcher.TakePlan().FullListing.Should().BeFalse();
    }

    [TestMethod]
    public void Dispose_WhenCalledTwiceOrBeforeUse_DoesNotThrow()
    {
        var watcher = Settled(watch: true);

        watcher.Dispose();
        var again = () => watcher.Dispose();

        again.Should().NotThrow();
        watcher.TakePlan().ChangedPaths.Should().BeEmpty();
    }

    [TestMethod]
    public void ReleaseIfIdle_WhenNoCostRequestForTheIdleTimeout_DisposesWatchersAndListsEverythingNext()
    {
        using var watcher = Settled(watch: true);
        watcher.ActiveWatchers.Should().Be(2);
        watcher.NotifyChanged(CostProvider.Claude, Path.Combine(Claude, "a.jsonl"), created: false);

        _clock.Now = Start + CostLogWatcher.IdleTimeout - TimeSpan.FromSeconds(1);
        watcher.ReleaseIfIdle().Should().BeFalse();
        watcher.ActiveWatchers.Should().Be(2);

        _clock.Now = Start + CostLogWatcher.IdleTimeout;
        watcher.ReleaseIfIdle().Should().BeTrue();
        watcher.ActiveWatchers.Should().Be(0);

        var plan = watcher.TakePlan();
        plan.FullListing.Should().BeTrue("nothing watched the logs while the view was closed");
        plan.ChangedPaths.Should().BeEmpty();
        watcher.ActiveWatchers.Should().Be(2, "the next request watches again");
        watcher.Complete(plan, succeeded: true);
        watcher.TakePlan().FullListing.Should().BeFalse();
    }

    [TestMethod]
    public void ReleaseIfIdle_WhenACostRequestWasRecent_KeepsTheWatchers()
    {
        using var watcher = Settled(watch: true);
        _clock.Now = Start + TimeSpan.FromMinutes(10);
        watcher.TakePlan();

        _clock.Now = Start + TimeSpan.FromMinutes(20);
        watcher.ReleaseIfIdle().Should().BeFalse("the last request was 10 minutes ago");
        watcher.ActiveWatchers.Should().Be(2);

        _clock.Now = Start + TimeSpan.FromMinutes(25);
        watcher.ReleaseIfIdle().Should().BeTrue();
        watcher.ActiveWatchers.Should().Be(0);
    }

    [TestMethod]
    public void ReleaseIfIdle_WhenCalledAgainOrAfterDispose_DoesNothing()
    {
        var watcher = Settled(watch: true);
        _clock.Now = Start + CostLogWatcher.IdleTimeout;
        watcher.ReleaseIfIdle().Should().BeTrue();

        watcher.ReleaseIfIdle().Should().BeFalse("there is nothing left to release");
        watcher.Dispose();
        watcher.ReleaseIfIdle().Should().BeFalse();
    }

    [TestMethod]
    public void IdleTimer_WhenWatching_ChecksPeriodicallyAndStopsOnceReleased()
    {
        var clock = new TimerClock(Start);
        using var watcher = new CostLogWatcher([new(CostProvider.Claude, Claude), new(CostProvider.Codex, Codex)],
            NullLogger<CostLogWatcher>.Instance, clock, watch: true);
        watcher.Complete(watcher.TakePlan(), succeeded: true);
        var first = clock.Timers.Should().ContainSingle().Subject;
        first.Period.Should().Be(CostLogWatcher.IdleCheckInterval);

        clock.Now = Start + CostLogWatcher.IdleTimeout;
        first.Fire();

        watcher.ActiveWatchers.Should().Be(0);
        first.Disposed.Should().BeTrue("an idle watcher needs no timer either");
        watcher.TakePlan().FullListing.Should().BeTrue();
        clock.Timers.Should().HaveCount(2, "watching again starts a new timer");
    }

    [TestMethod]
    public void TakePlan_WhenNotWatching_StartsNoTimer()
    {
        var clock = new TimerClock(Start);
        using var watcher = new CostLogWatcher([new(CostProvider.Claude, Claude)], NullLogger<CostLogWatcher>.Instance, clock, watch: false);

        watcher.TakePlan();

        clock.Timers.Should().BeEmpty();
    }

    private sealed class TimerClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        internal List<FakeTimer> Timers { get; } = [];
        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state, period);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        internal TimeSpan Period { get; } = period;
        internal bool Disposed { get; private set; }
        internal void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => true;
        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
