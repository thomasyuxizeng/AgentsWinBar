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
