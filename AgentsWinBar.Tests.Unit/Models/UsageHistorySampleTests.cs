using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public class UsageHistorySampleTests
{
    [TestMethod]
    public void Sample_WhenSerialized_RoundTripsUtcAndNullableReset()
    {
        var samples = new List<UsageHistorySample> {
            new() { Timestamp = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc), Provider = "claude", WindowKey = "session", UsedPercent = 42.5, ResetsAt = null },
            new() { Timestamp = new DateTime(2026, 10, 4, 11, 0, 0, DateTimeKind.Utc), Provider = "gemini", WindowKey = "extra:3p-5h", UsedPercent = 10, ResetsAt = new DateTime(2026, 10, 4, 15, 0, 0, DateTimeKind.Utc) } };
        var json = JsonSerializer.Serialize(samples, AppJsonSerializerContext.Default.ListUsageHistorySample);
        json.Should().Contain("\"usedPercent\": 42.5");
        var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageHistorySample)!;
        loaded.Should().Equal(samples);
        loaded[0].Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    [TestMethod]
    public void UsageDto_WhenFractional_KeepsHalfPointChangesAndExtraWindowIds()
    {
        var dto = JsonSerializer.Deserialize("""
            {"provider":"claude","usage":{"primary":{"usedPercent":10.5,"windowMinutes":300},
              "extraRateWindows":[{"id":"stable-id","title":"Fable only","window":{"usedPercent":12.25}}]}}
            """, AppJsonSerializerContext.Default.UsageDataDto)!;
        var data = dto.ToUsageData();
        data.Session!.Used.Should().Be(10);
        data.Session.Percent.Should().Be(10.5);
        data.ExtraWindows[0].Key.Should().Be("stable-id");
        data.ExtraWindows[0].Window.Percent.Should().Be(12.25);
        new UsageWindow { Used = 1, Limit = 4 }.Percent.Should().Be(25);
    }
}
