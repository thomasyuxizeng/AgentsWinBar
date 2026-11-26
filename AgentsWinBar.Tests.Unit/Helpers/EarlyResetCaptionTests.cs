using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class EarlyResetCaptionTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Chip_ReturnsExactExpectedBadge()
    {
        EarlyResetCaption.Chip().Should().Be("RESET EARLY");
    }

    [TestMethod]
    public void Detail_FormatsRelativeAgeAndPercentageDrop()
    {
        var at2hAgo = Now.AddHours(-2);
        EarlyResetCaption.Detail(79, 3, at2hAgo, Now)
            .Should().Be("Reset early 2h ago · 79% to 3%");

        var atJustNow = Now.AddSeconds(-30);
        EarlyResetCaption.Detail(50, 15, atJustNow, Now)
            .Should().Be("Reset early just now · 50% to 15%");
    }

    [TestMethod]
    public void ToastTitle_CapitalizesProviderAndIncludesWindowTitle()
    {
        EarlyResetCaption.ToastTitle("codex", "Session")
            .Should().Be("Codex Session was reset early");

        EarlyResetCaption.ToastTitle("claude", "Current week (Sonnet)")
            .Should().Be("Claude Current week (Sonnet) was reset early");

        EarlyResetCaption.ToastTitle("gemini", "5-hour")
            .Should().Be("Gemini 5-hour was reset early");
    }

    [TestMethod]
    public void ToastBody_FormatsRelativeOrAbsoluteScheduledResetTime()
    {
        // Future scheduled reset:
        var futureReset = Now.AddHours(4).AddMinutes(59);
        var bodyFuture = EarlyResetCaption.ToastBody(79, 3, futureReset, Now);
        bodyFuture.Should().Be("Usage dropped from 79% to 3%. The scheduled reset was in 4h 59m.");

        // Past scheduled reset:
        var pastReset = Now.AddHours(-2);
        var bodyPast = EarlyResetCaption.ToastBody(79, 3, pastReset, Now);
        bodyPast.Should().Be("Usage dropped from 79% to 3%. The scheduled reset was 2h ago.");

        // Unknown scheduled reset:
        var bodyNull = EarlyResetCaption.ToastBody(79, 3, null, Now);
        bodyNull.Should().Be("Usage dropped from 79% to 3%. The scheduled reset was unknown.");
    }
}
