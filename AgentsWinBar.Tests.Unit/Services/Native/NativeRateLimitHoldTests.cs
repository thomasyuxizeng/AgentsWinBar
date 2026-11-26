using System.Reflection;
using AwesomeAssertions;
using AgentsWinBar.Services.Native;
using static AgentsWinBar.Tests.Unit.Services.Native.NativeTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native;

[TestClass]
public class NativeRateLimitHoldTests
{
    private const string FirstCredential = "synthetic-first-access-token";
    private const string SecondCredential = "synthetic-second-access-token";

    private static NativeUsageResult RateLimited() => new()
    {
        Provider = "codex",
        Status = NativeUsageStatus.RateLimited,
        HttpStatus = 429,
        Message = "Synthetic rate limit.",
        NetworkCalls = 1
    };

    [TestMethod]
    public void Until_WhileHeld_ReturnsTheEndAndNullAfterwards()
    {
        var hold = new NativeRateLimitHold();
        hold.Until(Now).Should().BeNull();
        hold.Remember(RateLimited(), null, Now, FirstCredential);

        hold.Until(Now.AddMinutes(9)).Should().Be(Now.AddMinutes(10));
        hold.Until(Now.AddMinutes(10)).Should().BeNull();
        // The clock set back an hour keeps the hold's length, not its end time.
        hold.Until(Now.AddHours(-1)).Should().Be(Now.AddHours(-1).AddMinutes(10));
    }

    [TestMethod]
    public void Until_ClockSetBackThenAskedRepeatedly_EndDoesNotSlideForward()
    {
        var hold = new NativeRateLimitHold();
        hold.Remember(RateLimited(), null, Now, FirstCredential);
        var back = Now.AddMinutes(1).AddHours(-2);

        var first = hold.Until(back);
        var later = hold.Until(back.AddMinutes(10).AddSeconds(-1));

        first.Should().Be(back.AddMinutes(10));
        later.Should().Be(first, "the shift is stored, so a later wake-up sees the same end");
        hold.Until(back.AddMinutes(10)).Should().BeNull();
    }

    [TestMethod]
    public void Get_WhenSameCredentialIsHeld_ReturnsTheHeldFailure()
    {
        var hold = new NativeRateLimitHold();
        hold.Remember(RateLimited(), Now.AddHours(24), Now, FirstCredential);

        var held = hold.Get(Now.AddHours(23), FirstCredential);

        held.Should().NotBeNull();
        held!.Status.Should().Be(NativeUsageStatus.RateLimited);
        held.RetryAfter.Should().Be(Now.AddHours(24));
        held.NetworkCalls.Should().Be(0);
    }

    [TestMethod]
    public void Get_WhenCredentialChanged_LetsTheRequestThroughAndEndsTheHold()
    {
        var hold = new NativeRateLimitHold();
        hold.Remember(RateLimited(), Now.AddHours(24), Now, FirstCredential);

        hold.Get(Now.AddMinutes(1), SecondCredential).Should().BeNull();
        hold.Get(Now.AddMinutes(2), FirstCredential).Should().BeNull("the hold ended when the sign-in changed");
    }

    [TestMethod]
    public void Remember_WhenCredentialChanged_HoldsForTheNewRetryAfterEvenIfShorter()
    {
        var hold = new NativeRateLimitHold();
        hold.Remember(RateLimited(), Now.AddHours(24), Now, FirstCredential);

        var answered = hold.Remember(RateLimited(), Now.AddSeconds(90), Now.AddMinutes(1), SecondCredential);

        answered.RetryAfter.Should().Be(Now.AddSeconds(90));
        hold.Get(Now.AddSeconds(89), SecondCredential)!.RetryAfter.Should().Be(Now.AddSeconds(90));
        hold.Get(Now.AddSeconds(90), SecondCredential).Should().BeNull();
    }

    [TestMethod]
    public void Remember_WhenHoldIsSet_KeepsNoCopyOfTheCredential()
    {
        var hold = new NativeRateLimitHold();
        hold.Remember(RateLimited(), Now.AddHours(1), Now, FirstCredential);

        var fields = typeof(NativeRateLimitHold).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        fields.Should().NotContain(f => f.FieldType == typeof(string), "only a hash of the credential is kept");
        hold.Get(Now, FirstCredential)!.ToString().Should().NotContain(FirstCredential);
    }
}
