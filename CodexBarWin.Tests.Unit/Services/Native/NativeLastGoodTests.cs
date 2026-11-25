using AwesomeAssertions;
using CodexBarWin.Services.Native;
using static CodexBarWin.Tests.Unit.Services.Native.NativeTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native;

[TestClass]
public class NativeLastGoodTests
{
    private static NativeUsageResult Reading() => new()
    {
        Provider = "codex",
        Status = NativeUsageStatus.Ok,
        LoginMethod = "team",
        Primary = new(40, 300, Now.AddHours(-1), "Resets in 0h 0m"),
        Secondary = new(10, 10080, Now.AddDays(3), "Resets in 3d 0h"),
        ExtraWindows =
        [
            new("past", "Past extra", new(70, 300, Now.AddMinutes(-1), "old text")),
            new("future", "Future extra", new(20, 300, Now.AddHours(2), null)),
            new("open", "Window without reset time", new(5, 300, null, null))
        ],
        UpdatedAt = Now.AddMinutes(-30)
    };

    private static NativeUsageResult Failure(NativeUsageStatus status) => new() { Provider = "codex", Status = status, HttpStatus = 502, Message = "synthetic failure", NetworkCalls = 1 };

    [TestMethod]
    public void ClearPastResets_WhenWindowsHavePassed_ZeroesOnlyThoseWindows()
    {
        var result = NativeLastGood.ClearPastResets(Reading(), Now);

        result.Primary.Should().Be(new NativeUsageWindow(0, 300, null, null));
        result.Secondary.Should().Be(Reading().Secondary);
        result.ExtraWindows[0].Window.Should().Be(new NativeUsageWindow(0, 300, null, null));
        result.ExtraWindows[1].Window.Should().Be(Reading().ExtraWindows[1].Window);
        result.ExtraWindows[2].Window.Should().Be(Reading().ExtraWindows[2].Window, "a window without a reset time is not known to have reset");
        result.LoginMethod.Should().Be("team");
    }

    [TestMethod]
    public void ClearPastResets_WhenResetIsExactlyNow_ZeroesTheWindow()
    {
        var reading = Reading() with { Primary = new(40, 300, Now, "text"), ExtraWindows = [] };

        NativeLastGood.ClearPastResets(reading, Now).Primary!.UsedPercent.Should().Be(0);
    }

    [TestMethod]
    public void ClearPastResets_WhenNothingHasReset_ReturnsSameReading()
    {
        var reading = Reading() with { Primary = new(40, 300, Now.AddHours(1), null), ExtraWindows = [] };

        NativeLastGood.ClearPastResets(reading, Now).Should().BeSameAs(reading);
    }

    [TestMethod]
    public void FromCacheOr_WhenNoLastGood_ReturnsFailureUnchanged()
    {
        var failure = Failure(NativeUsageStatus.Failed);

        NativeLastGood.FromCacheOr(null, failure, Now).Should().BeSameAs(failure);
    }

    [TestMethod]
    public void FromCacheOr_WhenLastGood_ServesItCachedWithFailureFields()
    {
        var result = NativeLastGood.FromCacheOr(Reading(), Failure(NativeUsageStatus.RateLimited) with { RetryAfter = Now.AddMinutes(5) }, Now);

        result.Status.Should().Be(NativeUsageStatus.RateLimited);
        result.Message.Should().Be("synthetic failure");
        result.HttpStatus.Should().Be(502);
        result.RetryAfter.Should().Be(Now.AddMinutes(5));
        result.NetworkCalls.Should().Be(1);
        result.Cached.Should().BeTrue();
        result.CacheUpdatedAt.Should().Be(Now.AddMinutes(-30));
        result.UpdatedAt.Should().Be(Now.AddMinutes(-30));
        result.Primary!.UsedPercent.Should().Be(0);
        result.Secondary!.UsedPercent.Should().Be(10);
    }

    [TestMethod]
    [DataRow(NativeUsageStatus.SignInExpired, true)]
    [DataRow(NativeUsageStatus.Failed, true)]
    [DataRow(NativeUsageStatus.RateLimited, true)]
    [DataRow(NativeUsageStatus.SignInMissing, false)]
    [DataRow(NativeUsageStatus.Unauthorized, false)]
    [DataRow(NativeUsageStatus.Ok, false)]
    public void ServesLastGood_WhenStatusGiven_ServesOnlyExpiredAndTransient(NativeUsageStatus status, bool expected) =>
        NativeLastGood.ServesLastGood(status).Should().Be(expected);

    [TestMethod]
    public void Apply_WhenFreshReadingThenFailure_ServesRememberedReading()
    {
        var store = new NativeLastGoodStore();
        var fresh = Reading() with { Primary = new(40, 300, Now.AddHours(1), null), ExtraWindows = [], UpdatedAt = Now };

        store.Apply(fresh, Now).Should().BeSameAs(fresh);
        var served = store.Apply(Failure(NativeUsageStatus.SignInExpired), Now.AddMinutes(10));

        served.Cached.Should().BeTrue();
        served.CacheUpdatedAt.Should().Be(Now);
        served.Status.Should().Be(NativeUsageStatus.SignInExpired);
        served.Primary!.UsedPercent.Should().Be(40);
    }

    [TestMethod]
    public void Apply_WhenFailureBeforeAnyReading_ReturnsFailure()
    {
        var failure = Failure(NativeUsageStatus.Failed);

        new NativeLastGoodStore().Apply(failure, Now).Should().BeSameAs(failure);
    }

    [TestMethod]
    [DataRow(NativeUsageStatus.SignInMissing)]
    [DataRow(NativeUsageStatus.Unauthorized)]
    public void Apply_WhenSignedOutOrRejected_DoesNotServeRememberedReading(NativeUsageStatus status)
    {
        var store = new NativeLastGoodStore();
        store.Apply(Reading() with { UpdatedAt = Now }, Now);
        var failure = Failure(status);

        store.Apply(failure, Now).Should().BeSameAs(failure);
    }

    [TestMethod]
    public void Apply_WhenOkWithoutUsage_DoesNotReplaceRememberedReading()
    {
        var store = new NativeLastGoodStore();
        store.Apply(Reading() with { UpdatedAt = Now }, Now);
        store.Apply(new NativeUsageResult { Provider = "codex", Status = NativeUsageStatus.Ok }, Now);

        var served = store.Apply(Failure(NativeUsageStatus.Failed), Now);

        served.Cached.Should().BeTrue();
        served.Secondary.Should().NotBeNull();
    }
}
