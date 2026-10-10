using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class IncidentPersistenceAndExpiryTests
{
    private string _directory = null!;
    private string _cachePath = null!;
    private MockSettingsService _settings = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-incident-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _cachePath = Path.Combine(_directory, "cache.json");
        _settings = new MockSettingsService();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.GetFiles(_directory))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_directory, recursive: true);
        }
    }

    private CacheService CreateCache(string? path = null) =>
        new(NullLogger<CacheService>.Instance, _settings, path ?? _cachePath);

    [TestMethod]
    public async Task Persistence_IncidentDetail_SurvivesSaveAndLoadWithinRetention()
    {
        var cache1 = CreateCache();
        var updateTime = new DateTime(2026, 10, 10, 8, 30, 0, DateTimeKind.Utc);
        var fetchTime = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

        var data = new UsageData
        {
            Provider = "claude",
            FetchedAt = fetchTime,
            LastSuccessfulFetch = fetchTime,
            LastSuccessfulStatusFetch = fetchTime,
            UsageFetchOutcome = FetchOutcome.Success,
            StatusFetchOutcome = FetchOutcome.Success,
            Session = new UsageWindow { ReportedPercent = 40 },
            ServiceStatus = new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.Minor,
                Description = "Degraded performance",
                Url = "https://status.claude.com",
                IncidentTitle = "Elevated latency on Sonnet 3.5",
                LatestUpdateText = "Traffic has been rerouted.",
                LatestUpdateAt = updateTime
            }
        };

        cache1.Set("claude", data);
        await cache1.SaveAsync();

        // New cache instance simulating app restart 2 days later
        var cache2 = CreateCache();
        await cache2.LoadAsync();

        var restored = cache2.GetLastKnown("claude");
        restored.Should().NotBeNull();
        restored!.ServiceStatus.Should().NotBeNull();
        restored.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        restored.ServiceStatus.Description.Should().Be("Degraded performance");
        restored.ServiceStatus.Url.Should().Be("https://status.claude.com");
        restored.ServiceStatus.IncidentTitle.Should().Be("Elevated latency on Sonnet 3.5");
        restored.ServiceStatus.LatestUpdateText.Should().Be("Traffic has been rerouted.");
        restored.ServiceStatus.LatestUpdateAt.Should().Be(updateTime);
    }

    [TestMethod]
    public async Task Expiry_After7DaysWithoutSuccessfulStatusFetch_IncidentDetailIsPruned()
    {
        var cache1 = CreateCache();
        // 8 days ago
        var eightDaysAgo = DateTime.UtcNow.AddDays(-8);

        var data = new UsageData
        {
            Provider = "claude",
            FetchedAt = eightDaysAgo,
            LastSuccessfulFetch = eightDaysAgo,
            LastSuccessfulStatusFetch = eightDaysAgo,
            UsageFetchOutcome = FetchOutcome.Success,
            StatusFetchOutcome = FetchOutcome.Success,
            Session = new UsageWindow { ReportedPercent = 40 },
            ServiceStatus = new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.Major,
                IncidentTitle = "Ancient outage",
                LatestUpdateText = "Outage details",
                LatestUpdateAt = eightDaysAgo
            }
        };

        cache1.Set("claude", data);
        await cache1.SaveAsync();

        // Loading from disk after 8 days -> PruneExpired should drop the expired status
        var cache2 = CreateCache();
        await cache2.LoadAsync();

        var restored = cache2.GetLastKnown("claude");
        // After 8 days, both usage and status are beyond the 7-day retention window
        if (restored != null)
        {
            restored.ServiceStatus.Should().BeNull();
        }
    }

    [TestMethod]
    public void Expiry_OldOpenIncident_StillDisplayedWithAgeWhenStatusCheckedRecently()
    {
        // Real-world scenario: Claude incident open since Oct 7 (4 days ago),
        // but status check succeeded 5 minutes ago.
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var fourDaysAgo = now.AddDays(-4);
        var fiveMinutesAgo = now.AddMinutes(-5);

        var usage = new UsageData
        {
            Provider = "claude",
            FetchedAt = fiveMinutesAgo,
            LastSuccessfulFetch = fiveMinutesAgo,
            LastSuccessfulStatusFetch = fiveMinutesAgo,
            UsageFetchOutcome = FetchOutcome.Success,
            StatusFetchOutcome = FetchOutcome.Success,
            StatusIsStale = false,
            Session = new UsageWindow { ReportedPercent = 60 },
            ServiceStatus = new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.Minor,
                Description = "Monitoring recovery",
                IncidentTitle = "Elevated errors on platform.claude.com",
                LatestUpdateText = "Mitigated errors loading usage data.",
                LatestUpdateAt = fourDaysAgo
            }
        };

        var snapshot = DashboardSnapshotBuilder.Build(new DashboardInputs
        {
            Providers = [usage],
            NowUtc = now
        });

        var provider = snapshot.Providers.Single(p => p.Id == "claude");
        provider.ServiceStatus.Should().NotBeNull();
        provider.IncidentTitle.Should().Be("Elevated errors on platform.claude.com");
        provider.IncidentUpdateText.Should().Be("Mitigated errors loading usage data.");
        provider.IncidentUpdateAtUtc.Should().Be(fourDaysAgo);
        provider.StatusIsStale.Should().BeFalse();

        // Caption helper displays old incident age clearly
        IncidentCaption.AgeText(provider, now).Should().Be("Update 4d ago");
        IncidentCaption.Title(provider).Should().Be("Elevated errors on platform.claude.com");
    }

    [TestMethod]
    public void LastKnownGood_StatusFetchFailure_RetainsPreviousStatusWithIncidentTextAsStale()
    {
        var now = new DateTime(2026, 10, 11, 14, 0, 0, DateTimeKind.Utc);
        var incidentPostTime = now.AddHours(-6);
        var statusCheckedTime = now.AddHours(-1);

        // Usage fetch failed, status fetch failed, but previous status was retained from cache
        var staleUsage = new UsageData
        {
            Provider = "codex",
            FetchedAt = now,
            LastSuccessfulFetch = statusCheckedTime,
            LastSuccessfulStatusFetch = statusCheckedTime,
            UsageFetchOutcome = FetchOutcome.Failed,
            StatusFetchOutcome = FetchOutcome.Failed,
            UsageIsStale = true,
            StatusIsStale = true,
            Session = new UsageWindow { ReportedPercent = 75 },
            ServiceStatus = new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.Major,
                Description = "Partial system outage",
                IncidentTitle = "Delayed costs data in Compliance API",
                LatestUpdateText = "We have implemented a mitigation.",
                LatestUpdateAt = incidentPostTime
            }
        };

        var snapshot = DashboardSnapshotBuilder.Build(new DashboardInputs
        {
            Providers = [staleUsage],
            NowUtc = now
        });

        var provider = snapshot.Providers.Single(p => p.Id == "codex");
        provider.StatusIsStale.Should().BeTrue();
        provider.LastSuccessfulStatusFetchUtc.Should().Be(statusCheckedTime);
        provider.IncidentTitle.Should().Be("Delayed costs data in Compliance API");
        provider.IncidentUpdateText.Should().Be("We have implemented a mitigation.");
        provider.IncidentUpdateAtUtc.Should().Be(incidentPostTime);

        // Update age text reflects when provider posted the update
        IncidentCaption.AgeText(provider, now).Should().Be("Update 6h ago");
        // Status check staleness reflects when we last reached the status page
        StaleCaption.StatusText(provider.StatusIsStale, provider.LastSuccessfulStatusFetchUtc, now)
            .Should().Be("Stale · status last checked 1h ago");
    }
}
