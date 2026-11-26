using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class UsageFingerprintTests
{
    [TestMethod]
    public void HasChangedFrom_SignedOutEvenWithUnchangedNumbers_PreventsRefreshBackoff()
    {
        var good = UsageFingerprint.Create([Data()]);
        var signedOut = UsageFingerprint.Create([Data() with { AuthRequired = true }]);
        signedOut.HasChangedFrom(good).Should().BeTrue();
        signedOut.HasChangedFrom(signedOut).Should().BeTrue();
        good.HasChangedFrom(signedOut).Should().BeTrue();
    }

    private static readonly DateTime Reset = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static UsageData Data(double percent = 20) => new()
    {
        Provider = "codex",
        Session = new UsageWindow { ReportedPercent = percent, ResetAt = Reset }
    };

    [TestMethod]
    public void HasChangedFrom_UnchangedUsage_ReturnsFalse()
    {
        UsageFingerprint.Create([Data() with { FetchedAt = DateTime.UtcNow.AddMinutes(5) }])
            .HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeFalse();
    }

    [TestMethod]
    public void HasChangedFrom_PercentChangeBelowTolerance_ReturnsFalse()
    {
        UsageFingerprint.Create([Data(20.04)]).HasChangedFrom(UsageFingerprint.Create([Data()]))
            .Should().BeFalse();
    }

    [TestMethod]
    public void HasChangedFrom_PercentChangeAtTolerance_ReturnsFalse()
    {
        UsageFingerprint.Create([Data(20.05)]).HasChangedFrom(UsageFingerprint.Create([Data()]))
            .Should().BeFalse();
    }

    [TestMethod]
    public void HasChangedFrom_PercentChangeAboveTolerance_ReturnsTrue()
    {
        UsageFingerprint.Create([Data(20.06)]).HasChangedFrom(UsageFingerprint.Create([Data()]))
            .Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_WindowAdded_ReturnsTrue()
    {
        UsageFingerprint.Create([Data() with { Weekly = new UsageWindow() }])
            .HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_WindowRemoved_ReturnsTrue()
    {
        UsageFingerprint.Create([Data() with { Session = null }])
            .HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_ResetTimeDriftsWithinTolerance_ReturnsFalse()
    {
        // An idle rolling window reports "now + window length", so its reset time follows the refresh interval
        UsageFingerprint.Create([Data() with { Session = Data().Session! with { ResetAt = Reset.AddMinutes(15) } }])
            .HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeFalse();
        UsageFingerprint.Create([Data() with { Session = Data().Session! with { ResetAt = Reset.AddMinutes(-30) } }])
            .HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeFalse();
    }

    [TestMethod]
    public void HasChangedFrom_ResetTimeMovesBeyondTolerance_ReturnsTrue()
    {
        UsageFingerprint.Create([Data() with { Session = Data().Session! with { ResetAt = Reset.AddHours(1).AddSeconds(1) } }])
            .HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeTrue();
        UsageFingerprint.Create([Data() with { Session = Data().Session! with { ResetAt = Reset.AddHours(-2) } }])
            .HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_ResetTimeAppearsOrDisappears_ReturnsTrue()
    {
        var withoutReset = Data() with { Session = Data().Session! with { ResetAt = null } };
        UsageFingerprint.Create([withoutReset]).HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeTrue();
        UsageFingerprint.Create([Data()]).HasChangedFrom(UsageFingerprint.Create([withoutReset])).Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_ResetTimeDriftsButPercentMoves_ReturnsTrue()
    {
        UsageFingerprint.Create([Data(25) with { Session = new UsageWindow { ReportedPercent = 25, ResetAt = Reset.AddMinutes(5) } }])
            .HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_RepeatedProviderError_ReturnsTrue()
    {
        var data = Data() with { Error = "Unavailable" };
        UsageFingerprint.Create([data]).HasChangedFrom(UsageFingerprint.Create([data])).Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_ProviderRecoversFromError_ReturnsTrue()
    {
        UsageFingerprint.Create([Data()]).HasChangedFrom(UsageFingerprint.Create([Data() with { Error = "Unavailable" }]))
            .Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_NoPreviousRefresh_ReturnsTrue()
    {
        UsageFingerprint.Create([Data()]).HasChangedFrom(null).Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_ProviderAdded_ReturnsTrue()
    {
        UsageFingerprint.Create([Data(), Data() with { Provider = "claude" }])
            .HasChangedFrom(UsageFingerprint.Create([Data()])).Should().BeTrue();
    }

    [TestMethod]
    public void HasChangedFrom_ProviderOrderChanged_ReturnsFalse()
    {
        var claude = Data() with { Provider = "claude" };
        UsageFingerprint.Create([claude, Data()]).HasChangedFrom(UsageFingerprint.Create([Data(), claude]))
            .Should().BeFalse();
    }

    [TestMethod]
    public void HasChangedFrom_ExtraWindowPercentChanged_ReturnsTrue()
    {
        var data = Data() with { ExtraWindows = [new() { Key = "quota", Window = new() { ReportedPercent = 10 } }] };
        var changed = data with { ExtraWindows = [new() { Key = "quota", Window = new() { ReportedPercent = 11 } }] };
        UsageFingerprint.Create([changed]).HasChangedFrom(UsageFingerprint.Create([data])).Should().BeTrue();
    }
}
