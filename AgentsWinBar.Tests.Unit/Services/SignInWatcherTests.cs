using AwesomeAssertions;
using AgentsWinBar.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class SignInWatcherTests
{
    private const string Profile = @"C:\Users\Test";
    private static readonly string ClaudeFile = Path.Combine(Profile, ".claude", ".credentials.json");
    private static readonly string CodexFile = Path.Combine(Profile, ".codex", "auth.json");

    /// <summary>Hand-driven signals: tests set stamps, fire file events and tick timers themselves.</summary>
    private sealed class FakeSignals : ISignInSignals
    {
        public sealed class FakeTimer(TimeSpan due, TimeSpan period, Action tick) : IDisposable
        {
            public TimeSpan Due { get; } = due;
            public TimeSpan Period { get; } = period;
            public bool Disposed { get; private set; }
            /// <summary>Runs the callback even after Dispose: a real timer callback can race with its disposal.</summary>
            public void Tick() => tick();
            public void Dispose() => Disposed = true;
        }

        public sealed class FakeWatch(string path, Action changed) : IDisposable
        {
            public string Path { get; } = path;
            public bool Disposed { get; private set; }
            public void Change() { if (!Disposed) changed(); }
            public void Dispose() => Disposed = true;
        }

        public DateTime UtcNow { get; set; } = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        public string UserProfile => Profile;
        public Dictionary<string, DateTime?> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime? Credential { get; set; }
        public string? CredentialTargetRead { get; private set; }
        public Exception? WatchError { get; set; }
        public Exception? TimerError { get; set; }
        public bool FoldersExist { get; set; } = true;
        public List<FakeTimer> Timers { get; } = [];
        public List<FakeWatch> Watches { get; } = [];

        public DateTime? FileStamp(string path) => Files.GetValueOrDefault(path);
        public DateTime? CredentialStamp(string target) { CredentialTargetRead = target; return Credential; }

        public IDisposable? WatchFile(string path, Action changed)
        {
            if (WatchError is { } error) throw error;
            if (!FoldersExist) return null;
            var watch = new FakeWatch(path, changed);
            Watches.Add(watch);
            return watch;
        }

        public IDisposable StartTimer(TimeSpan dueTime, TimeSpan period, Action tick)
        {
            if (TimerError is { } error) throw error;
            var timer = new FakeTimer(dueTime, period, tick);
            Timers.Add(timer);
            return timer;
        }

        public FakeTimer Poll => Timers.Single(t => t.Period == SignInWatcher.PollInterval);
        public IEnumerable<FakeTimer> Live => Timers.Where(t => !t.Disposed);
        public bool AnyLive => Live.Any() || Watches.Any(w => !w.Disposed);
    }

    private readonly FakeSignals _signals = new();
    private readonly List<SignInRefreshRequest> _requests = [];
    private SignInWatcher _watcher = null!;

    [TestInitialize]
    public void Initialize()
    {
        _watcher = new SignInWatcher(_signals, NullLogger<SignInWatcher>.Instance);
        _watcher.RefreshRequested += (_, request) => _requests.Add(request);
    }

    [TestCleanup]
    public void Cleanup() => _watcher.Dispose();

    [TestMethod]
    public void Constructor_NoSignInPending_StartsNoTimerOrWatcher()
    {
        _signals.Timers.Should().BeEmpty();
        _signals.Watches.Should().BeEmpty();
        _watcher.IsPending("claude").Should().BeFalse();
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    public void Begin_ClaudeOrCodex_WatchesTheCredentialFile(string provider)
    {
        _watcher.Begin(provider);

        _signals.Watches.Single().Path.Should().Be(provider == "claude" ? ClaudeFile : CodexFile);
        _signals.Poll.Due.Should().Be(SignInWatcher.PollInterval);
        _watcher.IsPending(provider).Should().BeTrue();
    }

    [TestMethod]
    public void Begin_WhenWatchSetupThrows_ClearsOnlyTheFailedProvider()
    {
        _watcher.Begin("gemini");
        _signals.WatchError = new InvalidOperationException("Synthetic watcher failure.");

        var begin = () => _watcher.Begin("claude");
        begin.Should().Throw<InvalidOperationException>();

        _watcher.IsPending("claude").Should().BeFalse();
        _watcher.IsPending("gemini").Should().BeTrue();
        _signals.Poll.Disposed.Should().BeFalse();
    }

    [TestMethod]
    public void Begin_WhenPollSetupThrows_DisposesTheWatchAndAllowsRetry()
    {
        _signals.TimerError = new InvalidOperationException("Synthetic timer failure.");

        var begin = () => _watcher.Begin("claude");
        begin.Should().Throw<InvalidOperationException>();

        _watcher.IsPending("claude").Should().BeFalse();
        _signals.AnyLive.Should().BeFalse();
        _signals.TimerError = null;
        _watcher.Begin("claude");
        _watcher.IsPending("claude").Should().BeTrue();
        _signals.Poll.Disposed.Should().BeFalse();
    }

    [TestMethod]
    public void Begin_WhenCredentialFileChangesAgain_RefreshesAfterEachSettleUntilCanceled()
    {
        _watcher.Begin("claude");
        var watch = _signals.Watches.Single();

        _signals.Files[ClaudeFile] = _signals.UtcNow;
        watch.Change();
        watch.Change();   // a tool's write raises several events
        _requests.Should().BeEmpty("the refresh waits for the tool to finish writing");
        var settle = _signals.Timers.Single(t => t.Due == SignInWatcher.SettleDelay);
        settle.Period.Should().Be(Timeout.InfiniteTimeSpan);
        settle.Tick();

        _requests.Should().Equal(new SignInRefreshRequest("claude", true));
        settle.Disposed.Should().BeTrue();
        _watcher.IsPending("claude").Should().BeTrue();
        _signals.Poll.Tick();
        _requests.Should().ContainSingle("polling should remember the stamp already handled by the file watcher");

        _signals.UtcNow += TimeSpan.FromSeconds(5);
        _signals.Files[ClaudeFile] = _signals.UtcNow;
        watch.Change();
        _signals.Live.Single(t => t.Due == SignInWatcher.SettleDelay).Tick();
        _requests.Should().HaveCount(2).And.AllBeEquivalentTo(new SignInRefreshRequest("claude", true));

        _watcher.Cancel("claude");
        watch.Disposed.Should().BeTrue();
        _signals.Live.Should().BeEmpty("nothing keeps running once no sign-in is pending");
        _watcher.IsPending("claude").Should().BeFalse();
    }

    [TestMethod]
    public void Poll_WhenCredentialFileAppears_RequestsRefreshEvenWithoutFolderWatcher()
    {
        _signals.FoldersExist = false;   // the tool never ran: no folder to watch yet
        _watcher.Begin("codex");
        _signals.Watches.Should().BeEmpty();

        _signals.Poll.Tick();
        _requests.Should().BeEmpty();
        _signals.Files[CodexFile] = _signals.UtcNow;
        _signals.Poll.Tick();

        _requests.Should().Equal(new SignInRefreshRequest("codex", true));
        _signals.Poll.Tick();
        _requests.Should().ContainSingle();

        _signals.UtcNow += SignInWatcher.PollInterval;
        _signals.Files[CodexFile] = _signals.UtcNow;
        _signals.Poll.Tick();
        _requests.Should().HaveCount(2).And.AllBeEquivalentTo(new SignInRefreshRequest("codex", true));
        _signals.UtcNow += SignInWatcher.FileWindow;
        _signals.Poll.Tick();
        _signals.Live.Should().BeEmpty();
    }

    [TestMethod]
    public void Poll_WhenFileStampUnchanged_DoesNotRefresh()
    {
        _signals.Files[ClaudeFile] = _signals.UtcNow.AddDays(-3);
        _watcher.Begin("claude");

        for (var i = 0; i < 5; i++) { _signals.UtcNow += SignInWatcher.PollInterval; _signals.Poll.Tick(); }

        _requests.Should().BeEmpty();
        _watcher.IsPending("claude").Should().BeTrue();
    }

    [TestMethod]
    public void Poll_AfterFileWindow_StopsWatchingWithoutRefresh()
    {
        _watcher.Begin("claude");
        var watch = _signals.Watches.Single();

        _signals.UtcNow += SignInWatcher.FileWindow;
        _signals.Poll.Tick();

        _requests.Should().BeEmpty();
        watch.Disposed.Should().BeTrue();
        _signals.Live.Should().BeEmpty();
    }

    [TestMethod]
    public void Begin_Gemini_ChecksCredentialManagerEntryWithoutFileWatcher()
    {
        _watcher.Begin("gemini");

        _signals.Watches.Should().BeEmpty();
        _signals.CredentialTargetRead.Should().Be("gemini:antigravity");
        _signals.Poll.Should().NotBeNull();
    }

    [TestMethod]
    public void Poll_WhenGeminiCredentialIsRewrittenTwice_RefreshesForEachStamp()
    {
        _signals.Credential = _signals.UtcNow.AddDays(-1);
        _watcher.Begin("gemini");

        _signals.UtcNow += SignInWatcher.PollInterval;
        _signals.Credential = _signals.UtcNow;
        _signals.Poll.Tick();

        _requests.Should().Equal(new SignInRefreshRequest("gemini", true));
        _signals.Poll.Tick();
        _requests.Should().ContainSingle();

        _signals.UtcNow += SignInWatcher.PollInterval;
        _signals.Credential = _signals.UtcNow;
        _signals.Poll.Tick();
        _requests.Should().HaveCount(2).And.AllBeEquivalentTo(new SignInRefreshRequest("gemini", true));
        _watcher.Cancel("gemini");
        _signals.Live.Should().BeEmpty();
    }

    [TestMethod]
    public void Poll_GeminiWithoutCredentialChange_RechecksOnceAMinuteForFiveMinutesThenStops()
    {
        _watcher.Begin("gemini");
        var end = _signals.UtcNow + SignInWatcher.GeminiWindow;

        while (_signals.AnyLive)
        {
            _signals.UtcNow += SignInWatcher.PollInterval;
            _signals.Poll.Tick();
        }

        _signals.UtcNow.Should().Be(end);
        _requests.Should().HaveCount(4).And.AllBeEquivalentTo(new SignInRefreshRequest("gemini", false));
        _watcher.IsPending("gemini").Should().BeFalse();
    }

    [TestMethod]
    public void Cancel_StopsWatchingAndTheTimer()
    {
        _watcher.Begin("claude");

        _watcher.Cancel("CLAUDE");

        _watcher.IsPending("claude").Should().BeFalse();
        _signals.Watches.Single().Disposed.Should().BeTrue();
        _signals.Live.Should().BeEmpty();
    }

    [TestMethod]
    public void Cancel_OneOfTwoPending_KeepsTheOtherAndTheTimer()
    {
        _watcher.Begin("claude");
        _watcher.Begin("gemini");

        _watcher.Cancel("claude");

        _watcher.IsPending("gemini").Should().BeTrue();
        _signals.Timers.Count(t => t.Period == SignInWatcher.PollInterval).Should().Be(1, "pending sign-ins share one timer");
        _signals.Poll.Disposed.Should().BeFalse();
    }

    [TestMethod]
    public void Cancel_UnknownProvider_IsIgnored()
    {
        _watcher.Begin("claude");

        var act = () => _watcher.Cancel("openai");

        act.Should().NotThrow();
        _watcher.IsPending("claude").Should().BeTrue();
    }

    [TestMethod]
    public void Begin_Twice_ReplacesTheEarlierWatchAndIgnoresItsLateSettle()
    {
        _watcher.Begin("claude");
        var first = _signals.Watches.Single();
        first.Change();
        var staleSettle = _signals.Timers.Single(t => t.Due == SignInWatcher.SettleDelay);

        _watcher.Begin("claude");
        staleSettle.Tick();

        first.Disposed.Should().BeTrue();
        staleSettle.Disposed.Should().BeTrue();
        _requests.Should().BeEmpty();
        _watcher.IsPending("claude").Should().BeTrue();
        _signals.Timers.Count(t => t.Period == SignInWatcher.PollInterval).Should().Be(1, "a restart keeps the running timer");
    }

    [TestMethod]
    public void Begin_WhenInvalidProvider_Throws()
    {
        var act = () => _watcher.Begin("openai");

        act.Should().Throw<ArgumentException>();
        _signals.Timers.Should().BeEmpty();
    }

    [TestMethod]
    public void Dispose_StopsEverythingAndIgnoresLaterBegin()
    {
        _watcher.Begin("codex");

        _watcher.Dispose();
        _watcher.Begin("claude");

        _signals.Live.Should().BeEmpty();
        _watcher.IsPending("claude").Should().BeFalse();
    }
}
