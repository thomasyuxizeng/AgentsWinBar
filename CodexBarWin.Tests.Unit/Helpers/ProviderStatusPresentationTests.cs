using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class ProviderStatusPresentationTests
{
    [TestMethod]
    [DataRow(ProviderStatusIndicator.None, ProviderStatusSeverity.Healthy, "#10B981", false, false)]
    [DataRow(ProviderStatusIndicator.Minor, ProviderStatusSeverity.Warning, "#F59E0B", true, true)]
    [DataRow(ProviderStatusIndicator.Maintenance, ProviderStatusSeverity.Warning, "#F59E0B", true, true)]
    [DataRow(ProviderStatusIndicator.Major, ProviderStatusSeverity.Error, "#EF4444", true, true)]
    [DataRow(ProviderStatusIndicator.Critical, ProviderStatusSeverity.Error, "#EF4444", true, true)]
    [DataRow(ProviderStatusIndicator.Unknown, ProviderStatusSeverity.Neutral, "#9CA3AF", false, true)]
    [DataRow((ProviderStatusIndicator)999, ProviderStatusSeverity.Neutral, "#9CA3AF", false, true)]
    public void Presentation_WhenIndicatorKnownOrFuture_MapsHealthAndIncidentVisibility(
        ProviderStatusIndicator indicator, ProviderStatusSeverity severity, string color, bool incident, bool banner)
    {
        var status = new ProviderStatus { Indicator = indicator };
        ProviderStatusPresentation.Severity(status).Should().Be(severity);
        ProviderStatusPresentation.ColorHex(status).Should().Be(color);
        ProviderStatusPresentation.IsIncident(status).Should().Be(incident);
        ProviderStatusPresentation.ShowBanner(status).Should().Be(banner);
    }

    [TestMethod]
    public void Presentation_WhenStatusAbsent_IsNeutralWithNoIncidentOrBanner()
    {
        ProviderStatusPresentation.Severity(null).Should().Be(ProviderStatusSeverity.Neutral);
        ProviderStatusPresentation.ColorHex(null).Should().Be("#9CA3AF");
        ProviderStatusPresentation.IsIncident(null).Should().BeFalse();
        ProviderStatusPresentation.ShowBanner(null).Should().BeFalse();
        ProviderStatusPresentation.Description(null).Should().Be("No status available");
        ProviderStatusPresentation.StatusPageUri(null).Should().BeNull();
    }

    [TestMethod]
    [DataRow(null, ProviderStatusIndicator.Unknown)]
    [DataRow("", ProviderStatusIndicator.Unknown)]
    [DataRow("future", ProviderStatusIndicator.Unknown)]
    [DataRow(" MAJOR ", ProviderStatusIndicator.Major)]
    public void ParseIndicator_WhenMissingUnknownOrMixedCase_IsTolerant(string? value, ProviderStatusIndicator expected) =>
        ProviderStatusPresentation.ParseIndicator(value).Should().Be(expected);

    [TestMethod]
    [DataRow(ProviderStatusIndicator.Unknown, "No status available")]
    [DataRow(ProviderStatusIndicator.None, "All systems operational")]
    [DataRow(ProviderStatusIndicator.Minor, "Minor service disruption")]
    [DataRow(ProviderStatusIndicator.Major, "Major service disruption")]
    [DataRow(ProviderStatusIndicator.Critical, "Critical service disruption")]
    [DataRow(ProviderStatusIndicator.Maintenance, "Scheduled maintenance")]
    public void Description_WhenBlank_UsesIndicatorFallback(ProviderStatusIndicator indicator, string expected) =>
        ProviderStatusPresentation.Description(new ProviderStatus { Indicator = indicator, Description = " " }).Should().Be(expected);

    [TestMethod]
    public void Description_WhenReported_UsesProviderText() =>
        ProviderStatusPresentation.Description(new ProviderStatus { Description = "  Partial outage  " }).Should().Be("Partial outage");

    [TestMethod]
    public void Description_WhenUntrustedTextHasControlCharsOr5Kb_SanitizesAndCaps()
    {
        var input = "Disruption\u0000 with\u001B control chars. " + new string('A', 5000);
        var result = ProviderStatusPresentation.Description(new ProviderStatus { Description = input });

        result.Length.Should().BeLessThanOrEqualTo(IncidentTextSanitizer.DefaultMaxLength);
        result.Should().NotContain("\u0000");
        result.Should().NotContain("\u001B");
        result.Should().EndWith("...");
    }

    [TestMethod]
    [DataRow("https://status.claude.com/", true)]
    [DataRow("http://status.example.com/", true)]
    [DataRow("file:///C:/Windows/", false)]
    [DataRow("javascript:alert(1)", false)]
    [DataRow("ms-settings:privacy", false)]
    [DataRow("/relative", false)]
    [DataRow("invalid url", false)]
    [DataRow(null, false)]
    public void StatusPageUri_WhenUntrusted_OnlyAllowsAbsoluteWebLinks(string? url, bool valid) =>
        (ProviderStatusPresentation.StatusPageUri(new ProviderStatus { Url = url }) != null).Should().Be(valid);
}
