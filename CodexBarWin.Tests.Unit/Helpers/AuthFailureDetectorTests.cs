using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class AuthFailureDetectorTests
{
    [TestMethod]
    [DataRow("claude", "Claude OAuth access token missing. Run \u0060claude\u0060 to authenticate.")]
    [DataRow("claude", "OAuth token expired")]
    [DataRow("codex", "Refresh token invalid")]
    [DataRow("codex", "Refresh token has been revoked")]
    [DataRow("codex", "auth.json missing")]
    [DataRow("codex", "Missing credentials")]
    [DataRow("codex", "Not logged in")]
    [DataRow("codex", "Login required")]
    [DataRow("codex", "Run \u0060codex login\u0060 to authenticate")]
    [DataRow("gemini", "Please sign in to Antigravity")]
    [DataRow("gemini", "Run agy to authenticate")]
    [DataRow("gemini", "UNAUTHORIZED")]
    [DataRow("gemini", "HTTP 403 Forbidden")]
    [DataRow("gemini", "HTTP 401")]
    [DataRow("CLAUDE", "INVALID_GRANT")]
    public void IsAuthFailure_FinalAuthenticationMessage_ReturnsTrue(string provider, string message) =>
        AuthFailureDetector.IsAuthFailure(provider, 3, "provider", message).Should().BeTrue();

    [TestMethod]
    [DataRow(3, "Rate limited (429)")]
    [DataRow(429, "Unauthorized: too many requests")]
    [DataRow(3, "Network error refreshing token")]
    [DataRow(3, "Connection refused")]
    [DataRow(3, "DNS lookup failed")]
    [DataRow(3, "Request timed out")]
    [DataRow(124, "Please sign in")]
    [DataRow(127, "Run codex login")]
    [DataRow(3, "codex: command not found")]
    [DataRow(503, "Unauthorized")]
    [DataRow(3, "Service unavailable")]
    [DataRow(3, "Unexpected provider failure")]
    [DataRow(3, "HTTP 1401")]
    public void IsAuthFailure_InfrastructureOrUnrelatedFailure_ReturnsFalse(int code, string message) =>
        AuthFailureDetector.IsAuthFailure("codex", code, "provider", message).Should().BeFalse();

    [TestMethod]
    public void IsAuthFailure_StructuredCodeOrKindWithoutMessage_ReturnsTrue()
    {
        AuthFailureDetector.IsAuthFailure("gemini", 401, "provider", null).Should().BeTrue();
        AuthFailureDetector.IsAuthFailure("codex", 403, null, null).Should().BeTrue();
        AuthFailureDetector.IsAuthFailure("claude", 3, "AUTH_REQUIRED", null).Should().BeTrue();
        AuthFailureDetector.IsAuthFailure("unknown;cmd", 401, "auth", null).Should().BeFalse();
        AuthFailureDetector.IsAuthFailure("claude", 3, "network", "Token expired").Should().BeFalse();
    }

    [TestMethod]
    public void SanitizeMessage_MultilineSecrets_RedactsAndBoundsExplanation()
    {
        var safe = AuthFailureDetector.SanitizeMessage("Token revoked.\naccess_token=fixture-secret refresh_token=\"fixture-refresh\" Bearer fixture-bearer");
        safe.Should().Be("Token revoked. [redacted] [redacted] [redacted]");
        AuthFailureDetector.SanitizeMessage(new string('a', 800))!.Length.Should().BeLessThanOrEqualTo(512);
        AuthFailureDetector.SanitizeMessage(null).Should().BeNull();
    }

    [TestMethod]
    [DataRow("CLAUDE", "Sign in to Claude Code to see your Claude limits.")]
    [DataRow("codex", "Sign in to Codex to see your Codex limits.")]
    [DataRow("gemini", "Sign in to Antigravity with Google to see your Gemini limits.")]
    public void GetSignInHint_ValidProvider_ReturnsProviderRecoveryInstructions(string provider, string expected) =>
        AuthFailureDetector.GetSignInHint(provider).Should().Be(expected);
}
