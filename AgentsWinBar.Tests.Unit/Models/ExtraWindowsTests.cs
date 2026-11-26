using AwesomeAssertions;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public class ExtraWindowsTests
{
    private const string AntigravityStyleJson = """
        [{
          "provider": "gemini",
          "source": "cli",
          "usage": {
            "primary":   { "usedPercent": 1,  "windowMinutes": 300,   "resetsAt": "2026-10-04T09:42:25Z" },
            "secondary": { "usedPercent": 2,  "windowMinutes": 10080, "resetsAt": "2026-10-11T03:05:45Z" },
            "extraRateWindows": [
              { "id": "3p-5h",     "title": "Claude and GPT 5-hour", "window": { "usedPercent": 100, "windowMinutes": 300,   "resetsAt": "2026-10-04T09:04:07Z" } },
              { "id": "3p-weekly", "title": "Claude and GPT weekly", "window": { "usedPercent": 50.2, "windowMinutes": 10080, "resetsAt": "2026-10-11T04:04:07Z" } }
            ]
          }
        }]
        """;

    private static UsageData Parse(string json) =>
        System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageDataDto)![0].ToUsageData();

    [TestMethod]
    public void ToUsageData_WhenCliReportsExtraWindows_KeepsThemWithTitlesAndWindowLengths()
    {
        var data = Parse(AntigravityStyleJson);

        data.HasExtraWindows.Should().BeTrue();
        data.ExtraWindows.Select(x => x.Title).Should().Equal("External 5h", "External 7d");
        data.ExtraWindows[0].Window.Percent.Should().Be(100);
        data.ExtraWindows[0].Window.WindowMinutes.Should().Be(300);
        data.ExtraWindows[1].Window.WindowMinutes.Should().Be(10080);
        data.ExtraWindows[1].Window.ResetAt.Should().NotBeNull();
    }

    [TestMethod]
    public void ToUsageData_DoesNotTouchTheMainWindows()
    {
        var data = Parse(AntigravityStyleJson);

        data.Session!.WindowMinutes.Should().Be(300);
        data.Weekly!.WindowMinutes.Should().Be(10080);
    }

    [TestMethod]
    public void ToUsageData_WhenNoExtraWindows_IsEmptyNotNull()
    {
        var data = Parse("""[{"provider":"claude","usage":{"primary":{"usedPercent":5,"windowMinutes":300}}}]""");

        data.ExtraWindows.Should().BeEmpty();
        data.HasExtraWindows.Should().BeFalse();
    }

    [TestMethod]
    public void ToUsageData_SkipsExtraWindowsWithoutATitleOrWindow()
    {
        var data = Parse("""
            [{"provider":"gemini","usage":{"extraRateWindows":[
              {"id":"a","title":"","window":{"usedPercent":1,"windowMinutes":300}},
              {"id":"b","title":"No window"},
              {"id":"c","title":"  Kept  ","window":{"usedPercent":2,"windowMinutes":300}}
            ]}}]
            """);

        data.ExtraWindows.Should().ContainSingle().Which.Title.Should().Be("Kept");
    }

    [TestMethod]
    public void ExtraWindows_SurviveTheDiskCacheRoundTrip()
    {
        var cache = new CacheData
        {
            Timestamp = DateTime.UtcNow,
            Items = new Dictionary<string, UsageData> { ["gemini"] = Parse(AntigravityStyleJson) }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(cache, AppJsonSerializerContext.Default.CacheData);
        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CacheData)!;

        loaded.Items["gemini"].ExtraWindows.Should().HaveCount(2);
        loaded.Items["gemini"].ExtraWindows[0].Title.Should().Be("External 5h");
        loaded.Items["gemini"].ExtraWindows[0].Window.WindowMinutes.Should().Be(300);
    }
}
