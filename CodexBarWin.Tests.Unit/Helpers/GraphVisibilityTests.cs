using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class GraphVisibilityTests
{
    private static List<ProviderConfig> Providers() =>
        [new() { Id = "claude" }, new() { Id = "codex", ShowGraphs = false }, new() { Id = "gemini" }];

    [TestMethod]
    public void HiddenIds_WhenSomeSwitchedOff_ReturnsOnlyThose()
    {
        GraphVisibility.HiddenIds(Providers()).Should().Equal("codex");
    }

    [TestMethod]
    public void HiddenIds_WhenNullOrAllOn_ReturnsEmpty()
    {
        GraphVisibility.HiddenIds(null).Should().BeEmpty();
        GraphVisibility.HiddenIds(ProviderConfig.GetDefaults()).Should().BeEmpty();
    }

    [TestMethod]
    public void TrySet_WhenChanged_UpdatesTheProviderAndReturnsTrue()
    {
        var providers = Providers();
        GraphVisibility.TrySet(providers, "CLAUDE", visible: false).Should().BeTrue();
        providers[0].ShowGraphs.Should().BeFalse();
        GraphVisibility.TrySet(providers, "codex", visible: true).Should().BeTrue();
        GraphVisibility.HiddenIds(providers).Should().Equal("claude");
    }

    [TestMethod]
    public void TrySet_WhenUnchangedOrUnknown_ReturnsFalse()
    {
        var providers = Providers();
        GraphVisibility.TrySet(providers, "claude", visible: true).Should().BeFalse();
        GraphVisibility.TrySet(providers, "unknown", visible: false).Should().BeFalse();
        GraphVisibility.TrySet(null, "claude", visible: false).Should().BeFalse();
        GraphVisibility.HiddenIds(providers).Should().Equal("codex");
    }
}
