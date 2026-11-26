using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public sealed class RenewClaudeSignInSettingTests
{
    [TestMethod]
    public void RenewClaudeSignIn_WhenDefaultSettings_IsOn()
    {
        new AppSettings().RenewClaudeSignIn.Should().BeTrue();
        AppSettings.GetDefaults().RenewClaudeSignIn.Should().BeTrue();
    }

    [TestMethod]
    public void Deserialize_WhenSettingsFilePredatesTheSetting_LoadsOn()
    {
        var settings = JsonSerializer.Deserialize("""{"version":1,"showCostTotals":true}""", AppJsonSerializerContext.Default.AppSettings)!;
        settings.Normalize();

        settings.RenewClaudeSignIn.Should().BeTrue();
    }

    [TestMethod]
    public void Serialize_WhenTurnedOff_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new AppSettings { RenewClaudeSignIn = false }, AppJsonSerializerContext.Default.AppSettings);

        var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)!;
        loaded.Normalize();

        json.Should().Contain("\"renewClaudeSignIn\"");
        loaded.RenewClaudeSignIn.Should().BeFalse();
    }
}
