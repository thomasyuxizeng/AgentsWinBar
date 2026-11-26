using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class DashboardGlanceTests
{
    [TestMethod]
    public void Attention_SignedOutWithErrorStaleAndHighUsage_ReportsSignOutFirstAndOnlyOnce()
    {
        var signedOut = Provider("claude", Window("Weekly", 99)) with
        {
            AuthRequired = true, AuthHint = "Sign in again in WSL.",
            Error = "Token missing", UsageIsStale = true,
            ServiceStatus = new() { Indicator = ProviderStatusIndicator.Major }
        };
        var items = DashboardGlance.Attention([Provider("codex") with { Error = "Network error" }, signedOut]);
        items.Should().HaveCount(2);
        items[0].Should().Be(new AttentionItem("Claude is signed out", "Sign in again in WSL.", AttentionSeverity.Critical, "claude"));
        items[1].Label.Should().Be("Fetch failed");
        items[1].Detail.Should().Be("Codex");
    }

    private static readonly DateTime Reset = new(2026, 10, 15, 21, 0, 0, DateTimeKind.Utc);

    private static DashboardWindow Window(string title, double used, DateTime? reset = null, PaceKind pace = PaceKind.OnTrack) =>
        new() { Key = title.ToLowerInvariant(), Title = title, UsedPercent = used, ResetsAtUtc = reset, Pace = new(pace) };

    private static DashboardProvider Provider(string id, params DashboardWindow[] windows) =>
        new() { Id = id, DisplayName = char.ToUpperInvariant(id[0]) + id[1..], Windows = windows };

    private static ApiCreditsInfo Api(double percent, bool over = false) =>
        new() { State = ApiCreditsState.Ok, PercentUsed = percent, IsOverBudget = over, MonthlyUsd = 200 };

    [TestMethod]
    public void Attention_WhenEverythingIsFine_ReturnsEmpty()
    {
        var providers = new[] { Provider("claude", Window("Session", 20)), Provider("codex", Window("Weekly", 74.9)) };

        DashboardGlance.Attention(providers).Should().BeEmpty();
    }

    [TestMethod]
    public void Attention_WhenNull_ReturnsEmpty() => DashboardGlance.Attention(null).Should().BeEmpty();

    [TestMethod]
    public void Attention_WhenWindowsNearAndAtLimit_GroupsByKindAcrossProviders()
    {
        var providers = new[]
        {
            Provider("claude", Window("Session", 10), Window("Weekly", 87)),
            Provider("codex", Window("Session", 95), Window("Weekly", 83))
        };

        var items = DashboardGlance.Attention(providers);

        items.Should().HaveCount(2);
        items[0].Should().Be(new AttentionItem("At limit", "Codex Session", AttentionSeverity.Critical, "codex"));
        items[1].Should().Be(new AttentionItem("Near limit", "Claude Weekly, Codex Weekly", AttentionSeverity.Caution, "claude"));
    }

    [TestMethod]
    public void Attention_WhenWindowWillRunOut_ListsItOnceUnderRunsOut()
    {
        var providers = new[] { Provider("codex", Window("Session", 80, pace: PaceKind.WillRunOut)) };

        var items = DashboardGlance.Attention(providers);

        items.Should().ContainSingle().Which.Should().Be(
            new AttentionItem("Runs out before reset", "Codex Session", AttentionSeverity.Caution, "codex"));
    }

    [TestMethod]
    public void Attention_WhenAtLimitAndRunningOut_ListsOnlyAtLimit()
    {
        var providers = new[] { Provider("codex", Window("Session", 92, pace: PaceKind.WillRunOut)) };

        DashboardGlance.Attention(providers).Should().ContainSingle().Which.Label.Should().Be("At limit");
    }

    [TestMethod]
    public void Attention_WhenIncidentAndFetchError_OrdersCriticalFirst()
    {
        var minor = Provider("codex") with
        {
            ServiceStatus = new ProviderStatus { Indicator = ProviderStatusIndicator.Minor, Description = "Partial System Degradation" }
        };
        var failed = Provider("gemini") with { Error = "Request timed out", UsageIsStale = true };

        var items = DashboardGlance.Attention([minor, failed]);

        items.Select(i => i.Label).Should().Equal("Fetch failed", "Codex service");
        items[1].Detail.Should().Be("Partial System Degradation");
        items[1].Severity.Should().Be(AttentionSeverity.Caution);
    }

    [TestMethod]
    public void Attention_WhenMajorIncident_IsCritical()
    {
        var major = Provider("claude") with { ServiceStatus = new ProviderStatus { Indicator = ProviderStatusIndicator.Major } };

        DashboardGlance.Attention([major]).Should().ContainSingle().Which.Severity.Should().Be(AttentionSeverity.Critical);
    }

    [TestMethod]
    public void Attention_WhenStatusOperationalOrUnknown_AddsNothing()
    {
        var ok = Provider("claude") with { ServiceStatus = new ProviderStatus { Indicator = ProviderStatusIndicator.None } };
        var unknown = Provider("codex") with { ServiceStatus = new ProviderStatus { Indicator = ProviderStatusIndicator.Unknown } };

        DashboardGlance.Attention([ok, unknown]).Should().BeEmpty();
    }

    [TestMethod]
    public void Attention_WhenProvidersStale_ListsThemTogetherButNotFailedOnes()
    {
        var providers = new[]
        {
            Provider("claude") with { OldReadingUtc = Reset },
            Provider("codex") with { StatusIsStale = true },
            Provider("gemini") with { Error = "boom", UsageIsStale = true }
        };

        var items = DashboardGlance.Attention(providers);

        items.Should().ContainSingle(i => i.Label == "Stale data").Which.Detail.Should().Be("Claude, Codex");
    }

    [TestMethod]
    public void Attention_WhenApiCreditsOverBudget_IsCritical()
    {
        var claude = Provider("claude") with { ApiCredits = Api(120, over: true) };

        DashboardGlance.Attention([claude]).Should().ContainSingle().Which.Should().Be(
            new AttentionItem("API credits", "over budget", AttentionSeverity.Critical, "claude"));
    }

    [TestMethod]
    public void Attention_WhenApiCreditsAboveWarning_IsCaution()
    {
        var claude = Provider("claude") with { ApiCredits = Api(80) };

        DashboardGlance.Attention([claude]).Should().ContainSingle().Which.Should().Be(
            new AttentionItem("API credits", "running low", AttentionSeverity.Caution, "claude"));
    }

    [TestMethod]
    public void Attention_WhenApiCreditsNotOk_AddsNothing()
    {
        var claude = Provider("claude") with { ApiCredits = new ApiCreditsInfo { State = ApiCreditsState.NoKey, PercentUsed = 99 } };

        DashboardGlance.Attention([claude]).Should().BeEmpty();
    }

    [TestMethod]
    public void IsStale_WhenOnlyOldFetch_ReturnsFalse() =>
        DashboardGlance.IsStale(Provider("claude") with { IsStale = true }).Should().BeFalse();

    [TestMethod]
    public void IsStale_WhenCostIsLastKnownGood_ReturnsTrue() =>
        DashboardGlance.IsStale(Provider("claude") with { Cost = new DashboardCost { IsStale = true } }).Should().BeTrue();

    [TestMethod]
    public void IsStale_WhenNull_ReturnsFalse() => DashboardGlance.IsStale(null).Should().BeFalse();

    [TestMethod]
    public void ResetGroups_WhenConsecutiveWindowsShareReset_GroupsThem()
    {
        var windows = new[]
        {
            Window("Session", 10, Reset.AddHours(-3)),
            Window("Weekly", 20, Reset),
            Window("Sonnet", 5, Reset.AddSeconds(30)),
            Window("Extra", 1)
        };

        DashboardGlance.ResetGroups(windows).Should().Equal(0, 1, 1, 2);
    }

    [TestMethod]
    public void ResetGroups_WhenSameResetIsNotAdjacent_KeepsThemApart()
    {
        var windows = new[] { Window("A", 1, Reset), Window("B", 1, Reset.AddDays(1)), Window("C", 1, Reset) };

        DashboardGlance.ResetGroups(windows).Should().Equal(0, 1, 2);
    }

    [TestMethod]
    public void ResetGroups_WhenResetsUnknown_NeverGroups()
    {
        var windows = new[] { Window("A", 1), Window("B", 1) };

        DashboardGlance.ResetGroups(windows).Should().Equal(0, 1);
    }

    [TestMethod]
    public void ResetGroups_WhenNull_ReturnsEmpty() => DashboardGlance.ResetGroups(null).Should().BeEmpty();

    [TestMethod]
    public void SameReset_WhenMoreThanAMinuteApart_ReturnsFalse() =>
        DashboardGlance.SameReset(Reset, Reset.AddMinutes(2)).Should().BeFalse();

    [TestMethod]
    [DataRow(1, "Resets")]
    [DataRow(2, "Both reset")]
    [DataRow(3, "All 3 reset")]
    public void ResetVerb_ForCount_ReturnsSentenceVerb(int count, string expected) =>
        DashboardGlance.ResetVerb(count).Should().Be(expected);

    [TestMethod]
    public void ShowCombinedSpend_WhenOneProviderHasCost_ReturnsFalse()
    {
        var providers = new[] { Provider("claude") with { Cost = new DashboardCost() }, Provider("codex") };

        DashboardGlance.ShowCombinedSpend(providers).Should().BeFalse();
    }

    [TestMethod]
    public void ShowCombinedSpend_WhenTwoProvidersHaveCost_ReturnsTrue()
    {
        var providers = new[] { Provider("claude") with { Cost = new DashboardCost() }, Provider("codex") with { Cost = new DashboardCost() } };

        DashboardGlance.ShowCombinedSpend(providers).Should().BeTrue();
    }

    [TestMethod]
    public void WindowLabel_CombinesProviderAndWindow() =>
        DashboardGlance.WindowLabel(Provider("claude"), Window("Weekly", 1)).Should().Be("Claude Weekly");
}
