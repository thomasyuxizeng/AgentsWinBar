using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class PlanNameFormatterTests
{
    [TestMethod]
    [DataRow("team", "Team")]
    [DataRow("pro", "Pro")]
    [DataRow("plus", "Plus")]
    [DataRow("enterprise", "Enterprise")]
    [DataRow("  team ", "Team")]
    public void Display_WhenPlanIsLowercase_ReturnsTitleCase(string plan, string expected) =>
        PlanNameFormatter.Display(plan).Should().Be(expected);

    [TestMethod]
    [DataRow("Claude Max 20x")]
    [DataRow("Max 20x")]
    [DataRow("Google AI Pro")]
    [DataRow("Claude Team")]
    [DataRow("ChatGPT Plus")]
    public void Display_WhenPlanIsAlreadyCapitalized_ReturnsItUnchanged(string plan) =>
        PlanNameFormatter.Display(plan).Should().Be(plan);

    [TestMethod]
    public void Display_WhenSomeWordsAreLowercase_CapitalizesOnlyThoseWords()
    {
        PlanNameFormatter.Display("claude max 20x").Should().Be("Claude Max 20x");
        PlanNameFormatter.Display("Google ai pro").Should().Be("Google Ai Pro");
    }

    [TestMethod]
    public void Display_WhenPlanIsAnIdentifier_SplitsItIntoWords()
    {
        PlanNameFormatter.Display("free_workspace").Should().Be("Free Workspace");
        PlanNameFormatter.Display("Free_Workspace").Should().Be("Free_Workspace");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Display_WhenPlanIsMissing_ReturnsNull(string? plan) =>
        PlanNameFormatter.Display(plan).Should().BeNull();
}
