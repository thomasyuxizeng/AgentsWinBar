using AwesomeAssertions;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Models;

[TestClass]
public class ProviderConfigTests
{
    [TestMethod]
    public void GetDefaults_ReturnsThreeProviders()
    {
        // Act
        var defaults = ProviderConfig.GetDefaults();

        // Assert
        defaults.Should().NotBeNull();
        defaults.Should().HaveCount(3);
    }

    [TestMethod]
    public void GetDefaults_ProvidersHaveExpectedIdsAndOrder()
    {
        // Act
        var defaults = ProviderConfig.GetDefaults();

        // Assert
        defaults[0].Id.Should().Be("claude");
        defaults[0].DisplayName.Should().Be("Claude");
        defaults[0].Order.Should().Be(0);

        defaults[1].Id.Should().Be("codex");
        defaults[1].DisplayName.Should().Be("Codex");
        defaults[1].Order.Should().Be(1);

        defaults[2].Id.Should().Be("gemini");
        defaults[2].DisplayName.Should().Be("Gemini");
        defaults[2].Order.Should().Be(2);
    }

    [TestMethod]
    public void GetDefaults_AllProvidersAreEnabledByDefault()
    {
        // Act
        var defaults = ProviderConfig.GetDefaults();

        // Assert
        defaults.Should().AllSatisfy(p => p.IsEnabled.Should().BeTrue());
    }

    [TestMethod]
    public void ProviderConfig_PropertiesCanBeModified_PreservesUpdatedValues()
    {
        // Arrange
        var config = new ProviderConfig
        {
            Id = "claude",
            DisplayName = "Claude",
            IconPath = "Assets/claude.png",
            IsEnabled = false,
            Order = 10
        };

        // Act
        config.IsEnabled = true;
        config.Order = 0;

        // Assert
        config.Id.Should().Be("claude");
        config.DisplayName.Should().Be("Claude");
        config.IconPath.Should().Be("Assets/claude.png");
        config.IsEnabled.Should().BeTrue();
        config.Order.Should().Be(0);
    }

    [TestMethod]
    public void ProviderConfig_EqualityAndWithExpression_BehavesAsRecord()
    {
        // Arrange
        var original = new ProviderConfig
        {
            Id = "codex",
            DisplayName = "Codex",
            IsEnabled = true,
            Order = 1
        };

        // Act
        var clone = original with { };
        var modified = original with { Order = 5 };

        // Assert
        clone.Should().Be(original);
        modified.Should().NotBe(original);
        modified.Order.Should().Be(5);
        modified.Id.Should().Be("codex");
    }
}
