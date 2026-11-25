using System.Text;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class StatuspageIncidentParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ParseSummary_NoIncidents_ReturnsNoneAllSystemsOperational()
    {
        var json = """
            {
              "page": {
                "name": "Claude",
                "url": "https://status.claude.com",
                "updated_at": "2026-10-11T11:00:00Z"
              },
              "status": {
                "indicator": "none",
                "description": "All Systems Operational"
              },
              "incidents": [],
              "scheduled_maintenances": []
            }
            """;

        var status = StatuspageIncidentParser.ParseSummary(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.None);
        status.Description.Should().Be("All Systems Operational");
        status.Url.Should().Be("https://status.claude.com");
        status.IncidentTitle.Should().BeNull();
        status.LatestUpdateText.Should().BeNull();
        status.LatestUpdateAt.Should().BeNull();
    }

    [TestMethod]
    public void ParseSummary_OpenMonitoringIncident_ExtractsTitleAndLatestUpdate()
    {
        var json = """
            {
              "page": {
                "name": "Claude",
                "url": "https://status.claude.com",
                "updated_at": "2026-10-10T15:14:30Z"
              },
              "status": {
                "indicator": "minor",
                "description": "Partially Degraded Service"
              },
              "incidents": [
                {
                  "id": "978mjkgw6mh7",
                  "name": "Elevated errors on platform.claude.com",
                  "status": "monitoring",
                  "created_at": "2026-10-07T13:25:04Z",
                  "updated_at": "2026-10-07T21:45:27Z",
                  "resolved_at": null,
                  "impact": "major",
                  "shortlink": "https://stspg.io/sjkw7njwf0mt",
                  "incident_updates": [
                    {
                      "id": "1nnyzdbn2qkg",
                      "status": "monitoring",
                      "body": "We've mitigated the errors loading usage data on **platform.claude.com**. API requests are unaffected.",
                      "created_at": "2026-10-07T21:44:41Z",
                      "display_at": "2026-10-07T21:44:41Z"
                    },
                    {
                      "id": "l3390gw3zj8k",
                      "status": "identified",
                      "body": "Older update body...",
                      "created_at": "2026-10-07T17:28:02Z",
                      "display_at": "2026-10-07T17:28:02Z"
                    }
                  ]
                }
              ],
              "scheduled_maintenances": []
            }
            """;

        var status = StatuspageIncidentParser.ParseSummary(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        status.Description.Should().Be("Partially Degraded Service");
        status.IncidentTitle.Should().Be("Elevated errors on platform.claude.com");
        status.LatestUpdateText.Should().Be("We've mitigated the errors loading usage data on platform.claude.com. API requests are unaffected.");
        status.LatestUpdateAt.Should().Be(new DateTime(2026, 10, 7, 21, 44, 41, DateTimeKind.Utc));
        status.Url.Should().Be("https://stspg.io/sjkw7njwf0mt");
    }

    [TestMethod]
    public void ParseSummary_OpenInvestigatingIncident_ExtractsTitleAndLatestUpdate()
    {
        var json = """
            {
              "page": { "url": "https://status.openai.com/" },
              "status": { "indicator": "critical", "description": "Major Outage" },
              "incidents": [
                {
                  "name": "API Outage across all models",
                  "status": "investigating",
                  "resolved_at": null,
                  "impact": "critical",
                  "incident_updates": [
                    {
                      "body": "We are currently investigating elevated API errors.",
                      "display_at": "2026-10-11T10:00:00Z"
                    }
                  ]
                }
              ]
            }
            """;

        var status = StatuspageIncidentParser.ParseSummary(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Critical);
        status.IncidentTitle.Should().Be("API Outage across all models");
        status.LatestUpdateText.Should().Be("We are currently investigating elevated API errors.");
        status.LatestUpdateAt.Should().Be(new DateTime(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    public void ParseSummary_OpenIdentifiedIncident_ExtractsTitleAndLatestUpdate()
    {
        var json = """
            {
              "page": { "url": "https://status.openai.com/" },
              "status": { "indicator": "minor", "description": "Partial Outage" },
              "incidents": [
                {
                  "name": "Latency in EU region",
                  "status": "identified",
                  "resolved_at": null,
                  "impact": "minor",
                  "incident_updates": [
                    {
                      "body": "Identified bad routing policy, mitigation rolling out.",
                      "display_at": "2026-10-11T11:15:00Z"
                    }
                  ]
                }
              ]
            }
            """;

        var status = StatuspageIncidentParser.ParseSummary(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        status.IncidentTitle.Should().Be("Latency in EU region");
        status.LatestUpdateText.Should().Be("Identified bad routing policy, mitigation rolling out.");
    }

    [TestMethod]
    public void ParseSummary_ResolvedIncident_IgnoredReturnsNone()
    {
        var json = """
            {
              "page": { "url": "https://status.claude.com" },
              "status": { "indicator": "none", "description": "All Systems Operational" },
              "incidents": [
                {
                  "name": "Resolved incident from yesterday",
                  "status": "resolved",
                  "resolved_at": "2026-10-10T18:00:00Z",
                  "impact": "minor",
                  "incident_updates": [
                    {
                      "body": "This incident has been resolved.",
                      "display_at": "2026-10-10T18:00:00Z"
                    }
                  ]
                },
                {
                  "name": "Postmortem published",
                  "status": "postmortem",
                  "resolved_at": "2026-10-09T12:00:00Z"
                }
              ]
            }
            """;

        var status = StatuspageIncidentParser.ParseSummary(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.None);
        status.IncidentTitle.Should().BeNull();
        status.LatestUpdateText.Should().BeNull();
    }

    [TestMethod]
    public void ParseSummary_MultipleIncidents_SelectsHighestImpactOrMostRecent()
    {
        var json = """
            {
              "page": { "url": "https://status.openai.com/" },
              "status": { "indicator": "major", "description": "Service Disruption" },
              "incidents": [
                {
                  "name": "Minor slow responses",
                  "status": "monitoring",
                  "resolved_at": null,
                  "impact": "minor",
                  "updated_at": "2026-10-11T11:00:00Z",
                  "incident_updates": [
                    { "body": "Monitoring minor issue", "display_at": "2026-10-11T11:00:00Z" }
                  ]
                },
                {
                  "name": "Major API 500 Spike",
                  "status": "investigating",
                  "resolved_at": null,
                  "impact": "major",
                  "updated_at": "2026-10-11T10:30:00Z",
                  "incident_updates": [
                    { "body": "Investigating major spike", "display_at": "2026-10-11T10:30:00Z" }
                  ]
                }
              ]
            }
            """;

        var status = StatuspageIncidentParser.ParseSummary(json, Now);

        status.Should().NotBeNull();
        // Major has higher impact rank than minor
        status!.IncidentTitle.Should().Be("Major API 500 Spike");
        status.LatestUpdateText.Should().Be("Investigating major spike");
    }

    [TestMethod]
    public void ParseSummary_ScheduledMaintenance_InProgress_ExtractsMaintenanceDetail()
    {
        var json = """
            {
              "page": { "url": "https://status.claude.com" },
              "status": { "indicator": "none", "description": "All Systems Operational" },
              "incidents": [],
              "scheduled_maintenances": [
                {
                  "name": "Scheduled Database Maintenance",
                  "status": "in_progress",
                  "scheduled_for": "2026-10-11T11:30:00Z",
                  "incident_updates": [
                    {
                      "body": "Database maintenance is currently in progress.",
                      "display_at": "2026-10-11T11:30:00Z"
                    }
                  ]
                }
              ]
            }
            """;

        var status = StatuspageIncidentParser.ParseSummary(json, Now);

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Maintenance);
        status.IncidentTitle.Should().Be("Scheduled Database Maintenance");
        status.LatestUpdateText.Should().Be("Database maintenance is currently in progress.");
        status.LatestUpdateAt.Should().Be(new DateTime(2026, 10, 11, 11, 30, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    public void ParseSummary_MissingFields_HandlesGracefully()
    {
        var json = """
            {
              "incidents": [
                {
                  "name": "Incident without updates",
                  "status": "investigating",
                  "created_at": "2026-10-11T08:00:00Z",
                  "incident_updates": []
                }
              ]
            }
            """;

        var status = StatuspageIncidentParser.ParseSummary(json, Now);

        status.Should().NotBeNull();
        status!.IncidentTitle.Should().Be("Incident without updates");
        status.LatestUpdateText.Should().BeNull();
        status.LatestUpdateAt.Should().Be(new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("<html>502 Bad Gateway</html>")]
    [DataRow("42")]
    public void ParseSummary_MalformedJson_ReturnsNullWithoutThrowing(string malformed)
    {
        var status = StatuspageIncidentParser.ParseSummary(malformed, Now);
        status.Should().BeNull();
    }

    [TestMethod]
    public void ParseSummary_Utf8BytesWithBom_MatchesStringOverload()
    {
        var json = """
            {
              "page": { "url": "https://status.claude.com", "updated_at": "2026-10-11T11:00:00Z" },
              "status": { "indicator": "minor", "description": "Partially Degraded Service" },
              "incidents": [
                {
                  "name": "Elevated errors on platform.claude.com",
                  "status": "monitoring",
                  "resolved_at": null,
                  "impact": "major",
                  "incident_updates": [
                    { "body": "Mitigated errors.", "display_at": "2026-10-11T11:45:00Z" }
                  ]
                }
              ]
            }
            """;
        var payload = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();

        var expected = StatuspageIncidentParser.ParseSummary(json, Now);
        var actual = StatuspageIncidentParser.ParseSummary(payload.AsMemory(), Now);

        actual.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public void ParseSummary_Utf8EmptyWhitespaceOrOversized_ReturnsNull()
    {
        StatuspageIncidentParser.ParseSummary(ReadOnlyMemory<byte>.Empty, Now).Should().BeNull();
        StatuspageIncidentParser.ParseSummary("   \r\n\t"u8.ToArray().AsMemory(), Now).Should().BeNull();
        var oversized = new byte[StatuspageIncidentParser.MaxFeedBytes + 1];
        Array.Fill(oversized, (byte)' ');
        StatuspageIncidentParser.ParseSummary(oversized.AsMemory(), Now).Should().BeNull();
    }
}
