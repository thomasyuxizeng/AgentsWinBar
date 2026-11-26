using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Services.Native;
using AgentsWinBar.Services.Native.Cost;
using static AgentsWinBar.Tests.Unit.Services.Native.NativeTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native;

/// <summary>The timestamp bookkeeping only: no test starts an OS watcher or touches a real log folder.</summary>
[TestClass]
public class ProviderActivityTrackerTests
{
    private readonly TestClock _clock = new(Now);
    private readonly List<string> _started = [];
    private ProviderActivityTracker _tracker = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tracker = new ProviderActivityTracker(
            NativeCostService.DefaultRoots(Path.Combine(Path.GetTempPath(), "AgentsWinBar_NoSuchProfile_" + Guid.NewGuid())),
            new ListLogger<ProviderActivityTracker>(), _clock, watch: false);
        _tracker.ActivityStarted += (_, provider) => _started.Add(provider);
    }

    [TestCleanup]
    public void Cleanup() => _tracker.Dispose();

    [TestMethod]
    public void LastActivityUtc_NothingSeen_IsNull()
    {
        _tracker.LastActivityUtc("claude").Should().BeNull();
        _tracker.LastActivityUtc("codex").Should().BeNull();
        _tracker.LastActivityUtc("gemini").Should().BeNull();
    }

    [TestMethod]
    [DataRow(CostProvider.Claude, "claude")]
    [DataRow(CostProvider.Codex, "codex")]
    [DataRow(CostProvider.Gemini, "gemini")]
    public void NotifyActivity_RecordsClockTimeForThatProviderOnly(CostProvider provider, string id)
    {
        _tracker.NotifyActivity(provider);

        _tracker.LastActivityUtc(id).Should().Be(Now.UtcDateTime);
        _tracker.LastActivityUtc(id)!.Value.Kind.Should().Be(DateTimeKind.Utc);
        foreach (var other in new[] { "claude", "codex", "gemini" }.Where(p => p != id))
            _tracker.LastActivityUtc(other).Should().BeNull();
    }

    [TestMethod]
    public void NotifyActivity_LaterWrite_MovesTheStamp()
    {
        _tracker.NotifyActivity(CostProvider.Codex);
        _clock.Now = Now.AddMinutes(3);
        _tracker.NotifyActivity(CostProvider.Codex);

        _tracker.LastActivityUtc("codex").Should().Be(Now.AddMinutes(3).UtcDateTime);
    }

    [TestMethod]
    public void LastActivityUtc_ProviderNameInAnyCase_IsNormalized()
    {
        _tracker.NotifyActivity(CostProvider.Claude);

        _tracker.LastActivityUtc(" Claude ").Should().Be(Now.UtcDateTime);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("openai")]
    [DataRow("../claude")]
    public void LastActivityUtc_UnknownProvider_IsNull(string provider)
    {
        _tracker.NotifyActivity(CostProvider.Claude);

        _tracker.LastActivityUtc(provider).Should().BeNull();
    }

    [TestMethod]
    public void ActivityStarted_RaisedOnFirstWriteAndAfterAQuietWindowOnly()
    {
        _tracker.NotifyActivity(CostProvider.Claude);
        _clock.Now = Now.AddMinutes(1);
        _tracker.NotifyActivity(CostProvider.Claude);
        _clock.Now = Now.AddMinutes(1) + ProviderRefreshPlanner.ActiveWindow;
        _tracker.NotifyActivity(CostProvider.Claude);
        _clock.Now += ProviderRefreshPlanner.ActiveWindow + TimeSpan.FromSeconds(1);
        _tracker.NotifyActivity(CostProvider.Claude);
        _tracker.NotifyActivity(CostProvider.Codex);

        _started.Should().Equal("claude", "claude", "codex");
    }

    [TestMethod]
    public void ActivityStarted_HandlerThrows_StillRecordsTheStamp()
    {
        _tracker.ActivityStarted += (_, _) => throw new InvalidOperationException("handler bug");

        _tracker.NotifyActivity(CostProvider.Gemini);

        _tracker.LastActivityUtc("gemini").Should().Be(Now.UtcDateTime);
    }

    [TestMethod]
    public void LastActivityUtc_ClockSetBack_CountsAsJustNowAndExpiresOneWindowLater()
    {
        _tracker.NotifyActivity(CostProvider.Codex);
        _clock.Now = Now.AddHours(-2);

        var healed = _tracker.LastActivityUtc("codex");

        healed.Should().Be(Now.AddHours(-2).UtcDateTime);
        _clock.Now = Now.AddHours(-2) + ProviderRefreshPlanner.ActiveWindow + TimeSpan.FromSeconds(1);
        ProviderRefreshPlanner.IsActive(_tracker.LastActivityUtc("codex"), _clock.Now.UtcDateTime).Should().BeFalse();
    }

    [TestMethod]
    public void NotifyActivity_AfterClockSetBack_AnnouncesAgain()
    {
        _tracker.NotifyActivity(CostProvider.Codex);
        _clock.Now = Now.AddHours(-1);
        _tracker.NotifyActivity(CostProvider.Codex);

        _started.Should().Equal("codex", "codex");
        _tracker.LastActivityUtc("codex").Should().Be(Now.AddHours(-1).UtcDateTime);
    }

    [TestMethod]
    public void NotifyActivity_OlderTimeFromARacingCallback_KeepsTheNewerStampAndAnnouncesNothing()
    {
        _tracker.NotifyActivity(CostProvider.Codex);
        _clock.Now = Now.AddMinutes(3);
        _tracker.NotifyActivity(CostProvider.Codex);
        // A callback that read the clock 50 ms before the one above and finishes after it
        _clock.Now = Now.AddMinutes(3).AddMilliseconds(-50);
        _tracker.NotifyActivity(CostProvider.Codex);

        _started.Should().Equal("codex");
        _tracker.LastActivityUtc("codex").Should().Be(Now.AddMinutes(3).UtcDateTime);
    }

    [TestMethod]
    public void NotifyActivity_ConcurrentCallbacks_AnnounceOnce()
    {
        var announced = 0;
        using var tracker = new ProviderActivityTracker([], new ListLogger<ProviderActivityTracker>(), TimeProvider.System, watch: false);
        tracker.ActivityStarted += (_, _) => Interlocked.Increment(ref announced);

        Parallel.For(0, 50_000, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) },
            _ => tracker.NotifyActivity(CostProvider.Codex));

        announced.Should().Be(1);
    }

    [TestMethod]
    public void NotifyActivity_AfterDispose_RaisesNothing()
    {
        _tracker.Dispose();

        _tracker.NotifyActivity(CostProvider.Claude);

        _started.Should().BeEmpty();
    }

    [TestMethod]
    public void EnsureWatching_FoldersExist_WatchesEachUntilDisposed()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentsWinBar_ActivityWatch_" + Guid.NewGuid());
        try
        {
            var tracker = new ProviderActivityTracker(Roots(root), new ListLogger<ProviderActivityTracker>(), _clock);

            tracker.EnsureWatching();
            tracker.ActiveWatchers.Should().Be(2);
            tracker.Dispose();
            tracker.ActiveWatchers.Should().Be(0);
            _clock.Now += ProviderActivityTracker.RetryInterval;
            tracker.EnsureWatching();
            tracker.ActiveWatchers.Should().Be(0, "no watcher is started after Dispose");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void EnsureWatching_WindowsRefusesToStartAWatcher_DoesNotThrowAndTriesAgainLater()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentsWinBar_ActivityWatch_" + Guid.NewGuid());
        try
        {
            using var tracker = new ProviderActivityTracker(Roots(root), new ListLogger<ProviderActivityTracker>(), _clock)
            {
                StartWatcher = _ => throw new System.ComponentModel.Win32Exception(5)
            };

            var ensure = () => tracker.EnsureWatching();

            ensure.Should().NotThrow();
            tracker.ActiveWatchers.Should().Be(0);
            tracker.StartWatcher = watcher => watcher.EnableRaisingEvents = true;
            _clock.Now += ProviderActivityTracker.RetryInterval;
            tracker.EnsureWatching();
            tracker.ActiveWatchers.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Two log folders under a new temporary folder (never a real profile), created on disk.</summary>
    private static CostLogRoot[] Roots(string root)
    {
        CostLogRoot[] roots =
        [
            new(CostProvider.Codex, Path.Combine(root, "sessions")),
            new(CostProvider.Gemini, Path.Combine(root, "antigravity"))
        ];
        foreach (var folder in roots) Directory.CreateDirectory(folder.Directory);
        return roots;
    }

    [TestMethod]
    public void EnsureWatching_WithoutWatching_StartsNoWatcher()
    {
        _tracker.EnsureWatching();

        _tracker.ActiveWatchers.Should().Be(0);
    }

    [TestMethod]
    public void Dispose_Twice_DoesNotThrow()
    {
        _tracker.Dispose();
        var again = () => _tracker.Dispose();

        again.Should().NotThrow();
        _tracker.EnsureWatching();
        _tracker.ActiveWatchers.Should().Be(0);
    }
}
