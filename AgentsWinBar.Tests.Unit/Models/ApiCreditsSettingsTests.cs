using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public class ApiCreditsSettingsTests
{
    [TestMethod]
    public void GetDefaults_NewSettings_HasApiCreditsDefaults()
    {
        AppSettings.GetDefaults().ApiCreditsMonthlyUsd.Should().Be(200);
        AppSettings.GetDefaults().ApiCreditsCycleStartDay.Should().Be(1);
        new AppSettings().ApiCreditsMonthlyUsd.Should().Be(200);
        new AppSettings().ApiCreditsCycleStartDay.Should().Be(1);
    }

    [TestMethod]
    [DataRow(-1, 0, 200, 1)]
    [DataRow(0, 29, 200, 1)]
    [DataRow(100001, -1, 200, 1)]
    [DataRow(100000, 28, 100000, 28)]
    [DataRow(75, 15, 75, 15)]
    public void Normalize_BillingValues_RepairsOnlyInvalidValues(int monthly, int day, int expectedMonthly, int expectedDay)
    {
        var settings = new AppSettings { ApiCreditsMonthlyUsd = monthly, ApiCreditsCycleStartDay = day };
        settings.Normalize(); settings.ApiCreditsMonthlyUsd.Should().Be(expectedMonthly);
        settings.ApiCreditsCycleStartDay.Should().Be(expectedDay);
    }

    [TestMethod]
    public void Deserialize_OldJson_UsesDefaultsAndRoundTripsNewSettings()
    {
        var old = JsonSerializer.Deserialize("""{"showCostTotals":false,"refreshIntervalSeconds":60}""", AppJsonSerializerContext.Default.AppSettings)!;
        old.Normalize(); old.ApiCreditsMonthlyUsd.Should().Be(200); old.ApiCreditsCycleStartDay.Should().Be(1);
        old.ShowCostTotals.Should().BeFalse();
        old.ApiCreditsMonthlyUsd = 123.45m; old.ApiCreditsCycleStartDay = 12;
        var json = JsonSerializer.Serialize(old, AppJsonSerializerContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)!;
        restored.ApiCreditsMonthlyUsd.Should().Be(123.45m); restored.ApiCreditsCycleStartDay.Should().Be(12);
        json.Should().NotContain("ANTHROPIC_ADMIN_KEY"); json.Should().NotContain("spentUsd");
    }
}
