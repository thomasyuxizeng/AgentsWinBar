using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class IncidentTextSanitizerTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\t\r\n")]
    public void Sanitize_WhenNullOrEmpty_ReturnsEmpty(string? input)
    {
        var result = IncidentTextSanitizer.Sanitize(input);
        result.Should().BeEmpty();
    }

    [TestMethod]
    public void Sanitize_WhenMarkdownBoldAndItalics_StripsFormatting()
    {
        var input = "We have **mitigated** the issue. *Service* is now ***operational***. Also __tested__ and _verified_.";
        var result = IncidentTextSanitizer.Sanitize(input);
        result.Should().Be("We have mitigated the issue. Service is now operational. Also tested and verified.");
    }

    [TestMethod]
    public void Sanitize_WhenMarkdownLinksAndImages_ExtractsLabels()
    {
        var input = "See our [status page](https://status.claude.com) and ![logo](https://example.com/logo.png) for details.";
        var result = IncidentTextSanitizer.Sanitize(input);
        result.Should().Be("See our status page and logo for details.");
    }

    [TestMethod]
    public void Sanitize_WhenMarkdownHeadersAndLists_StripsMarkers()
    {
        var input = """
            # Incident Notice
            ## Details
            - API traffic impacted
            * Console degraded
            1. Identified root cause
            2. Applied fix
            """;
        var result = IncidentTextSanitizer.Sanitize(input);
        result.Should().Be("Incident Notice Details API traffic impacted Console degraded Identified root cause Applied fix");
    }

    [TestMethod]
    public void Sanitize_WhenMarkdownCodeAndFences_ExtractsPlainText()
    {
        var input = """
            Check `status_impact` code:
            ```json
            {"status": "ok"}
            ```
            All green.
            """;
        var result = IncidentTextSanitizer.Sanitize(input);
        result.Should().Be("Check status_impact code: {\"status\": \"ok\"} All green.");
    }

    [TestMethod]
    public void Sanitize_WhenHtmlTagsAndEntities_DecodesEntitiesAndStripsTags()
    {
        var input = "<p>Errors on &quot;platform&quot; &amp; Admin API have been resolved.</p><br/>Status: <b>OK</b> &copy; 2026";
        var result = IncidentTextSanitizer.Sanitize(input);
        result.Should().Be("Errors on \"platform\" & Admin API have been resolved. Status: OK © 2026");
    }

    [TestMethod]
    public void Sanitize_WhenGoogleSectionHeaders_RemovesHeaderLines()
    {
        var input = """
            **Summary:**
            Mitigated intermittent elevated error rates with Gemini models.
            **Description:**
            Between 6:00 and 13:22 PDT, engineers mitigated the issue.
            **Symptoms:**
            Intermittent 500 errors.
            """;
        var result = IncidentTextSanitizer.Sanitize(input);
        result.Should().Be("Mitigated intermittent elevated error rates with Gemini models. Between 6:00 and 13:22 PDT, engineers mitigated the issue. Intermittent 500 errors.");
    }

    [TestMethod]
    public void Sanitize_WhenConsecutiveWhitespaceAndNewlines_CollapsesToSingleSpace()
    {
        var input = "Line 1 \r\n\t   Line 2\n\n\nLine 3   End";
        var result = IncidentTextSanitizer.Sanitize(input);
        result.Should().Be("Line 1 Line 2 Line 3 End");
    }

    [TestMethod]
    public void Sanitize_WhenTextExceedsMaxLength_TruncatesOnWordBoundaryWithEllipsis()
    {
        var input = "We have implemented a mitigation, and delayed Costs data is now processing through the Compliance API. Some customers may continue to see incomplete Costs data while the backlog catches up. We expect this catch-up process to continue through Sunday, October 11. ChatGPT usage and API Platform requests remain unaffected.";
        var result = IncidentTextSanitizer.Sanitize(input, maxLength: 200);

        result.Length.Should().BeLessThanOrEqualTo(200);
        result.Should().EndWith("...");
        result.Should().NotContain(" ,...");
        result.Should().NotContain(" ....");
        // Must cut at a clean word boundary
        result.Should().Be("We have implemented a mitigation, and delayed Costs data is now processing through the Compliance API. Some customers may continue to see incomplete Costs data while the backlog catches up. We...");
    }

    [TestMethod]
    public void Sanitize_WhenSingleLongWord_TruncatesHardAtMaxWithEllipsis()
    {
        var input = new string('A', 250);
        var result = IncidentTextSanitizer.Sanitize(input, maxLength: 50);

        result.Length.Should().Be(50);
        result.Should().EndWith("...");
        result.Should().Be(new string('A', 47) + "...");
    }

    [TestMethod]
    public void Sanitize_WhenUnicodeAndSurrogatePairs_PreservesCharactersWithoutSplittingSurrogates()
    {
        var input = "Status: 🚀 All systems operational in Zürich & 東京!";
        var result = IncidentTextSanitizer.Sanitize(input);
        result.Should().Be("Status: 🚀 All systems operational in Zürich & 東京!");

        // Edge case: surrogate pair right near the boundary
        var nearBoundary = new string('x', 17) + " 🚀 extra text";
        var truncated = IncidentTextSanitizer.Sanitize(nearBoundary, maxLength: 20);
        truncated.Length.Should().BeLessThanOrEqualTo(20);
        truncated.Should().EndWith("...");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    public void Sanitize_WhenNonPositiveMaxLength_ReturnsEmpty(int max)
    {
        var result = IncidentTextSanitizer.Sanitize("Some text", maxLength: max);
        result.Should().BeEmpty();
    }
}
