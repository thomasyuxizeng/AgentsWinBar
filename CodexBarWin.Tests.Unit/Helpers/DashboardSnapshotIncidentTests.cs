using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class DashboardSnapshotIncidentTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private static UsageData Provider(string id = "claude") => new() { Provider = id, FetchedAt = Now };
    private static DashboardInputs Inputs(params UsageData[] providers) => new() { Providers = providers, NowUtc = Now };

    [TestMethod]
    public void Build_ProviderWithActiveIncident_ExposesIncidentProperties()
    {
        var updateTime = Now.AddHours(-2);
        var incidentStatus = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Minor,
            Description = "Degraded performance",
            Url = "https://status.claude.com",
            IncidentTitle = "Elevated API Errors",
            LatestUpdateText = "We are monitoring the mitigation.",
            LatestUpdateAt = updateTime
        };

        var snapshot = DashboardSnapshotBuilder.Build(Inputs(
            Provider("claude") with
            {
                Session = new UsageWindow { ReportedPercent = 20 },
                ServiceStatus = incidentStatus
            }));

        var claude = snapshot.Providers.Single(p => p.Id == "claude");
        claude.IncidentTitle.Should().Be("Elevated API Errors");
        claude.IncidentUpdateText.Should().Be("We are monitoring the mitigation.");
        claude.IncidentUpdateAtUtc.Should().Be(updateTime);
        snapshot.Summary.IncidentCount.Should().Be(1);
    }

    [TestMethod]
    public void Build_ProviderWithHealthyStatus_IncidentPropertiesAreNull()
    {
        var healthyStatus = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.None,
            Description = "All systems operational",
            IncidentTitle = "Should be ignored",
            LatestUpdateText = "Resolved text",
            LatestUpdateAt = Now.AddHours(-1)
        };

        var snapshot = DashboardSnapshotBuilder.Build(Inputs(
            Provider("codex") with
            {
                Session = new UsageWindow { ReportedPercent = 40 },
                ServiceStatus = healthyStatus
            }));

        var codex = snapshot.Providers.Single(p => p.Id == "codex");
        codex.IncidentTitle.Should().BeNull();
        codex.IncidentUpdateText.Should().BeNull();
        codex.IncidentUpdateAtUtc.Should().BeNull();
        snapshot.Summary.IncidentCount.Should().Be(0);
    }

    [TestMethod]
    public void Build_ProviderWithoutStatus_IncidentPropertiesAreNull()
    {
        var snapshot = DashboardSnapshotBuilder.Build(Inputs(
            Provider("gemini") with
            {
                Session = new UsageWindow { ReportedPercent = 10 },
                ServiceStatus = null
            }));

        var gemini = snapshot.Providers.Single(p => p.Id == "gemini");
        gemini.IncidentTitle.Should().BeNull();
        gemini.IncidentUpdateText.Should().BeNull();
        gemini.IncidentUpdateAtUtc.Should().BeNull();
        snapshot.Summary.IncidentCount.Should().Be(0);
    }

    [TestMethod]
    public void Build_MultipleProviders_IncidentSummaryCountMatchesAndFieldsMappedIndependently()
    {
        var claudeIncidentTime = Now.AddMinutes(-30);
        var geminiIncidentTime = Now.AddHours(-5);

        var snapshot = DashboardSnapshotBuilder.Build(Inputs(
            Provider("claude") with
            {
                Session = new UsageWindow { ReportedPercent = 15 },
                ServiceStatus = new ProviderStatus
                {
                    Indicator = ProviderStatusIndicator.Minor,
                    IncidentTitle = "Claude latency",
                    LatestUpdateText = "Investigating elevated latencies.",
                    LatestUpdateAt = claudeIncidentTime
                }
            },
            Provider("codex") with
            {
                Session = new UsageWindow { ReportedPercent = 50 },
                ServiceStatus = new ProviderStatus
                {
                    Indicator = ProviderStatusIndicator.None,
                    Description = "Operational"
                }
            },
            Provider("gemini") with
            {
                Session = new UsageWindow { ReportedPercent = 80 },
                ServiceStatus = new ProviderStatus
                {
                    Indicator = ProviderStatusIndicator.Major,
                    IncidentTitle = "Gemini model outage",
                    LatestUpdateText = "Engineers deployed a hotfix.",
                    LatestUpdateAt = geminiIncidentTime
                }
            }));

        snapshot.Summary.ProviderCount.Should().Be(3);
        snapshot.Summary.IncidentCount.Should().Be(2);

        var claude = snapshot.Providers.Single(p => p.Id == "claude");
        claude.IncidentTitle.Should().Be("Claude latency");
        claude.IncidentUpdateText.Should().Be("Investigating elevated latencies.");
        claude.IncidentUpdateAtUtc.Should().Be(claudeIncidentTime);

        var codex = snapshot.Providers.Single(p => p.Id == "codex");
        codex.IncidentTitle.Should().BeNull();
        codex.IncidentUpdateText.Should().BeNull();
        codex.IncidentUpdateAtUtc.Should().BeNull();

        var gemini = snapshot.Providers.Single(p => p.Id == "gemini");
        gemini.IncidentTitle.Should().Be("Gemini model outage");
        gemini.IncidentUpdateText.Should().Be("Engineers deployed a hotfix.");
        gemini.IncidentUpdateAtUtc.Should().Be(geminiIncidentTime);
    }
}
