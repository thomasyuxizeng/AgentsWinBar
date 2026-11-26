using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class IncidentCaptionTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Title_NullStatus_ReturnsDefaultFallback() =>
        IncidentCaption.Title((ProviderStatus?)null).Should().Be("Provider service status");

    [TestMethod]
    public void Title_NullStatusWithCustomFallback_ReturnsCustomFallback() =>
        IncidentCaption.Title((ProviderStatus?)null, "Custom Fallback").Should().Be("Custom Fallback");

    [TestMethod]
    public void Title_HealthyStatusWithNoneIndicator_ReturnsFallbackEvenIfTitleSet()
    {
        var status = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.None,
            IncidentTitle = "Old resolved incident"
        };
        IncidentCaption.Title(status).Should().Be("Provider service status");
        IncidentCaption.Title(status, "Fallback").Should().Be("Fallback");
    }

    [TestMethod]
    public void Title_OpenIncident_ReturnsTrimmedTitle()
    {
        var status = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Minor,
            IncidentTitle = "  Elevated error rates on API  "
        };
        IncidentCaption.Title(status).Should().Be("Elevated error rates on API");
    }

    [TestMethod]
    public void Title_OpenIncidentWithNullOrWhitespaceTitle_ReturnsFallback()
    {
        var statusNull = new ProviderStatus { Indicator = ProviderStatusIndicator.Major, IncidentTitle = null };
        var statusEmpty = new ProviderStatus { Indicator = ProviderStatusIndicator.Major, IncidentTitle = "   " };
        IncidentCaption.Title(statusNull).Should().Be("Provider service status");
        IncidentCaption.Title(statusEmpty).Should().Be("Provider service status");
    }

    [TestMethod]
    public void Title_StringOverload_HandlesNullWhitespaceAndValid()
    {
        IncidentCaption.Title((string?)null).Should().Be("Provider service status");
        IncidentCaption.Title("").Should().Be("Provider service status");
        IncidentCaption.Title("   ").Should().Be("Provider service status");
        IncidentCaption.Title("API outage", "Fallback").Should().Be("API outage");
    }

    [TestMethod]
    public void Title_DashboardProviderOverload_ResolvesTitleCorrectly()
    {
        var provider = new DashboardProvider
        {
            Id = "claude",
            IncidentTitle = "Platform degradation"
        };
        IncidentCaption.Title(provider).Should().Be("Platform degradation");

        var emptyProvider = new DashboardProvider { Id = "codex" };
        IncidentCaption.Title(emptyProvider).Should().Be("Provider service status");
    }

    [TestMethod]
    public void UpdateBody_NullOrHealthyStatus_ReturnsEmpty()
    {
        IncidentCaption.UpdateBody((ProviderStatus?)null).Should().BeEmpty();

        var healthy = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.None,
            LatestUpdateText = "Everything is operational"
        };
        IncidentCaption.UpdateBody(healthy).Should().BeEmpty();
    }

    [TestMethod]
    public void UpdateBody_OpenIncident_SanitizesMarkdownAndHtml()
    {
        var status = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Major,
            LatestUpdateText = "**Mitigated:** Issue with `chat` model. See [details](https://example.com) &amp; updates."
        };
        IncidentCaption.UpdateBody(status).Should().Be("Mitigated: Issue with chat model. See details & updates.");
    }

    [TestMethod]
    public void UpdateBody_LongText_CapsAtWordBoundaryWithEllipsis()
    {
        var longText = new string('a', 50) + " " + new string('b', 50) + " " + new string('c', 50) + " " + new string('d', 60);
        var status = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Critical,
            LatestUpdateText = longText
        };
        var body = IncidentCaption.UpdateBody(status, maxLength: 100);
        body.Length.Should().BeLessThanOrEqualTo(100);
        body.Should().EndWith("...");
    }

    [TestMethod]
    public void UpdateBody_DashboardProviderOverload_ResolvesBody()
    {
        var provider = new DashboardProvider
        {
            Id = "gemini",
            IncidentUpdateText = "Investigation in progress."
        };
        IncidentCaption.UpdateBody(provider).Should().Be("Investigation in progress.");
        IncidentCaption.UpdateBody((DashboardProvider?)null).Should().BeEmpty();
    }

    [TestMethod]
    public void AgeText_NullTimestampOrNullStatus_ReturnsEmpty()
    {
        IncidentCaption.AgeText((DateTime?)null, Now).Should().BeEmpty();
        IncidentCaption.AgeText((ProviderStatus?)null, Now).Should().BeEmpty();
        IncidentCaption.AgeText((DashboardProvider?)null, Now).Should().BeEmpty();

        var healthy = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.None,
            LatestUpdateAt = Now.AddHours(-1)
        };
        IncidentCaption.AgeText(healthy, Now).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(0, "Update just now")]
    [DataRow(30, "Update just now")]
    [DataRow(59, "Update just now")]
    [DataRow(60, "Update 1m ago")]
    [DataRow(12 * 60, "Update 12m ago")]
    [DataRow(59 * 60 + 59, "Update 59m ago")]
    [DataRow(60 * 60, "Update 1h ago")]
    [DataRow(3 * 3600, "Update 3h ago")]
    [DataRow(23 * 3600 + 59 * 60, "Update 23h ago")]
    [DataRow(24 * 3600, "Update 1d ago")]
    [DataRow(4 * 86400, "Update 4d ago")]
    public void AgeText_ExactTimeBoundaries_MapsToExpectedBuckets(int secondsAgo, string expectedCaption)
    {
        var updateTime = Now.AddSeconds(-secondsAgo);
        IncidentCaption.AgeText(updateTime, Now).Should().Be(expectedCaption);

        var status = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Minor,
            LatestUpdateAt = updateTime
        };
        IncidentCaption.AgeText(status, Now).Should().Be(expectedCaption);

        var provider = new DashboardProvider
        {
            Id = "claude",
            IncidentUpdateAtUtc = updateTime
        };
        IncidentCaption.AgeText(provider, Now).Should().Be(expectedCaption);
    }

    [TestMethod]
    public void AgeText_FutureTimestampDueToClockDrift_ClampsToJustNow()
    {
        var future = Now.AddMinutes(5);
        IncidentCaption.AgeText(future, Now).Should().Be("Update just now");
    }

    [TestMethod]
    public void AgeText_UnspecifiedKind_TreatedAsUtc()
    {
        var unspecified = DateTime.SpecifyKind(Now.AddHours(-3), DateTimeKind.Unspecified);
        IncidentCaption.AgeText(unspecified, Now).Should().Be("Update 3h ago");
    }
}
