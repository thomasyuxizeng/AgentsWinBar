using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Models;

[TestClass]
public class ProviderStatusSerializationTests
{
    [TestMethod]
    public void ProviderStatus_WithIncidentFields_RoundTripsThroughJson()
    {
        var original = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Major,
            Description = "Partially Degraded Service",
            Url = "https://status.claude.com",
            UpdatedAt = new DateTime(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc),
            IncidentTitle = "Elevated errors on platform.claude.com",
            LatestUpdateText = "We have mitigated errors loading usage data.",
            LatestUpdateAt = new DateTime(2026, 10, 11, 10, 15, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(original, AppJsonSerializerContext.Default.ProviderStatus);
        var deserialized = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ProviderStatus);

        deserialized.Should().NotBeNull();
        deserialized.Should().Be(original);
        deserialized!.IncidentTitle.Should().Be("Elevated errors on platform.claude.com");
        deserialized.LatestUpdateText.Should().Be("We have mitigated errors loading usage data.");
        deserialized.LatestUpdateAt.Should().Be(new DateTime(2026, 10, 11, 10, 15, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    public void CacheData_WithIncidentFields_RoundTripsThroughJson()
    {
        var status = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Minor,
            Description = "Degraded performance",
            Url = "https://status.openai.com",
            UpdatedAt = new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc),
            IncidentTitle = "Delayed Costs data",
            LatestUpdateText = "Mitigation in progress.",
            LatestUpdateAt = new DateTime(2026, 10, 11, 8, 30, 0, DateTimeKind.Utc)
        };

        var cache = new CacheData
        {
            Timestamp = DateTime.UtcNow,
            Items = new()
            {
                ["codex"] = new UsageData
                {
                    Provider = "codex",
                    Status = "cli",
                    ServiceStatus = status
                }
            }
        };

        var json = JsonSerializer.Serialize(cache, AppJsonSerializerContext.Default.CacheData);
        var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CacheData);

        loaded.Should().NotBeNull();
        loaded!.Items["codex"].ServiceStatus.Should().Be(status);
        loaded.Items["codex"].ServiceStatus!.IncidentTitle.Should().Be("Delayed Costs data");
        loaded.Items["codex"].ServiceStatus!.LatestUpdateText.Should().Be("Mitigation in progress.");
        loaded.Items["codex"].ServiceStatus!.LatestUpdateAt.Should().Be(new DateTime(2026, 10, 11, 8, 30, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    public void OldJson_WithoutIncidentFields_DeserializesCleanlyWithNulls()
    {
        // Old cache.json format without incidentTitle, latestUpdateText, or latestUpdateAt
        var oldJson = """
            {
              "timestamp": "2026-10-10T12:00:00Z",
              "items": {
                "claude": {
                  "provider": "claude",
                  "status": "cli",
                  "fetchedAt": "2026-10-10T12:00:00Z",
                  "serviceStatus": {
                    "indicator": 2,
                    "description": "Partially Degraded Service",
                    "url": "https://status.claude.com",
                    "updatedAt": "2026-10-10T11:45:00Z"
                  }
                }
              }
            }
            """;

        var cache = JsonSerializer.Deserialize(oldJson, AppJsonSerializerContext.Default.CacheData);

        cache.Should().NotBeNull();
        cache!.Items.Should().ContainKey("claude");
        var status = cache.Items["claude"].ServiceStatus;
        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        status.Description.Should().Be("Partially Degraded Service");
        status.Url.Should().Be("https://status.claude.com");
        status.IncidentTitle.Should().BeNull();
        status.LatestUpdateText.Should().BeNull();
        status.LatestUpdateAt.Should().BeNull();

        // Also test old CLI JSON format without incident fields
        var oldCliJson = """
            {
              "provider": "claude",
              "source": "cli",
              "status": {
                "indicator": "minor",
                "description": "Partially Degraded Service",
                "url": "https://status.claude.com"
              }
            }
            """;
        var cliData = JsonSerializer.Deserialize(oldCliJson, AppJsonSerializerContext.Default.UsageDataDto)!.ToUsageData();
        cliData.ServiceStatus.Should().NotBeNull();
        cliData.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        cliData.ServiceStatus.IncidentTitle.Should().BeNull();
        cliData.ServiceStatus.LatestUpdateText.Should().BeNull();
        cliData.ServiceStatus.LatestUpdateAt.Should().BeNull();
    }

    [TestMethod]
    public void ProviderStatusDto_ToProviderStatus_MapsAllIncidentProperties()
    {
        var dto = new ProviderStatusDto
        {
            Indicator = "major",
            Description = "Major Outage",
            Url = "https://status.claude.com",
            UpdatedAt = new DateTime(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc),
            IncidentTitle = "Elevated 500 errors",
            LatestUpdateText = "Mitigating issue.",
            LatestUpdateAt = new DateTime(2026, 10, 11, 10, 5, 0, DateTimeKind.Utc)
        };

        var status = dto.ToProviderStatus();

        status.Indicator.Should().Be(ProviderStatusIndicator.Major);
        status.Description.Should().Be("Major Outage");
        status.Url.Should().Be("https://status.claude.com");
        status.UpdatedAt.Should().Be(new DateTime(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc));
        status.IncidentTitle.Should().Be("Elevated 500 errors");
        status.LatestUpdateText.Should().Be("Mitigating issue.");
        status.LatestUpdateAt.Should().Be(new DateTime(2026, 10, 11, 10, 5, 0, DateTimeKind.Utc));
    }
}
