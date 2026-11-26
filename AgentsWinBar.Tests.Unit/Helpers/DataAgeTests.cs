using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class DataAgeTests
{
    private static readonly DateTime Fetched = new(2026, 10, 8, 6, 1, 0, DateTimeKind.Utc);

    [TestMethod]
    public void OldReadingUtc_ReadingTwelveHoursBeforeFetch_ReturnsReadingTime()
    {
        var reading = new DateTime(2026, 10, 7, 18, 39, 1, DateTimeKind.Utc);
        DataAge.OldReadingUtc(Fetched, reading).Should().Be(reading);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(14)]
    [DataRow(15)]
    public void OldReadingUtc_WithinFifteenMinutes_ReturnsNull(int minutesOld) =>
        DataAge.OldReadingUtc(Fetched, Fetched.AddMinutes(-minutesOld)).Should().BeNull();

    [TestMethod]
    public void OldReadingUtc_JustOverThreshold_ReturnsReadingTime() =>
        DataAge.OldReadingUtc(Fetched, Fetched.AddMinutes(-15).AddSeconds(-1)).Should().Be(Fetched.AddMinutes(-15).AddSeconds(-1));

    [TestMethod]
    public void OldReadingUtc_NoReadingTimeOrReadingAfterFetch_ReturnsNull()
    {
        DataAge.OldReadingUtc(Fetched, null).Should().BeNull();
        DataAge.OldReadingUtc(Fetched, Fetched.AddMinutes(30)).Should().BeNull();
    }

    [TestMethod]
    public void OldReadingUtc_LocalAndUnspecifiedKinds_AreComparedAsUtc()
    {
        var reading = Fetched.AddHours(-2);
        DataAge.OldReadingUtc(Fetched.ToLocalTime(), reading).Should().Be(reading);
        DataAge.OldReadingUtc(Fetched, DateTime.SpecifyKind(reading, DateTimeKind.Unspecified)).Should().Be(reading);
    }

    [TestMethod]
    public void ToUsageData_MapsTheCliUpdatedAtAsUtc()
    {
        const string json = """[{"provider":"claude","source":"oauth","usage":{"primary":{"usedPercent":9},"updatedAt":"2026-10-07T18:39:01Z"}}]""";
        var data = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageDataDto)![0].ToUsageData();
        data.DataUpdatedAt.Should().Be(new DateTime(2026, 10, 7, 18, 39, 1, DateTimeKind.Utc));
        data.DataUpdatedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [TestMethod]
    public void ToUsageData_WithoutUpdatedAt_LeavesItNull()
    {
        const string json = """[{"provider":"codex","usage":{"primary":{"usedPercent":9}}}]""";
        System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageDataDto)![0]
            .ToUsageData().DataUpdatedAt.Should().BeNull();
    }
}
