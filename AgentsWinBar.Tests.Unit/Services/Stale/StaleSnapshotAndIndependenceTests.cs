using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Services.Stale;

[TestClass]
public class StaleSnapshotAndIndependenceTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Build_WhenUsageStaleAndCostFresh_UsageIsStaleWhileCostIsNot()
    {
        var usageTime = Now.AddMinutes(-30);
        var costTime = Now.AddMinutes(-1);
        var provider = new UsageData
        {
            Provider = "claude",
            FetchedAt = Now,
            LastSuccessfulFetch = usageTime,
            UsageIsStale = true,
            StatusIsStale = false,
            Session = new UsageWindow { ReportedPercent = 40 },
            Cost = new CostData
            {
                Provider = "claude",
                FetchedAt = costTime,
                LastSuccessfulFetch = costTime,
                IsStale = false,
                TodayCostUSD = 2.0m,
                FetchOutcome = FetchOutcome.Success
            }
        };

        var snapshot = DashboardSnapshotBuilder.Build(new()
        {
            Providers = [provider],
            Settings = new() { ShowCostTotals = true },
            NowUtc = Now
        });

        var p = snapshot.Providers.Single();
        p.IsStale.Should().BeTrue();
        p.UsageIsStale.Should().BeTrue();
        p.StatusIsStale.Should().BeFalse();
        p.LastSuccessfulUsageFetchUtc.Should().Be(usageTime);

        p.Cost.Should().NotBeNull();
        p.Cost!.IsStale.Should().BeFalse();
        p.Cost.LastSuccessfulFetchUtc.Should().Be(costTime);

        // StaleCaption for cost is empty because cost is fresh
        StaleCaption.Text(p.Cost.IsStale, p.Cost.LastSuccessfulFetchUtc, Now).Should().BeEmpty();
        // StaleCaption for usage shows 30m ago
        StaleCaption.Text(p.UsageIsStale, p.LastSuccessfulUsageFetchUtc, Now).Should().Be("Stale · last updated 30m ago");
    }

    [TestMethod]
    public void Build_WhenUsageFreshAndCostStale_UsageIsFreshWhileCostIsStale()
    {
        var usageTime = Now.AddMinutes(-2);
        var costTime = Now.AddHours(-4);
        var provider = new UsageData
        {
            Provider = "codex",
            FetchedAt = usageTime,
            LastSuccessfulFetch = usageTime,
            UsageIsStale = false,
            StatusIsStale = false,
            Session = new UsageWindow { ReportedPercent = 65 },
            Cost = new CostData
            {
                Provider = "codex",
                FetchedAt = costTime,
                LastSuccessfulFetch = costTime,
                IsStale = true,
                TodayCostUSD = 15.0m,
                FetchOutcome = FetchOutcome.Success
            }
        };

        var snapshot = DashboardSnapshotBuilder.Build(new()
        {
            Providers = [provider],
            Settings = new() { ShowCostTotals = true },
            NowUtc = Now
        });

        var p = snapshot.Providers.Single();
        // Provider is stale overall because cost is stale
        p.IsStale.Should().BeTrue();
        p.UsageIsStale.Should().BeFalse();
        p.LastSuccessfulUsageFetchUtc.Should().Be(usageTime);

        p.Cost.Should().NotBeNull();
        p.Cost!.IsStale.Should().BeTrue();
        p.Cost.LastSuccessfulFetchUtc.Should().Be(costTime);
        p.Cost.StaleAge.Should().Be(TimeSpan.FromHours(4));

        // Usage caption is empty because usage is fresh
        StaleCaption.Text(p.UsageIsStale, p.LastSuccessfulUsageFetchUtc, Now).Should().BeEmpty();
        // Cost caption shows 4h ago
        StaleCaption.Text(p.Cost.IsStale, p.Cost.LastSuccessfulFetchUtc, Now).Should().Be("Stale · last updated 4h ago");
    }

    [TestMethod]
    public void Build_WhenBothUsageAndCostStale_LastSuccessfulFetchUtcIsOldest()
    {
        var usageTime = Now.AddMinutes(-45); // 45m ago
        var costTime = Now.AddHours(-3);     // 3h ago (older)
        var provider = new UsageData
        {
            Provider = "gemini",
            FetchedAt = Now,
            LastSuccessfulFetch = usageTime,
            UsageIsStale = true,
            StatusIsStale = false,
            Session = new UsageWindow { ReportedPercent = 80 },
            Cost = new CostData
            {
                Provider = "gemini",
                FetchedAt = costTime,
                LastSuccessfulFetch = costTime,
                IsStale = true,
                TodayCostUSD = 8.5m,
                FetchOutcome = FetchOutcome.Success
            }
        };

        var snapshot = DashboardSnapshotBuilder.Build(new()
        {
            Providers = [provider],
            Settings = new() { ShowCostTotals = true },
            NowUtc = Now
        });

        var p = snapshot.Providers.Single();
        p.UsageIsStale.Should().BeTrue();
        p.Cost!.IsStale.Should().BeTrue();
        // LastSuccessfulFetchUtc should be the oldest among stale kinds (costTime = 3h ago)
        p.LastSuccessfulFetchUtc.Should().Be(costTime);
        p.StaleAge.Should().Be(TimeSpan.FromHours(3));
    }

    [TestMethod]
    public void Build_MultiProviderMix_CalculatesSummaryCountsAccurately()
    {
        var freshTime = Now.AddMinutes(-1);
        var staleUsageTime = Now.AddHours(-1);
        var staleCostTime = Now.AddHours(-2);

        var p1Fresh = new UsageData
        {
            Provider = "claude",
            FetchedAt = freshTime,
            LastSuccessfulFetch = freshTime,
            Session = new UsageWindow { ReportedPercent = 10 }
        };

        var p2StaleUsage = new UsageData
        {
            Provider = "codex",
            FetchedAt = Now,
            LastSuccessfulFetch = staleUsageTime,
            UsageIsStale = true,
            Session = new UsageWindow { ReportedPercent = 20 }
        };

        var p3StaleCost = new UsageData
        {
            Provider = "gemini",
            FetchedAt = freshTime,
            LastSuccessfulFetch = freshTime,
            Session = new UsageWindow { ReportedPercent = 30 },
            Cost = new CostData { Provider = "gemini", FetchedAt = staleCostTime, IsStale = true, TodayCostUSD = 1m, FetchOutcome = FetchOutcome.Success }
        };

        var p4Error = new UsageData
        {
            Provider = "other",
            FetchedAt = Now,
            Error = "Connection refused"
        };

        var snapshot = DashboardSnapshotBuilder.Build(new()
        {
            Providers = [p1Fresh, p2StaleUsage, p3StaleCost, p4Error],
            Settings = new() { ShowCostTotals = true },
            NowUtc = Now
        });

        snapshot.Summary.ProviderCount.Should().Be(4);
        snapshot.Summary.HealthyProviderCount.Should().Be(1); // p1 only
        snapshot.Summary.StaleProviderCount.Should().Be(2);   // p2 and p3
        snapshot.Summary.ErrorProviderCount.Should().Be(1);   // p4
        snapshot.Summary.CostAvailable.Should().BeTrue();
        snapshot.Summary.TotalCostToday.Should().Be(1m);
    }

    [TestMethod]
    public void Regression_FreshHealthyData_ProducesIdenticalOutputToPreStaleBaseline()
    {
        var fetchTime = Now.AddMinutes(-2);
        var freshData = new UsageData
        {
            Provider = "claude",
            FetchedAt = fetchTime,
            LastSuccessfulFetch = fetchTime,
            UsageIsStale = false,
            StatusIsStale = false,
            Session = new UsageWindow { ReportedPercent = 35, WindowMinutes = 300, ResetAt = Now.AddHours(3) },
            Cost = new CostData
            {
                Provider = "claude",
                FetchedAt = fetchTime,
                LastSuccessfulFetch = fetchTime,
                IsStale = false,
                TodayCostUSD = 3.5m,
                TodayTokens = 350,
                Last30DaysCostUSD = 35m,
                Last30DaysTokens = 3500,
                FetchOutcome = FetchOutcome.Success
            }
        };

        var snapshot = DashboardSnapshotBuilder.Build(new()
        {
            Providers = [freshData],
            Settings = new() { ShowCostTotals = true },
            NowUtc = Now
        });

        var p = snapshot.Providers.Single();
        p.IsStale.Should().BeFalse();
        p.UsageIsStale.Should().BeFalse();
        p.StatusIsStale.Should().BeFalse();
        p.Error.Should().BeNull();
        p.Windows.Should().ContainSingle();
        p.Cost.Should().NotBeNull();
        p.Cost!.IsStale.Should().BeFalse();
        p.Cost.TodayCost.Should().Be(3.5m);

        // StaleCaption text should be completely empty
        StaleCaption.Text(p.UsageIsStale, p.LastSuccessfulUsageFetchUtc, Now).Should().BeEmpty();
        StaleCaption.Text(p.Cost.IsStale, p.Cost.LastSuccessfulFetchUtc, Now).Should().BeEmpty();

        snapshot.Summary.HealthyProviderCount.Should().Be(1);
        snapshot.Summary.StaleProviderCount.Should().Be(0);
        snapshot.Summary.ErrorProviderCount.Should().Be(0);
    }
}
