using AgentsWinBar.Models;
using AwesomeAssertions;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public class AppSettingsTests
{
    [TestMethod]
    [DataRow(ResetAnimationMode.WhenNotBatterySaver)]
    [DataRow(ResetAnimationMode.Always)]
    [DataRow(ResetAnimationMode.Never)]
    public void Normalize_ResetAnimationMode_ValidModesRoundTrip(ResetAnimationMode mode)
    {
        var settings = new AppSettings { ResetAnimationMode = mode };
        settings.Normalize();
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);
        System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)!
            .ResetAnimationMode.Should().Be(mode);
        json.Should().Contain("resetAnimationMode");
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(99)]
    public void Normalize_UndefinedResetAnimationMode_FallsBackToDefault(int value)
    {
        var settings = new AppSettings { ResetAnimationMode = (ResetAnimationMode)value };
        settings.Normalize();
        settings.ResetAnimationMode.Should().Be(ResetAnimationMode.WhenNotBatterySaver);
    }

    [TestMethod]
    public void Deserialize_OldSettingsWithoutResetAnimationMode_UsesDefault()
    {
        new AppSettings().ResetAnimationMode.Should().Be(ResetAnimationMode.WhenNotBatterySaver);
        AppSettings.GetDefaults().ResetAnimationMode.Should().Be(ResetAnimationMode.WhenNotBatterySaver);
        System.Text.Json.JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettings)!
            .ResetAnimationMode.Should().Be(ResetAnimationMode.WhenNotBatterySaver);
    }

    [TestMethod]
    [DataRow(-1, 30)]
    [DataRow(1441, 30)]
    [DataRow(0, 0)]
    [DataRow(1440, 1440)]
    [DataRow(60, 60)]
    public void Normalize_LockedRefreshInterval_ValidatesIncludingDisabled(int value, int expected)
    {
        var settings = AppSettings.GetDefaults();
        settings.LockedRefreshIntervalMinutes = value;
        settings.Normalize();
        settings.LockedRefreshIntervalMinutes.Should().Be(expected);
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);
        System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)!
            .LockedRefreshIntervalMinutes.Should().Be(expected);
    }

    [TestMethod]
    public void Deserialize_LockedRefreshIntervalMissing_DefaultsToThirtyMinutes()
    {
        new AppSettings().LockedRefreshIntervalMinutes.Should().Be(30);
        AppSettings.GetDefaults().LockedRefreshIntervalMinutes.Should().Be(30);
        System.Text.Json.JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettings)!
            .LockedRefreshIntervalMinutes.Should().Be(30);
    }
    [TestMethod]
    public void GetDefaults_AdaptiveRefreshEnabled_DefaultsToTrue()
    {
        AppSettings.GetDefaults().AdaptiveRefreshEnabled.Should().BeTrue();
        new AppSettings().AdaptiveRefreshEnabled.Should().BeTrue();
    }

    [TestMethod]
    public void Deserialize_AdaptiveRefreshOptionMissing_DefaultsToTrue()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettings)!;
        settings.Normalize();
        settings.AdaptiveRefreshEnabled.Should().BeTrue();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Serialize_AdaptiveRefreshOption_RoundTrips(bool enabled)
    {
        var settings = AppSettings.GetDefaults();
        settings.AdaptiveRefreshEnabled = enabled;
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);
        json.Should().Contain("\"adaptiveRefreshEnabled\": " + enabled.ToString().ToLowerInvariant());
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)!;
        restored.Normalize();
        restored.AdaptiveRefreshEnabled.Should().Be(enabled);
    }

    [TestMethod]
    public void Normalize_NullSectionsAndInvalidNumbers_RecoversDefaults()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize(
            """{"providers":null,"timeouts":null,"animation":null,"refreshIntervalSeconds":-1,"cacheExpiryMinutes":0,"theme":99}""",
            AppJsonSerializerContext.Default.AppSettings)!;
        settings.Normalize();
        settings.Providers.Should().HaveCount(3);
        settings.Timeouts.Should().NotBeNull();
        settings.Animation.Steps.Should().Be(15);
        settings.RefreshIntervalSeconds.Should().Be(120);
        settings.CacheExpiryMinutes.Should().Be(5);
        settings.Theme.Should().Be(AppTheme.System);
    }

    [TestMethod]
    public void Normalize_DuplicateNullAndInvalidProviders_KeepsUniqueNormalizedProviders()
    {
        var settings = new AppSettings { Providers =
            [null!, new() { Id = "CLAUDE", IsEnabled = false }, new() { Id = "claude" }, new() { Id = "codex; exit" }, new() { Id = "gemini" }] };
        settings.Normalize();
        settings.Providers.Select(p => p.Id).Should().Equal("claude", "gemini");
        settings.Providers[0].IsEnabled.Should().BeFalse();
    }

    [TestMethod]
    public void Normalize_EmptyProvidersAndValidCustomValues_PreservesThem()
    {
        var settings = new AppSettings { Providers = [], RefreshIntervalSeconds = 15, CacheExpiryMinutes = 30,
            Theme = AppTheme.Light, CodexBarGuideUrl = "https://example.com/guide" };
        settings.Animation.Steps = 20;
        settings.Timeouts.WslCommandTimeoutSeconds = 120;
        settings.Normalize();
        settings.Providers.Should().BeEmpty();
        settings.RefreshIntervalSeconds.Should().Be(15);
        settings.CacheExpiryMinutes.Should().Be(30);
        settings.Theme.Should().Be(AppTheme.Light);
        settings.Animation.Steps.Should().Be(20);
        settings.Timeouts.WslCommandTimeoutSeconds.Should().Be(120);
        settings.CodexBarGuideUrl.Should().Be("https://example.com/guide");
    }

    [TestMethod]
    public void Normalize_ZeroOrExcessiveAnimationAndTimeouts_UsesSafeDefaults()
    {
        var settings = AppSettings.GetDefaults();
        settings.RefreshIntervalSeconds = int.MaxValue;
        settings.CacheExpiryMinutes = int.MaxValue;
        settings.Animation = new() { Steps = 0, ShowDurationMs = -1, HideDurationMs = int.MaxValue };
        settings.Timeouts = new() { WslCommandTimeoutSeconds = 0, WslStatusCheckTimeoutSeconds = -1,
            CliProviderFirstFetchTimeoutSeconds = int.MaxValue, CliProviderTimeoutSeconds = -1,
            StandardProviderFirstFetchTimeoutSeconds = 0, StandardProviderTimeoutSeconds = int.MaxValue };
        settings.Normalize();
        settings.RefreshIntervalSeconds.Should().Be(120);
        settings.CacheExpiryMinutes.Should().Be(5);
        settings.Animation.Should().BeEquivalentTo(new AnimationSettings());
        settings.Timeouts.Should().BeEquivalentTo(new TimeoutSettings());
    }

    [TestMethod]
    [DataRow("file:///C:/Windows/System32/cmd.exe")]
    [DataRow("javascript:alert(1)")]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("https://github.com/steipete/CodexBar#installation")]
    public void Normalize_NonWebGuideUrl_UsesDefault(string? url)
    {
        var settings = new AppSettings { CodexBarGuideUrl = url! };
        settings.Normalize();
        settings.CodexBarGuideUrl.Should().Be(AppSettings.GetDefaults().CodexBarGuideUrl);
    }

    [TestMethod]
    public void ShowCostTotals_WhenOldSettingsOrDefaults_IsOnAndRoundTripsOff()
    {
        AppSettings.GetDefaults().ShowCostTotals.Should().BeTrue();
        var old = System.Text.Json.JsonSerializer.Deserialize("{}", AppJsonSerializerContext.Default.AppSettings)!;
        old.ShowCostTotals.Should().BeTrue();
        old.ShowCostTotals = false;
        var json = System.Text.Json.JsonSerializer.Serialize(old, AppJsonSerializerContext.Default.AppSettings);
        System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)!.ShowCostTotals.Should().BeFalse();
    }
    [TestMethod]
    public void ShowGraphs_WhenOldSettingsOrDefaults_IsOnAndRoundTripsOffThroughNormalize()
    {
        ProviderConfig.GetDefaults().Should().OnlyContain(p => p.ShowGraphs);
        var old = System.Text.Json.JsonSerializer.Deserialize(
            "{\"providers\":[{\"id\":\"claude\",\"displayName\":\"Claude\"}]}", AppJsonSerializerContext.Default.AppSettings)!;
        old.Providers.Single().ShowGraphs.Should().BeTrue();

        old.Providers.Single().ShowGraphs = false;
        old.Normalize();
        var json = System.Text.Json.JsonSerializer.Serialize(old, AppJsonSerializerContext.Default.AppSettings);
        System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)!
            .Providers.Single().ShowGraphs.Should().BeFalse();
    }

    [TestMethod]
    public void GetDefaults_ReturnsValidDefaults()
    {
        // Act
        var settings = AppSettings.GetDefaults();

        // Assert
        settings.Should().NotBeNull();
        settings.Version.Should().Be(1);
        settings.Providers.Should().HaveCount(3);
        settings.RefreshIntervalSeconds.Should().Be(120);
        settings.StartWithWindows.Should().BeFalse();
        settings.StartMinimized.Should().BeTrue();
        settings.Theme.Should().Be(AppTheme.System);
    }

    [TestMethod]
    public void GetDefaults_IncludesAllProviders()
    {
        // Act
        var settings = AppSettings.GetDefaults();

        // Assert
        settings.Providers.Should().Contain(p => p.Id == "claude");
        settings.Providers.Should().Contain(p => p.Id == "codex");
        settings.Providers.Should().Contain(p => p.Id == "gemini");
    }

    [TestMethod]
    public void GetDefaults_ProvidersAreEnabled()
    {
        // Act
        var settings = AppSettings.GetDefaults();

        // Assert
        settings.Providers.Should().AllSatisfy(p => p.IsEnabled.Should().BeTrue());
    }

    [TestMethod]
    public void GetDefaults_IncludesTimeoutSettings()
    {
        // Act
        var settings = AppSettings.GetDefaults();

        // Assert
        settings.Timeouts.Should().NotBeNull();
        settings.Timeouts.WslCommandTimeoutSeconds.Should().Be(30);
        settings.Timeouts.WslStatusCheckTimeoutSeconds.Should().Be(10);
        settings.Timeouts.CliProviderFirstFetchTimeoutSeconds.Should().Be(60);
        settings.Timeouts.CliProviderTimeoutSeconds.Should().Be(45);
        settings.Timeouts.StandardProviderFirstFetchTimeoutSeconds.Should().Be(20);
        settings.Timeouts.StandardProviderTimeoutSeconds.Should().Be(10);
    }

    [TestMethod]
    public void GetDefaults_IncludesAnimationSettings()
    {
        // Act
        var settings = AppSettings.GetDefaults();

        // Assert
        settings.Animation.Should().NotBeNull();
        settings.Animation.ShowDurationMs.Should().Be(150);
        settings.Animation.HideDurationMs.Should().Be(100);
        settings.Animation.Steps.Should().Be(15);
    }

    [TestMethod]
    public void GetDefaults_IncludesCacheExpiry()
    {
        // Act
        var settings = AppSettings.GetDefaults();

        // Assert
        settings.CacheExpiryMinutes.Should().Be(5);
    }

    [TestMethod]
    public void GetDefaults_IncludesGuideUrl()
    {
        // Act
        var settings = AppSettings.GetDefaults();

        // Assert
        settings.CodexBarGuideUrl.Should().NotBeNullOrEmpty();
        settings.CodexBarGuideUrl.Should().StartWith("https://");
    }

    [TestMethod]
    public void GetDefaults_OverlayIsOffAndHasNoSavedPosition()
    {
        // Act
        var settings = AppSettings.GetDefaults();

        // Assert
        settings.AlwaysOnOverlayEnabled.Should().BeFalse();
        settings.OverlayX.Should().BeNull();
        settings.OverlayY.Should().BeNull();
    }

    [TestMethod]
    public void AlertsEnabled_IsOnByDefaultAndMissingFieldInOlderFilesKeepsDefault()
    {
        AppSettings.GetDefaults().AlertsEnabled.Should().BeTrue();

        var older = System.Text.Json.JsonSerializer.Deserialize("""{"Version":1}""", AppJsonSerializerContext.Default.AppSettings);
        older!.AlertsEnabled.Should().BeTrue();

        var off = System.Text.Json.JsonSerializer.Deserialize("""{"AlertsEnabled":false}""", AppJsonSerializerContext.Default.AppSettings);
        off!.AlertsEnabled.Should().BeFalse();
    }

    [TestMethod]
    public void TaskbarDisplay_IsOffByDefaultAndIndependentOfOverlay()
    {
        // Arrange
        var settings = AppSettings.GetDefaults();

        // Act
        settings.AlwaysOnOverlayEnabled = true;

        // Assert
        settings.TaskbarDisplayEnabled.Should().BeFalse();
    }

    [TestMethod]
    public void TaskbarDisplay_RoundTripsThroughJsonAndMissingFieldDefaultsToOff()
    {
        // Arrange
        var settings = AppSettings.GetDefaults();
        settings.TaskbarDisplayEnabled = true;

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings);
        var older = System.Text.Json.JsonSerializer.Deserialize("""{"Version":1}""", AppJsonSerializerContext.Default.AppSettings);

        // Assert
        loaded!.TaskbarDisplayEnabled.Should().BeTrue();
        older!.TaskbarDisplayEnabled.Should().BeFalse();
    }

    [TestMethod]
    public void Overlay_SettingsRoundTripThroughJson()
    {
        // Arrange
        var settings = AppSettings.GetDefaults();
        settings.AlwaysOnOverlayEnabled = true;
        settings.OverlayX = -1500;   // negative = a monitor left of the primary
        settings.OverlayY = 42;

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.AlwaysOnOverlayEnabled.Should().BeTrue();
        loaded.OverlayX.Should().Be(-1500);
        loaded.OverlayY.Should().Be(42);
    }

    [TestMethod]
    public void Overlay_OldSettingsFileWithoutOverlayFieldsStillLoads()
    {
        // Arrange - settings.json written by a version before the overlay existed
        const string json = """{"Version":1,"RefreshIntervalSeconds":120}""";

        // Act
        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.AlwaysOnOverlayEnabled.Should().BeFalse();
        loaded.OverlayX.Should().BeNull();
    }
}

[TestClass]
public class TimeoutSettingsTests
{
    [TestMethod]
    public void DefaultConstructor_SetsDefaultValues()
    {
        // Act
        var settings = new TimeoutSettings();

        // Assert
        settings.WslCommandTimeoutSeconds.Should().Be(30);
        settings.WslStatusCheckTimeoutSeconds.Should().Be(10);
        settings.CliProviderFirstFetchTimeoutSeconds.Should().Be(60);
        settings.CliProviderTimeoutSeconds.Should().Be(45);
        settings.StandardProviderFirstFetchTimeoutSeconds.Should().Be(20);
        settings.StandardProviderTimeoutSeconds.Should().Be(10);
    }
}

[TestClass]
public class AnimationSettingsTests
{
    [TestMethod]
    public void DefaultConstructor_SetsDefaultValues()
    {
        // Act
        var settings = new AnimationSettings();

        // Assert
        settings.ShowDurationMs.Should().Be(150);
        settings.HideDurationMs.Should().Be(100);
        settings.Steps.Should().Be(15);
    }
}
