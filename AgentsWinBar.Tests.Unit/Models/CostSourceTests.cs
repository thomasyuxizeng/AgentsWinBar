using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public sealed class CostSourceTests
{
    [TestMethod]
    public void GetDefaults_WhenCreated_UsesNativeCosts()
    {
        AppSettings.GetDefaults().CostSource.Should().Be(UsageSource.Native);
        AppSettings.GetDefaults().UsesNativeCost().Should().BeTrue();
        new AppSettings().UsesNativeCost().Should().BeTrue();
    }

    [TestMethod]
    [DataRow("wsl", "wsl")]
    [DataRow(" WSL ", "wsl")]
    [DataRow("Wsl", "wsl")]
    [DataRow("native", "native")]
    [DataRow("NATIVE", "native")]
    [DataRow("cli", "native")]
    [DataRow("", "native")]
    [DataRow(null, "native")]
    public void Normalize_WhenCostSourceIsStored_KeepsWslAndTurnsEverythingElseIntoNative(string? stored, string expected)
    {
        var settings = new AppSettings { CostSource = stored! };

        settings.UsesNativeCost().Should().Be(expected == UsageSource.Native);
        settings.Normalize();

        settings.CostSource.Should().Be(expected);
    }

    [TestMethod]
    public void Deserialize_WhenSettingsFilePredatesTheSetting_LoadsNative()
    {
        var settings = JsonSerializer.Deserialize("""{"version":1,"showCostTotals":true}""", AppJsonSerializerContext.Default.AppSettings)!;
        settings.Normalize();

        settings.CostSource.Should().Be(UsageSource.Native);
    }

    [TestMethod]
    public void Serialize_WhenCostSourceIsWsl_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new AppSettings { CostSource = UsageSource.Wsl }, AppJsonSerializerContext.Default.AppSettings);

        var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)!;
        loaded.Normalize();

        json.Should().Contain("\"costSource\"");
        loaded.UsesNativeCost().Should().BeFalse();
    }

    [TestMethod]
    public void UsesNativeCost_WhenUsageSourcesAreWsl_IsIndependentOfThem()
    {
        var settings = AppSettings.GetDefaults();
        settings.UsageSources["claude"] = UsageSource.Wsl;

        settings.UsesNativeCost().Should().BeTrue();
        settings.AllEnabledUsageNative().Should().BeFalse();
    }

    [TestMethod]
    public void ToCostData_WhenCliJsonHasNoUnpricedTokens_ReadsZero()
    {
        var dto = JsonSerializer.Deserialize("""{"provider":"claude","sessionTokens":5,"daily":[{"date":"2026-11-25","totalTokens":5}]}""",
            AppJsonSerializerContext.Default.CostDataDto)!;

        var data = dto.ToCostData("claude", new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc));

        dto.UnpricedTokens.Should().BeNull();
        data.UnpricedTokens.Should().Be(0);
        data.Daily.Single().UnpricedTokens.Should().Be(0);
    }

    [TestMethod]
    public void ToCostData_WhenNativeJsonHasUnpricedTokens_CarriesThem()
    {
        var dto = JsonSerializer.Deserialize(
            """{"provider":"claude","sessionTokens":50,"unpricedTokens":20,"daily":[{"date":"2026-11-25","totalTokens":50,"unpricedTokens":20}]}""",
            AppJsonSerializerContext.Default.CostDataDto)!;

        var data = dto.ToCostData("claude", new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc));

        data.UnpricedTokens.Should().Be(20);
        data.Daily.Single().UnpricedTokens.Should().Be(20);
        var cached = JsonSerializer.Serialize(new CostCacheFile { Items = { ["claude"] = data } }, AppJsonSerializerContext.Default.CostCacheFile);
        JsonSerializer.Deserialize(cached, AppJsonSerializerContext.Default.CostCacheFile)!.Items["claude"].UnpricedTokens.Should().Be(20);
    }
}
