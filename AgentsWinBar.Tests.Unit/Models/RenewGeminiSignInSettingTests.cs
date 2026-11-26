using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public sealed class RenewGeminiSignInSettingTests
{
    [TestMethod]
    public void RenewGeminiSignIn_WhenDefaultSettings_IsOn()
    {
        new AppSettings().RenewGeminiSignIn.Should().BeTrue();
        AppSettings.GetDefaults().RenewGeminiSignIn.Should().BeTrue();
    }

    [TestMethod]
    public void Deserialize_WhenSettingsFilePredatesTheSetting_LoadsOn()
    {
        var settings = JsonSerializer.Deserialize("""{"version":1,"showCostTotals":true}""", AppJsonSerializerContext.Default.AppSettings)!;
        settings.Normalize();

        settings.RenewGeminiSignIn.Should().BeTrue();
    }

    [TestMethod]
    public void Serialize_WhenTurnedOff_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new AppSettings { RenewGeminiSignIn = false }, AppJsonSerializerContext.Default.AppSettings);

        var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)!;
        loaded.Normalize();

        json.Should().Contain("\"renewGeminiSignIn\"");
        loaded.RenewGeminiSignIn.Should().BeFalse();
        loaded.RenewClaudeSignIn.Should().BeTrue("the Claude setting is independent");
    }
}
