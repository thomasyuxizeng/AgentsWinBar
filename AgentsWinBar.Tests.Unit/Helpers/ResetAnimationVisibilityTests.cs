using System.Runtime.CompilerServices;
using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ResetAnimationVisibilityTests
{
    private static readonly DateTime Detected = new(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Back = Detected.AddHours(6);

    [TestMethod]
    public void VisibleSince_ResetNeverNoted_ReturnsNull()
    {
        new ResetAnimationVisibility().VisibleSince(Detected).Should().BeNull();
    }

    [TestMethod]
    public void VisibleSince_UnseenResetBeforeScreenReturns_ReturnsNull()
    {
        var visibility = new ResetAnimationVisibility();
        visibility.NoteUnseen(Detected);
        visibility.VisibleSince(Detected).Should().BeNull();
    }

    [TestMethod]
    public void NoteVisible_AfterUnseenReset_RecordsReturnTimeAndRaisesResumed()
    {
        var visibility = new ResetAnimationVisibility();
        var raised = 0;
        visibility.Resumed += (_, _) => raised++;
        visibility.NoteUnseen(Detected);
        visibility.NoteVisible(Back).Should().BeTrue();
        visibility.VisibleSince(Detected).Should().Be(Back);
        raised.Should().Be(1);
    }

    [TestMethod]
    public void NoteVisible_NothingPending_RaisesNothing()
    {
        var visibility = new ResetAnimationVisibility();
        var raised = 0;
        visibility.Resumed += (_, _) => raised++;
        visibility.NoteVisible(Back).Should().BeFalse();
        raised.Should().Be(0);
    }

    [TestMethod]
    public void NoteVisible_ASecondReturn_KeepsTheFirstReturnTime()
    {
        var visibility = new ResetAnimationVisibility();
        visibility.NoteUnseen(Detected);
        visibility.NoteVisible(Back);
        visibility.NoteVisible(Back.AddHours(3)).Should().BeFalse();
        visibility.VisibleSince(Detected).Should().Be(Back);
    }

    [TestMethod]
    public void NoteVisible_OnlyResetsFromTheLockedStretchAreMeasuredFromTheReturn()
    {
        var visibility = new ResetAnimationVisibility();
        visibility.NoteUnseen(Detected);
        visibility.NoteVisible(Back);
        var later = Back.AddHours(2);
        visibility.NoteUnseen(later);
        visibility.NoteVisible(later.AddHours(1));
        visibility.VisibleSince(Detected).Should().Be(Back);
        visibility.VisibleSince(later).Should().Be(later.AddHours(1));
    }

    [TestMethod]
    public void NoteUnseen_SameStampTwice_IsOneEntry()
    {
        var visibility = new ResetAnimationVisibility();
        visibility.NoteUnseen(Detected);
        visibility.NoteUnseen(Detected.ToLocalTime());
        visibility.NoteVisible(Back).Should().BeTrue();
        visibility.VisibleSince(Detected).Should().Be(Back);
    }

    [TestMethod]
    public void NoteUnseen_MoreThanTheLimit_ForgetsTheOldest()
    {
        var visibility = new ResetAnimationVisibility();
        for (var i = 0; i < 40; i++) visibility.NoteUnseen(Detected.AddMinutes(i));
        visibility.NoteVisible(Back);
        visibility.VisibleSince(Detected).Should().BeNull();
        visibility.VisibleSince(Detected.AddMinutes(39)).Should().Be(Back);
    }

    [TestMethod]
    public void SubscribeResumed_LiveTarget_InvokesCallbackOnNoteVisible()
    {
        var visibility = new ResetAnimationVisibility();
        var target = new DummyTarget();
        using var sub = visibility.SubscribeResumed(target, static t => t.Hits++);
        visibility.NoteUnseen(Detected);
        visibility.NoteVisible(Back).Should().BeTrue();
        target.Hits.Should().Be(1);
    }

    [TestMethod]
    public void SubscribeResumed_DisposedSubscription_DoesNotInvokeOrRetain()
    {
        var visibility = new ResetAnimationVisibility();
        var target = new DummyTarget();
        var sub = visibility.SubscribeResumed(target, static t => t.Hits++);
        sub.Dispose();
        visibility.WeakListenerCount.Should().Be(0);
        visibility.NoteUnseen(Detected);
        visibility.NoteVisible(Back).Should().BeTrue();
        target.Hits.Should().Be(0);
    }

    [TestMethod]
    public void SubscribeResumed_UnreferencedTarget_AllowsGcAndPrunesDeadListener()
    {
        var visibility = new ResetAnimationVisibility();
        var weakTarget = SubscribeTemporaryTarget(visibility);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        weakTarget.TryGetTarget(out _).Should().BeFalse();

        visibility.NoteUnseen(Detected);
        visibility.NoteVisible(Back).Should().BeTrue();
        visibility.WeakListenerCount.Should().Be(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<DummyTarget> SubscribeTemporaryTarget(ResetAnimationVisibility visibility)
    {
        var target = new DummyTarget();
        _ = visibility.SubscribeResumed(target, static t => t.Hits++);
        return new WeakReference<DummyTarget>(target);
    }

    private sealed class DummyTarget
    {
        public int Hits { get; set; }
    }
}
