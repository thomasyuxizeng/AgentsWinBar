using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class EarlyResetDisplayTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Detail_WithActiveReset_UsesCurrentPercentAsTarget()
    {
        EarlyResetDisplay.Detail(Now.AddHours(-2), 79, 3, Now).Should().Be("Reset early 2h ago · 79% to 3%");
    }

    [TestMethod]
    public void Detail_WithoutReset_IsEmpty()
    {
        EarlyResetDisplay.Detail(null, null, 3, Now).Should().BeEmpty();
        EarlyResetDisplay.Detail(Now.AddHours(-1), null, 3, Now).Should().BeEmpty();
        EarlyResetDisplay.Detail(null, 50, 3, Now).Should().BeEmpty();
    }

    [TestMethod]
    public void Detail_NonFiniteCurrentPercent_ShowsZero()
    {
        EarlyResetDisplay.Detail(Now.AddHours(-2), 79, double.NaN, Now).Should().Be("Reset early 2h ago · 79% to 0%");
    }

    [TestMethod]
    public void AppendToTip_AddsLineOnlyWhenDetailPresent()
    {
        EarlyResetDisplay.AppendToTip("Session: 3% used", "Reset early 2h ago · 79% to 3%")
            .Should().Be("Session: 3% used\nReset early 2h ago · 79% to 3%");
        EarlyResetDisplay.AppendToTip("Session: 3% used", "").Should().Be("Session: 3% used");
        EarlyResetDisplay.AppendToTip(null, "x").Should().Be("x");
    }

    [TestMethod]
    public void Chip_OnlyWhenEarlyResetActive()
    {
        EarlyResetDisplay.Chip(Now).Should().Be("RESET EARLY");
        EarlyResetDisplay.Chip(null).Should().BeEmpty();
    }
}
