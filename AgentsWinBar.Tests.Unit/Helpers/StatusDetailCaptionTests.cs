using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class StatusDetailCaptionTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static ProviderStatus Incident(string? title = "Elevated errors on Claude.ai", string? body = "We are investigating.",
        DateTime? at = null, ProviderStatusIndicator indicator = ProviderStatusIndicator.Minor) => new()
    {
        Indicator = indicator,
        Description = "Partially degraded service",
        IncidentTitle = title,
        LatestUpdateText = body,
        LatestUpdateAt = at ?? Now.AddHours(-3)
    };

    // BannerTitle

    [TestMethod]
    public void BannerTitle_NullOrBlank_IsEmpty()
    {
        StatusDetailCaption.BannerTitle((string?)null, "desc").Should().BeEmpty();
        StatusDetailCaption.BannerTitle("   ", "desc").Should().BeEmpty();
    }

    [TestMethod]
    public void BannerTitle_Trimmed() =>
        StatusDetailCaption.BannerTitle("  Outage  ", "desc").Should().Be("Outage");

    [TestMethod]
    public void BannerTitle_SameAsDescription_IsEmpty() =>
        StatusDetailCaption.BannerTitle("partially degraded service", " Partially Degraded Service ").Should().BeEmpty();

    [TestMethod]
    public void BannerTitle_Status_HealthyOrNull_IsEmpty()
    {
        StatusDetailCaption.BannerTitle((ProviderStatus?)null).Should().BeEmpty();
        StatusDetailCaption.BannerTitle(Incident(indicator: ProviderStatusIndicator.None)).Should().BeEmpty();
    }

    [TestMethod]
    public void BannerTitle_Status_Incident_ReturnsTitle() =>
        StatusDetailCaption.BannerTitle(Incident()).Should().Be("Elevated errors on Claude.ai");

    [TestMethod]
    public void BannerTitle_Status_NoTitle_IsEmpty() =>
        StatusDetailCaption.BannerTitle(Incident(title: null)).Should().BeEmpty();

    [TestMethod]
    public void BannerTitle_Provider_UsesProviderFieldsThenStatus()
    {
        StatusDetailCaption.BannerTitle((DashboardProvider?)null).Should().BeEmpty();
        StatusDetailCaption.BannerTitle(new DashboardProvider { ServiceStatus = Incident(title: null), IncidentTitle = "From provider" })
            .Should().Be("From provider");
        StatusDetailCaption.BannerTitle(new DashboardProvider { ServiceStatus = Incident() })
            .Should().Be("Elevated errors on Claude.ai");
        StatusDetailCaption.BannerTitle(new DashboardProvider { ServiceStatus = Incident(title: null) }).Should().BeEmpty();
    }

    // Tooltip

    [TestMethod]
    public void Tooltip_Full_ListsDescriptionTitleBodyAndAge() =>
        StatusDetailCaption.Tooltip(Incident(), Now)
            .Should().Be("Partially degraded service\nElevated errors on Claude.ai\nWe are investigating.\nUpdate 3h ago");

    [TestMethod]
    public void Tooltip_NoTitleNoBodyNoTime_IsJustTheDescription()
    {
        var status = new ProviderStatus { Indicator = ProviderStatusIndicator.Major, Description = "Major outage" };
        StatusDetailCaption.Tooltip(status, Now).Should().Be("Major outage");
    }

    [TestMethod]
    public void Tooltip_Healthy_IgnoresStaleIncidentText() =>
        StatusDetailCaption.Tooltip(Incident(indicator: ProviderStatusIndicator.None), Now)
            .Should().Be("Partially degraded service");

    [TestMethod]
    public void Tooltip_NullStatus_IsNoStatusAvailable() =>
        StatusDetailCaption.Tooltip((ProviderStatus?)null, Now).Should().Be("No status available");

    [TestMethod]
    public void Tooltip_LongOpenIncident_ShowsDaysOld() =>
        StatusDetailCaption.Tooltip(Incident(at: Now.AddDays(-4)), Now).Should().EndWith("Update 4d ago");

    [TestMethod]
    public void Tooltip_TitleSameAsDescription_NotRepeated() =>
        StatusDetailCaption.Tooltip("Outage", "outage", "Fixing it", Now.AddMinutes(5), Now)
            .Should().Be("Outage\nFixing it\nUpdate just now");

    [TestMethod]
    public void Tooltip_Provider_UsesProviderIncidentFields()
    {
        var provider = new DashboardProvider
        {
            ServiceStatus = Incident(title: null, body: null),
            IncidentTitle = "Provider title",
            IncidentUpdateText = "Provider body",
            IncidentUpdateAtUtc = Now.AddMinutes(-20)
        };
        StatusDetailCaption.Tooltip(provider, Now)
            .Should().Be("Partially degraded service\nProvider title\nProvider body\nUpdate 20m ago");
    }

    [TestMethod]
    public void Tooltip_Provider_HealthyOrNull_IsDescriptionOnly()
    {
        StatusDetailCaption.Tooltip((DashboardProvider?)null, Now).Should().Be("No status available");
        var healthy = new DashboardProvider
        {
            ServiceStatus = new ProviderStatus { Indicator = ProviderStatusIndicator.None },
            IncidentTitle = "stale", IncidentUpdateText = "stale", IncidentUpdateAtUtc = Now
        };
        StatusDetailCaption.Tooltip(healthy, Now).Should().Be("All systems operational");
    }

    // FetchedWhenStale

    [TestMethod]
    public void FetchedWhenStale_Fresh_IsEmpty() =>
        StatusDetailCaption.FetchedWhenStale("Sun 11 Oct, 3:34 AM", Now.AddMinutes(-9), false, Now.AddMinutes(-9), Now).Should().BeEmpty();

    [TestMethod]
    public void FetchedWhenStale_SameAgeSaid_Once() =>
        StatusDetailCaption.FetchedWhenStale("Sun 11 Oct, 3:34 AM", Now.AddMinutes(-9), true, Now.AddMinutes(-9), Now)
            .Should().Be("Fetched Sun 11 Oct, 3:34 AM · 9m ago · Stale, showing last good reading");

    [TestMethod]
    public void FetchedWhenStale_DifferentAges_BothShown() =>
        StatusDetailCaption.FetchedWhenStale("Sun 11 Oct, 3:34 AM", Now.AddMinutes(-9), true, Now.AddHours(-2), Now)
            .Should().Be("Fetched Sun 11 Oct, 3:34 AM · 9m ago · Stale, last good reading 2h ago");

    [TestMethod]
    public void FetchedWhenStale_UnknownLastSuccess_SaysUnknown() =>
        StatusDetailCaption.FetchedWhenStale("Sun 11 Oct, 3:34 AM", Now.AddMinutes(-9), true, null, Now)
            .Should().Be("Fetched Sun 11 Oct, 3:34 AM · 9m ago · Stale, last good reading time unknown");

    // LastKnownData

    [TestMethod]
    public void LastKnownData_NotStaleOrNothingShown_IsEmpty()
    {
        StatusDetailCaption.LastKnownData(false, true, Now.AddMinutes(-9), Now).Should().BeEmpty();
        StatusDetailCaption.LastKnownData(true, false, Now.AddMinutes(-9), Now).Should().BeEmpty();
    }

    [TestMethod]
    public void LastKnownData_StaleAndShown_SaysFromAge() =>
        StatusDetailCaption.LastKnownData(true, true, Now.AddMinutes(-9), Now).Should().Be("Showing last-known data from 9m ago");

    [TestMethod]
    public void LastKnownData_UnknownTime_OmitsAge() =>
        StatusDetailCaption.LastKnownData(true, true, null, Now).Should().Be("Showing last-known data");

    [TestMethod]
    public void LastKnownData_Provider_RequiresWindows()
    {
        StatusDetailCaption.LastKnownData((DashboardProvider?)null, Now).Should().BeEmpty();
        var window = new DashboardWindow();
        var stale = new DashboardProvider { UsageIsStale = true, LastSuccessfulUsageFetchUtc = Now.AddHours(-1), Windows = [window] };
        StatusDetailCaption.LastKnownData(stale, Now).Should().Be("Showing last-known data from 1h ago");
        StatusDetailCaption.LastKnownData(stale with { Windows = [] }, Now).Should().BeEmpty();
        StatusDetailCaption.LastKnownData(stale with { UsageIsStale = false }, Now).Should().BeEmpty();
    }
}
