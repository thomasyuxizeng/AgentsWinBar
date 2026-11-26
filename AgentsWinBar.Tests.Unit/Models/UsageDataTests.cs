using AwesomeAssertions;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public class UsageDataTests
{
    [TestMethod]
    [DataRow("gemini")]
    [DataRow("Gemini")]
    [DataRow("GEMINI")]
    public void SessionLabel_WhenProviderIsGemini_ReturnsFiveHour(string provider)
    {
        // Arrange
        var data = new UsageData { Provider = provider };

        // Act & Assert
        data.SessionLabel.Should().Be("5-hour");
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    [DataRow("Claude")]
    [DataRow("other")]
    [DataRow("")]
    public void SessionLabel_WhenProviderIsNotGemini_ReturnsSession(string provider)
    {
        // Arrange
        var data = new UsageData { Provider = provider };

        // Act & Assert
        data.SessionLabel.Should().Be("Session");
    }

    [TestMethod]
    public void WeeklyLabel_Always_ReturnsWeekly()
    {
        // Arrange & Act
        var data = new UsageData { Provider = "claude" };

        // Assert
        data.WeeklyLabel.Should().Be("Weekly");
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("Claude")]
    [DataRow("CLAUDE")]
    public void TertiaryLabel_WhenProviderIsClaude_ReturnsSonnetWeekly(string provider)
    {
        // Arrange
        var data = new UsageData { Provider = provider };

        // Act & Assert
        data.TertiaryLabel.Should().Be("Current week (Sonnet)");
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("gemini")]
    [DataRow("other")]
    [DataRow("")]
    public void TertiaryLabel_WhenProviderIsNotClaude_ReturnsAdditional(string provider)
    {
        // Arrange
        var data = new UsageData { Provider = provider };

        // Act & Assert
        data.TertiaryLabel.Should().Be("Additional");
    }

    [TestMethod]
    public void HasWeekly_WhenWeeklySetOrNull_ReturnsExpected()
    {
        // Arrange & Act & Assert
        new UsageData { Weekly = new UsageWindow() }.HasWeekly.Should().BeTrue();
        new UsageData { Weekly = null }.HasWeekly.Should().BeFalse();
    }

    [TestMethod]
    public void HasTertiary_WhenTertiarySetOrNull_ReturnsExpected()
    {
        // Arrange & Act & Assert
        new UsageData { Tertiary = new UsageWindow() }.HasTertiary.Should().BeTrue();
        new UsageData { Tertiary = null }.HasTertiary.Should().BeFalse();
    }

    [TestMethod]
    public void HasExtraWindows_WhenExtraWindowsHasElementsOrEmpty_ReturnsExpected()
    {
        // Arrange & Act & Assert
        new UsageData { ExtraWindows = [] }.HasExtraWindows.Should().BeFalse();
        new UsageData { ExtraWindows = [new ExtraUsageWindow { Title = "Custom" }] }.HasExtraWindows.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("Network timeout", true)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void HasError_WhenErrorSetOrNullOrEmpty_ReturnsExpected(string? error, bool expected)
    {
        // Arrange
        var data = new UsageData { Error = error };

        // Act & Assert
        data.HasError.Should().Be(expected);
    }

    [TestMethod]
    public void IsStale_WhenFetchedAtOlderThanFiveMinutes_ReturnsTrue()
    {
        // Arrange
        var data = new UsageData { FetchedAt = DateTime.UtcNow.AddMinutes(-5).AddSeconds(-1) };

        // Act & Assert
        data.IsStale.Should().BeTrue();
    }

    [TestMethod]
    public void IsStale_WhenFetchedAtRecent_ReturnsFalse()
    {
        // Arrange
        var data = new UsageData { FetchedAt = DateTime.UtcNow.AddMinutes(-4) };

        // Act & Assert
        data.IsStale.Should().BeFalse();
    }

    [TestMethod]
    public void UsageWindow_Percent_WhenReportedPercentSpecified_UsesReportedPercent()
    {
        // Arrange
        var window = new UsageWindow { ReportedPercent = 75.5, Used = 10, Limit = 100 };

        // Act & Assert
        window.Percent.Should().Be(75.5);
    }

    [TestMethod]
    [DataRow(25, 100, 25.0)]
    [DataRow(5, 10, 50.0)]
    [DataRow(0, 100, 0.0)]
    public void UsageWindow_Percent_WhenReportedPercentNullAndLimitPositive_CalculatesFromUsedAndLimit(int used, int limit, double expected)
    {
        // Arrange
        var window = new UsageWindow { Used = used, Limit = limit, ReportedPercent = null };

        // Act & Assert
        window.Percent.Should().Be(expected);
    }

    [TestMethod]
    [DataRow(10, 0)]
    [DataRow(10, -5)]
    public void UsageWindow_Percent_WhenReportedPercentNullAndLimitZeroOrNegative_ReturnsZero(int used, int limit)
    {
        // Arrange
        var window = new UsageWindow { Used = used, Limit = limit, ReportedPercent = null };

        // Act & Assert
        window.Percent.Should().Be(0.0);
    }

    [TestMethod]
    [DataRow(42.4, "42%")]
    [DataRow(42.6, "43%")]
    [DataRow(43.5, "44%")]
    [DataRow(0.0, "0%")]
    [DataRow(100.0, "100%")]
    public void UsageWindow_PercentText_FormatsAsRoundedIntegerPercentage(double percent, string expected)
    {
        // Arrange
        var window = new UsageWindow { ReportedPercent = percent };

        // Act & Assert
        window.PercentText.Should().Be(expected);
    }

    [TestMethod]
    public void UsageWindow_TimeUntilReset_WhenResetAtSpecified_ReturnsRemainingTimeSpan()
    {
        // Arrange
        var resetAt = DateTime.UtcNow.AddHours(2);
        var window = new UsageWindow { ResetAt = resetAt };

        // Act
        var timeUntilReset = window.TimeUntilReset;

        // Assert
        timeUntilReset.Should().NotBeNull();
        timeUntilReset!.Value.TotalMinutes.Should().BeInRange(115, 125);
    }

    [TestMethod]
    public void UsageWindow_TimeUntilReset_WhenResetAtNull_ReturnsNull()
    {
        // Arrange
        var window = new UsageWindow { ResetAt = null };

        // Act & Assert
        window.TimeUntilReset.Should().BeNull();
    }

    [TestMethod]
    public void UsageWindow_ResetText_WhenResetAtSpecified_ReturnsFormattedResetTime()
    {
        // Arrange
        var window = new UsageWindow { ResetAt = DateTime.UtcNow.AddMinutes(45), ResetIn = "fallback" };

        // Act & Assert
        window.ResetText.Should().StartWith("Resets in ");
        window.ResetText.Should().EndWith("m");
    }

    [TestMethod]
    [DataRow("  in 2 days  ", "in 2 days")]
    [DataRow("Tomorrow", "Tomorrow")]
    public void UsageWindow_ResetText_WhenResetAtNullAndResetInSpecified_ReturnsTrimmedResetIn(string resetIn, string expected)
    {
        // Arrange
        var window = new UsageWindow { ResetAt = null, ResetIn = resetIn };

        // Act & Assert
        window.ResetText.Should().Be(expected);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void UsageWindow_ResetText_WhenBothNullOrWhitespace_ReturnsEmptyString(string? resetIn)
    {
        // Arrange
        var window = new UsageWindow { ResetAt = null, ResetIn = resetIn };

        // Act & Assert
        window.ResetText.Should().BeEmpty();
    }

    [TestMethod]
    public void UsageWindow_ElapsedPercentAndPace_WhenConfigured_ComputesFromResetAt()
    {
        // Arrange
        var window = new UsageWindow
        {
            ResetAt = DateTime.UtcNow.AddMinutes(150),
            WindowMinutes = 300,
            ReportedPercent = 50.0
        };

        // Act & Assert
        window.ElapsedPercent.Should().NotBeNull();
        window.ElapsedPercent!.Value.Should().BeInRange(49.0, 51.0);
        window.Pace.Should().NotBeNull();
        window.PaceSummary.Should().NotBeNull();
    }

    [TestMethod]
    public void CreditsInfo_Remaining_CalculatesDifferenceBetweenLimitAndUsed()
    {
        // Arrange
        var credits = new CreditsInfo { Limit = 50.0m, Used = 12.5m };

        // Act & Assert
        credits.Remaining.Should().Be(37.5m);
    }

    [TestMethod]
    public void ExtraUsageWindow_DefaultValues_AreExpected()
    {
        // Arrange & Act
        var extra = new ExtraUsageWindow();

        // Assert
        extra.Key.Should().BeEmpty();
        extra.Title.Should().BeEmpty();
        extra.Window.Should().NotBeNull();
    }

    [TestMethod]
    public void UsageAnsweredFromHold_WhenSerialized_IsLeftOut()
    {
        var data = new UsageData { Provider = "claude", UsageAnsweredFromHold = true };

        var json = System.Text.Json.JsonSerializer.Serialize(data, AppJsonSerializerContext.Default.UsageData);

        json.Should().NotContain("AnsweredFromHold", "it describes one read and is never saved");
        System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.UsageData)!
            .UsageAnsweredFromHold.Should().BeFalse();
    }
}
