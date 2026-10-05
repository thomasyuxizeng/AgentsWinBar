using System.Reflection;
using System.Runtime.InteropServices;
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
    [DataRow(1, 0, true)]
    [DataRow(1, 1, false)]
    [DataRow(1, -1, false)]
    [DataRow(1, 2, false)]
    [DataRow(0, 0, false)]
    [DataRow(2, 0, false)]
    public void ReadIsLocked_LevelAndSessionFlags_ReportsOnlyKnownLock(int level, int flags, bool expected)
    {
        var buffer = Marshal.AllocHGlobal(20);
        try
        {
            Marshal.WriteInt32(buffer, 0, level);
            Marshal.WriteInt32(buffer, 8, 42); // SessionId
            Marshal.WriteInt32(buffer, 12, 0); // SessionState is not the lock flag.
            Marshal.WriteInt32(buffer, 16, flags);
            SessionStateNative.ReadIsLocked(buffer, 20).Should().Be(expected);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [TestMethod]
    public void ReadIsLocked_NullOrTruncatedBuffer_DoesNotReportLock()
    {
        SessionStateNative.ReadIsLocked(IntPtr.Zero, 20).Should().BeFalse();
        var buffer = Marshal.AllocHGlobal(20);
        try
        {
            Marshal.WriteInt32(buffer, 0, 1);
            Marshal.WriteInt32(buffer, 16, 0);
            SessionStateNative.ReadIsLocked(buffer, 19).Should().BeFalse();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

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
