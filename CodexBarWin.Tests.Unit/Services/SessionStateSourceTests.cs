using System.Reflection;
using AwesomeAssertions;
using CodexBarWin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class SessionStateSourceTests
{
    [TestMethod]
    [DataRow(SessionSwitchReason.SessionLock, true)]
    [DataRow(SessionSwitchReason.RemoteDisconnect, true)]
    [DataRow(SessionSwitchReason.ConsoleDisconnect, true)]
    [DataRow(SessionSwitchReason.SessionUnlock, false)]
    [DataRow(SessionSwitchReason.RemoteConnect, false)]
    [DataRow(SessionSwitchReason.ConsoleConnect, false)]
    public void SessionSwitch_LockAndConnectionTransitions_RaisesMatchingEvent(SessionSwitchReason reason, bool locked)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("SystemEvents requires Windows");
        using var source = new SessionStateSource(NullLogger<SessionStateSource>.Instance);
        var locks = 0;
        var unlocks = 0;
        source.Locked += (_, _) => locks++;
        source.Unlocked += (_, _) => unlocks++;
        typeof(SessionStateSource).GetMethod("OnSessionSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(source, [source, new SessionSwitchEventArgs(reason)]);
        locks.Should().Be(locked ? 1 : 0);
        unlocks.Should().Be(locked ? 0 : 1);
    }

    [TestMethod]
    public void PowerModeChanged_ResumeAndDisposedSource_RaisesOnceThenStops()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("SystemEvents requires Windows");
        using var source = new SessionStateSource(NullLogger<SessionStateSource>.Instance);
        var resumes = 0;
        source.Resumed += (_, _) => resumes++;
        var handler = typeof(SessionStateSource).GetMethod("OnPowerModeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(source, [source, new PowerModeChangedEventArgs(PowerModes.Suspend)]);
        handler.Invoke(source, [source, new PowerModeChangedEventArgs(PowerModes.Resume)]);
        resumes.Should().Be(1);
        source.Dispose();
        handler.Invoke(source, [source, new PowerModeChangedEventArgs(PowerModes.Resume)]);
        resumes.Should().Be(1);
    }
}
