using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Models;

[TestClass]
public class ProviderStatusTests
{
    [TestMethod]
    [DataRow("none", ProviderStatusIndicator.None)]
    [DataRow("minor", ProviderStatusIndicator.Minor)]
    [DataRow("major", ProviderStatusIndicator.Major)]
    [DataRow("critical", ProviderStatusIndicator.Critical)]
    [DataRow("maintenance", ProviderStatusIndicator.Maintenance)]
    [DataRow("future-indicator", ProviderStatusIndicator.Unknown)]
    public void ToUsageData_WhenStatusPresent_ParsesIndicatorAndMetadata(string indicator, ProviderStatusIndicator expected)
    {
        var json = $$$"""
            {"provider":"claude","source":"cli","usage":{"primary":{"usedPercent":42}},
             "status":{"indicator":"{{{indicator}}}","description":"Provider report",
                       "url":"https://status.claude.com/","updatedAt":"2026-10-04T09:45:21Z"}}
            """;
        var data = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.UsageDataDto)!.ToUsageData();

        data.ServiceStatus!.Indicator.Should().Be(expected);
        data.ServiceStatus.Description.Should().Be("Provider report");
        data.ServiceStatus.Url.Should().Be("https://status.claude.com/");
        data.ServiceStatus.UpdatedAt.Should().Be(new DateTime(2026, 10, 4, 9, 45, 21, DateTimeKind.Utc));
        data.Status.Should().Be("cli");
        data.Session!.Percent.Should().Be(42);
        data.HasError.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"status\":null}")]
    public void ToUsageData_WhenStatusMissing_HasNoServiceStatusOrError(string json)
    {
        var data = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.UsageDataDto)!.ToUsageData();
        data.ServiceStatus.Should().BeNull();
        data.HasError.Should().BeFalse();
    }

    [TestMethod]
    public void ToUsageData_WhenIndicatorMissing_IsUnknown()
    {
        var data = JsonSerializer.Deserialize("""{"status":{"description":"Unknown health"}}""",
            AppJsonSerializerContext.Default.UsageDataDto)!.ToUsageData();
        data.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Unknown);
        data.HasError.Should().BeFalse();
    }

    [TestMethod]
    public void ServiceStatus_WhenCached_SurvivesSourceGeneratedRoundTrip()
    {
        var status = new ProviderStatus { Indicator = ProviderStatusIndicator.Major, Description = "Incident",
            Url = "https://status.openai.com/", UpdatedAt = DateTime.UtcNow };
        var cache = new CacheData { Items = new() { ["codex"] = new UsageData { Provider = "codex", Status = "cli", ServiceStatus = status } } };
        var json = JsonSerializer.Serialize(cache, AppJsonSerializerContext.Default.CacheData);
        var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CacheData)!;
        loaded.Items["codex"].ServiceStatus.Should().Be(status);
        loaded.Items["codex"].Status.Should().Be("cli");
    }
}
