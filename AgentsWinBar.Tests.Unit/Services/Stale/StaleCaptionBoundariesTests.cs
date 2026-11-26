using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Services.Stale;

[TestClass]
public class StaleCaptionBoundariesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(0, "Stale · last updated just now")]
    [DataRow(1, "Stale · last updated just now")]
    [DataRow(58, "Stale · last updated just now")]
    [DataRow(59, "Stale · last updated just now")]
    [DataRow(60, "Stale · last updated 1m ago")]
    [DataRow(61, "Stale · last updated 1m ago")]
    [DataRow(59 * 60, "Stale · last updated 59m ago")]
    [DataRow(59 * 60 + 59, "Stale · last updated 59m ago")]
    [DataRow(60 * 60, "Stale · last updated 1h ago")]
    [DataRow(60 * 60 + 1, "Stale · last updated 1h ago")]
    [DataRow(23 * 3600, "Stale · last updated 23h ago")]
    [DataRow(23 * 3600 + 59 * 60 + 59, "Stale · last updated 23h ago")]
    [DataRow(24 * 3600, "Stale · last updated 1d ago")]
    [DataRow(24 * 3600 + 1, "Stale · last updated 1d ago")]
    [DataRow(365 * 86400, "Stale · last updated 365d ago")]
    public void StaleCaption_ExactTimeBoundaries_MapsToExpectedBuckets(int secondsAgo, string expectedCaption)
    {
        var timestamp = Now.AddSeconds(-secondsAgo);
        StaleCaption.Text(true, timestamp, Now).Should().Be(expectedCaption);
        StaleCaption.Age(timestamp, Now).Should().Be(expectedCaption["Stale · last updated ".Length..]);
    }

    [TestMethod]
    public void StaleCaption_NullTimestampWhenStale_ReturnsUnknown()
    {
        StaleCaption.Text(true, null, Now).Should().Be("Stale · last update unknown");
    }

    [TestMethod]
    public void StaleCaption_NullTimestampWhenFresh_ReturnsEmpty()
    {
        StaleCaption.Text(false, null, Now).Should().BeEmpty();
    }

    [TestMethod]
    public void StaleCaption_FreshWithAnyTimestamp_AlwaysReturnsEmpty()
    {
        StaleCaption.Text(false, Now.AddDays(-10), Now).Should().BeEmpty();
        StaleCaption.Text(false, Now.AddMinutes(-5), Now).Should().BeEmpty();
        StaleCaption.Text(false, Now, Now).Should().BeEmpty();
    }

    [TestMethod]
    public void Precedence_DashboardFormatLogic_RefreshingTakesPrecedenceOverStaleUsage()
    {
        // Replicating the precedence in DashboardFormat.FreshnessLine(provider):
        // provider == null ? string.Empty : provider.IsLoading ? "Refreshing…" : provider.UsageIsStale ? StaleCaption.Text(...) : ...
        string EvaluateFreshness(DashboardProvider? p, DateTime nowUtc) =>
            p == null ? string.Empty
            : p.IsLoading ? "Refreshing…"
            : p.UsageIsStale ? StaleCaption.Text(true, p.LastSuccessfulUsageFetchUtc, nowUtc)
            : "Fetched";

        var staleProviderLoading = new DashboardProvider
        {
            Id = "claude",
            IsLoading = true,
            UsageIsStale = true,
            LastSuccessfulUsageFetchUtc = Now.AddMinutes(-15)
        };

        var staleProviderIdle = staleProviderLoading with { IsLoading = false };

        // While refreshing, "Refreshing…" takes precedence over stale caption
        EvaluateFreshness(staleProviderLoading, Now).Should().Be("Refreshing…");
        // Once not refreshing, stale caption displays
        EvaluateFreshness(staleProviderIdle, Now).Should().Be("Stale · last updated 15m ago");
    }

    [TestMethod]
    public void Precedence_DashboardFormatLogic_CostStaleDeterminedDirectlyByCostIsStale()
    {
        // Replicating DashboardFormat.CostStale(cost):
        // cost == null ? string.Empty : StaleCaption.Text(cost.IsStale, cost.LastSuccessfulFetchUtc, DateTime.UtcNow);
        string EvaluateCostStale(DashboardCost? c, DateTime nowUtc) =>
            c == null ? string.Empty
            : StaleCaption.Text(c.IsStale, c.LastSuccessfulFetchUtc, nowUtc);

        var staleCost = new DashboardCost
        {
            IsStale = true,
            LastSuccessfulFetchUtc = Now.AddHours(-2)
        };

        var freshCost = staleCost with { IsStale = false };

        EvaluateCostStale(null, Now).Should().BeEmpty();
        EvaluateCostStale(freshCost, Now).Should().BeEmpty();
        EvaluateCostStale(staleCost, Now).Should().Be("Stale · last updated 2h ago");
    }
}
