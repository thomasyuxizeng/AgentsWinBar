using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class SignInStatusTests
{
    private static UsageData Reading(string provider = "claude") => new()
    {
        Provider = provider, UsageFetchOutcome = FetchOutcome.Success,
        Session = new UsageWindow { ReportedPercent = 20, WindowMinutes = 300 }
    };

    [TestMethod]
    public void Of_WhenNoData_ReturnsUnknown() => SignInStatus.Of(null).Should().Be(SignInState.Unknown);

    [TestMethod]
    public void Of_WhenReading_ReturnsSignedIn() => SignInStatus.Of(Reading()).Should().Be(SignInState.SignedIn);

    [TestMethod]
    public void Of_WhenAuthRequired_ReturnsSignedOutEvenWithLastKnownReading() =>
        SignInStatus.Of(Reading() with { AuthRequired = true }).Should().Be(SignInState.SignedOut);

    [TestMethod]
    public void Of_WhenSignInExpired_ReturnsExpiredNotSignedOut() =>
        SignInStatus.Of(Reading() with { SignInExpired = true, UsageIsCached = true }).Should().Be(SignInState.Expired);

    [TestMethod]
    public void Of_WhenOnlyNonAuthError_ReturnsUnknown() =>
        SignInStatus.Of(new UsageData { Provider = "codex", Error = "Request timed out", UsageFetchOutcome = FetchOutcome.Failed })
            .Should().Be(SignInState.Unknown);

    [TestMethod]
    [DataRow(SignInState.SignedOut, true)]
    [DataRow(SignInState.Expired, false)]
    [DataRow(SignInState.SignedIn, false)]
    [DataRow(SignInState.Unknown, false)]
    public void CanSignIn_OnlyForSignedOut_ReturnsExpected(SignInState state, bool expected) =>
        SignInStatus.CanSignIn(state).Should().Be(expected);

    [TestMethod]
    [DataRow("claude", "Expired, waiting for Claude Code to renew it")]
    [DataRow("Codex", "Expired, waiting for Codex to renew it")]
    [DataRow("gemini", "Expired, waiting for Antigravity to renew it")]
    public void Text_WhenExpired_NamesTheToolThatRenewsIt(string provider, string expected) =>
        SignInStatus.Text(provider, SignInState.Expired).Should().Be(expected);

    [TestMethod]
    [DataRow(SignInState.SignedIn, "Signed in")]
    [DataRow(SignInState.SignedOut, "Signed out")]
    [DataRow(SignInState.Unknown, "Not checked yet")]
    public void Text_OtherStates_ReturnsPlainState(SignInState state, string expected) =>
        SignInStatus.Text("claude", state).Should().Be(expected);

    [TestMethod]
    public void ToolName_WhenInvalidProvider_Throws()
    {
        var act = () => SignInStatus.ToolName("openai");
        act.Should().Throw<ArgumentException>();
    }
}
