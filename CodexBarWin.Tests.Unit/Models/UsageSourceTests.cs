using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Models;

/// <summary>The per-provider usage source setting: "native" by default, "wsl" as a fallback.</summary>
[TestClass]
public class UsageSourceTests
{
    [TestMethod]
    [DataRow("wsl", "wsl")]
    [DataRow(" WSL ", "wsl")]
    [DataRow("native", "native")]
    [DataRow("Native", "native")]
    [DataRow("cli", "native")]
    [DataRow("", "native")]
    [DataRow(null, "native")]
    public void Normalize_StoredValue_ReturnsKnownSource(string? stored, string expected)
    {
        UsageSource.Normalize(stored).Should().Be(expected);
    }

    [TestMethod]
    public void GetDefaults_UsageSources_AreNativeForEveryProvider()
    {
        var settings = AppSettings.GetDefaults();

        settings.UsageSources.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["claude"] = "native", ["codex"] = "native", ["gemini"] = "native"
        });
        settings.AllEnabledUsageNative().Should().BeTrue();
    }

    [TestMethod]
    public void Normalize_SettingsWrittenBeforeUsageSources_LoadAsNative()
    {
        var settings = JsonSerializer.Deserialize("""{"providers":[{"id":"claude","isEnabled":true}],"refreshIntervalSeconds":300}""",
            AppJsonSerializerContext.Default.AppSettings)!;

        settings.Normalize();

        settings.GetUsageSource("claude").Should().Be(UsageSource.Native);
        settings.UsageSources.Values.Should().OnlyContain(v => v == UsageSource.Native);
    }

    [TestMethod]
    public void Normalize_NullUsageSources_RestoresDefaults()
    {
        var settings = new AppSettings { UsageSources = null! };

        settings.Normalize();

        settings.UsageSources.Should().HaveCount(3).And.OnlyContain(kv => kv.Value == UsageSource.Native);
    }

    [TestMethod]
    public void Normalize_UnknownValuesAndKeys_BecomeNativeAndAreDropped()
    {
        var settings = new AppSettings
        {
            UsageSources = new()
            {
                ["CLAUDE"] = "WSL",
                ["codex"] = "carrier-pigeon",
                ["openai"] = "wsl",
                ["gemini; rm -rf /"] = "wsl"
            }
        };

        settings.Normalize();

        settings.UsageSources.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["claude"] = "wsl", ["codex"] = "native", ["gemini"] = "native"
        });
    }

    [TestMethod]
    public void GetUsageSource_InvalidProvider_ThrowsArgumentException()
    {
        var act = () => AppSettings.GetDefaults().GetUsageSource("openai");

        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void AllEnabledUsageNative_EnabledProviderSetToWsl_ReturnsFalse()
    {
        var settings = AppSettings.GetDefaults();
        settings.UsageSources["codex"] = UsageSource.Wsl;

        settings.AllEnabledUsageNative().Should().BeFalse();
        settings.UsesNativeUsage("codex").Should().BeFalse();
        settings.UsesNativeUsage("claude").Should().BeTrue();
    }

    [TestMethod]
    public void AllEnabledUsageNative_OnlyDisabledProviderSetToWsl_ReturnsTrue()
    {
        var settings = AppSettings.GetDefaults();
        settings.UsageSources["codex"] = UsageSource.Wsl;
        settings.Providers.Single(p => p.Id == "codex").IsEnabled = false;

        settings.AllEnabledUsageNative().Should().BeTrue();
    }

    [TestMethod]
    public void AllEnabledUsageNative_NoProviders_ReturnsTrue()
    {
        new AppSettings { Providers = [] }.AllEnabledUsageNative().Should().BeTrue();
    }
}
