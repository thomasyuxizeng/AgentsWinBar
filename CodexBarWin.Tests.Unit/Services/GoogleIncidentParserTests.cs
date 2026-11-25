using System.Text;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class GoogleIncidentParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ParseIncidents_OpenIncident_ExtractsTitleAndSanitizedUpdate()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "service_name": "Gemini",
                "status_impact": "SERVICE_DISRUPTION",
                "severity": "high",
                "begin": "2026-10-11T09:00:00Z",
                "end": null,
                "modified": "2026-10-11T11:00:00Z",
                "external_desc": "**Summary:**\nIntermittent elevated errors on Gemini 3.1.\n**Description:**\nEngineers mitigating.",
                "most_recent_update": {
                  "text": "**Summary:**\nMitigation applied, error rates dropping.",
                  "status": "SERVICE_DISRUPTION",
                  "when": "2026-10-11T11:00:00Z"
                }
              }
            ]
            """;

        var status = GoogleIncidentParser.ParseIncidents(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Major);
        status.Description.Should().Be("Intermittent elevated errors on Gemini 3.1.");
        status.IncidentTitle.Should().Be("Intermittent elevated errors on Gemini 3.1.");
        status.LatestUpdateText.Should().Be("Mitigation applied, error rates dropping.");
        status.LatestUpdateAt.Should().Be(new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    public void ParseIncidents_ResolvedIncidentWithEnd_ReturnsNone()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "service_name": "Gemini",
                "status_impact": "SERVICE_OUTAGE",
                "severity": "high",
                "begin": "2026-10-10T08:00:00Z",
                "end": "2026-10-10T10:00:00Z",
                "external_desc": "Outage has ended."
              }
            ]
            """;

        var status = GoogleIncidentParser.ParseIncidents(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.None);
        status.IncidentTitle.Should().BeNull();
        status.LatestUpdateText.Should().BeNull();
    }

    [TestMethod]
    public void ParseIncidents_ResolvedIncidentWithoutEnd_DetectsAvailableAndReturnsNone()
    {
        // Real-world Google feed behavior: status_impact is still SERVICE_DISRUPTION,
        // end is null or empty, but most_recent_update.status is AVAILABLE.
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "service_name": "Gemini",
                "status_impact": "SERVICE_DISRUPTION",
                "severity": "medium",
                "begin": "2026-10-11T07:00:00Z",
                "end": null,
                "external_desc": "Issue mitigated.",
                "most_recent_update": {
                  "text": "The issue has been resolved for all users.",
                  "status": "AVAILABLE",
                  "when": "2026-10-11T10:30:00Z"
                }
              }
            ]
            """;

        var status = GoogleIncidentParser.ParseIncidents(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.None);
        status.IncidentTitle.Should().BeNull();
        status.LatestUpdateText.Should().BeNull();
    }

    [TestMethod]
    public void ParseIncidents_ResolvedViaUpdatesArray_DetectsAvailableAndReturnsNone()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "status_impact": "SERVICE_DISRUPTION",
                "severity": "medium",
                "end": null,
                "updates": [
                  { "status": "SERVICE_DISRUPTION", "text": "Investigating" },
                  { "status": "AVAILABLE", "text": "Resolved" }
                ]
              }
            ]
            """;

        var status = GoogleIncidentParser.ParseIncidents(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.None);
        status.IncidentTitle.Should().BeNull();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("<html>Server Error</html>")]
    [DataRow("{}")]
    public void ParseIncidents_MalformedJson_ReturnsNullWithoutThrowing(string malformed)
    {
        var status = GoogleIncidentParser.ParseIncidents(malformed, Now);
        status.Should().BeNull();
    }

    [TestMethod]
    public void ParseIncidents_Utf8BytesWithBom_MatchesStringOverload()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "service_name": "Gemini",
                "status_impact": "SERVICE_DISRUPTION",
                "severity": "high",
                "begin": "2026-10-11T09:00:00Z",
                "end": null,
                "modified": "2026-10-11T11:00:00Z",
                "external_desc": "Intermittent errors on Gemini.",
                "most_recent_update": {
                  "text": "Mitigating now.",
                  "status": "SERVICE_DISRUPTION",
                  "when": "2026-10-11T11:00:00Z"
                }
              }
            ]
            """;
        var payload = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();

        var expected = GoogleIncidentParser.ParseIncidents(json, Now);
        var actual = GoogleIncidentParser.ParseIncidents(payload.AsMemory(), Now);

        actual.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void ParseIncidents_Utf8EmptyWhitespaceOrOversized_ReturnsNull()
    {
        GoogleIncidentParser.ParseIncidents(ReadOnlyMemory<byte>.Empty, Now).Should().BeNull();
        GoogleIncidentParser.ParseIncidents("   \r\n\t"u8.ToArray().AsMemory(), Now).Should().BeNull();
        var oversized = new byte[GoogleIncidentParser.MaxFeedBytes + 1];
        Array.Fill(oversized, (byte)' ');
        GoogleIncidentParser.ParseIncidents(oversized.AsMemory(), Now).Should().BeNull();
    }
}
