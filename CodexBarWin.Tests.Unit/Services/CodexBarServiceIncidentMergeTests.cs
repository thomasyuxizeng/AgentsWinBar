using System.Diagnostics;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class CodexBarServiceIncidentMergeTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private MockWslService _wsl = null!;
    private MockCacheService _cache = null!;
    private MockSettingsService _settings = null!;
    private Mock<IProviderIncidentFetcher> _fetcher = null!;
    private TestClock _clock = null!;

    [TestInitialize]
    public void Setup()
    {
        _wsl = new();
        _cache = new();
        _settings = new();
        _fetcher = new(MockBehavior.Strict);
        _clock = new();
        _settings.SetProviders([
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "codex", IsEnabled = true },
            new() { Id = "gemini", IsEnabled = true }
        ]);
    }

    private CodexBarService CreateService() =>
        new(_wsl, _cache, _settings, new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance, _clock,
            incidentFetcher: _fetcher.Object);

    [TestMethod]
    public async Task MergeRules_CliStatusKept_FetcherAddsIncidentDetail()
    {
        var service = CreateService();
        var updateTime = _clock.Now.UtcDateTime.AddMinutes(-45);

        // CLI returns minor disruption with its own description and URL
        _wsl.SetCommandResult(
            "codexbar --provider claude --format json --source cli --status",
            new WslResult
            {
                Success = true,
                Output = """{"provider":"claude","usage":{"primary":{"usedPercent":30}},"status":{"indicator":"minor","description":"CLI reports degraded performance","url":"https://status.claude.com/cli"}}"""
            });

        // Incident fetcher returns details for claude
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.Major, // Feeds may say Major, but CLI is authoritative
                Description = "Feed description that should NOT overwrite CLI",
                Url = "https://status.claude.com/feed",
                IncidentTitle = "Elevated API Error Rates",
                LatestUpdateText = "Engineers have identified the root cause.",
                LatestUpdateAt = updateTime
            });

        var result = await service.GetUsageAsync("claude");

        result.Should().NotBeNull();
        result!.ServiceStatus.Should().NotBeNull();
        // CLI authoritative fields
        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        result.ServiceStatus.Description.Should().Be("CLI reports degraded performance");
        result.ServiceStatus.Url.Should().Be("https://status.claude.com/cli");
        // Enriched incident details from fetcher
        result.ServiceStatus.IncidentTitle.Should().Be("Elevated API Error Rates");
        result.ServiceStatus.LatestUpdateText.Should().Be("Engineers have identified the root cause.");
        result.ServiceStatus.LatestUpdateAt.Should().Be(updateTime);
        result.StatusFetchOutcome.Should().Be(FetchOutcome.Success);
    }

    [TestMethod]
    public async Task MergeRules_CliNoneHealthy_ClearsIncidentDetail()
    {
        var service = CreateService();

        // CLI says healthy (none)
        _wsl.SetCommandResult(
            "codexbar --provider claude --format json --source cli --status",
            new WslResult
            {
                Success = true,
                Output = """{"provider":"claude","usage":{"primary":{"usedPercent":30}},"status":{"indicator":"none","description":"All systems normal"}}"""
            });

        // Even if fetcher reports an open incident (or stale cache had an incident), CLI None is authoritative and clears detail
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.Minor,
                IncidentTitle = "Should be cleared",
                LatestUpdateText = "Should be cleared",
                LatestUpdateAt = _clock.Now.UtcDateTime.AddHours(-1)
            });

        var result = await service.GetUsageAsync("claude");

        result.Should().NotBeNull();
        result!.ServiceStatus.Should().NotBeNull();
        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.None);
        result.ServiceStatus.Description.Should().Be("All systems normal");
        result.ServiceStatus.IncidentTitle.Should().BeNull();
        result.ServiceStatus.LatestUpdateText.Should().BeNull();
        result.ServiceStatus.LatestUpdateAt.Should().BeNull();
    }

    [TestMethod]
    public async Task MergeRules_CliReportsIncident_FetcherHealthyOrNone_ClearsIncidentDetail()
    {
        var service = CreateService();

        // CLI reports minor disruption
        _wsl.SetCommandResult(
            "codexbar --provider codex --format json --source cli --status",
            new WslResult
            {
                Success = true,
                Output = """{"provider":"codex","usage":{"primary":{"usedPercent":50}},"status":{"indicator":"minor","description":"Degraded"}}"""
            });

        // Fetcher returns indicator None (no open incident)
        _fetcher.Setup(f => f.GetIncidentAsync("codex", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.None,
                Description = "All systems operational"
            });

        var result = await service.GetUsageAsync("codex");

        result.Should().NotBeNull();
        result!.ServiceStatus.Should().NotBeNull();
        // CLI indicator retained
        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        result.ServiceStatus.Description.Should().Be("Degraded");
        // Incident detail cleared
        result.ServiceStatus.IncidentTitle.Should().BeNull();
        result.ServiceStatus.LatestUpdateText.Should().BeNull();
        result.ServiceStatus.LatestUpdateAt.Should().BeNull();
    }

    [TestMethod]
    public async Task MergeRules_GeminiNoCliStatus_AdoptsFetcherStatusWhole()
    {
        var service = CreateService();
        var updateTime = _clock.Now.UtcDateTime.AddHours(-2);

        // CLI returns gemini usage with NO status object
        _wsl.SetCommandResult(
            "codexbar --provider gemini --format json --source cli --status",
            new WslResult
            {
                Success = true,
                Output = """{"provider":"gemini","usage":{"primary":{"usedPercent":10}}}"""
            });

        _fetcher.Setup(f => f.GetIncidentAsync("gemini", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.Major,
                Description = "Service disruption on Gemini models",
                Url = "https://www.google.com/appsstatus/dashboard/",
                IncidentTitle = "Elevated 500 errors on Gemini 3.1 Pro",
                LatestUpdateText = "Our engineers have mitigated the issue.",
                LatestUpdateAt = updateTime
            });

        var result = await service.GetUsageAsync("gemini");

        result.Should().NotBeNull();
        result!.ServiceStatus.Should().NotBeNull();
        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Major);
        result.ServiceStatus.Description.Should().Be("Service disruption on Gemini models");
        result.ServiceStatus.Url.Should().Be("https://www.google.com/appsstatus/dashboard/");
        result.ServiceStatus.IncidentTitle.Should().Be("Elevated 500 errors on Gemini 3.1 Pro");
        result.ServiceStatus.LatestUpdateText.Should().Be("Our engineers have mitigated the issue.");
        result.ServiceStatus.LatestUpdateAt.Should().Be(updateTime);
        result.StatusFetchOutcome.Should().Be(FetchOutcome.Success);
    }

    [TestMethod]
    public async Task FetcherFailure_DoesNotFailCliStatus_AndRetainsPreviousDetailAsStale()
    {
        var service = CreateService();
        var initialIncidentTime = _clock.Now.UtcDateTime.AddHours(-3);

        // Prepopulate cache with prior status including incident detail
        var cachedStatus = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Minor,
            Description = "Degraded",
            IncidentTitle = "Database latency",
            LatestUpdateText = "Restarting database replicas",
            LatestUpdateAt = initialIncidentTime
        };
        _cache.Set("claude", new UsageData
        {
            Provider = "claude",
            FetchedAt = _clock.Now.UtcDateTime.AddHours(-1),
            LastSuccessfulFetch = _clock.Now.UtcDateTime.AddHours(-1),
            LastSuccessfulStatusFetch = _clock.Now.UtcDateTime.AddHours(-1),
            ServiceStatus = cachedStatus,
            UsageFetchOutcome = FetchOutcome.Success,
            StatusFetchOutcome = FetchOutcome.Success,
            Session = new UsageWindow { ReportedPercent = 25 }
        });

        // CLI reports minor status successfully
        _wsl.SetCommandResult(
            "codexbar --provider claude --format json --source cli --status",
            new WslResult
            {
                Success = true,
                Output = """{"provider":"claude","usage":{"primary":{"usedPercent":28}},"status":{"indicator":"minor","description":"Degraded"}}"""
            });

        // Fetcher fails (returns null)
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderStatus?)null);

        var result = await service.GetUsageAsync("claude");

        result.Should().NotBeNull();
        result!.ServiceStatus.Should().NotBeNull();
        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        result.ServiceStatus.Description.Should().Be("Degraded");
        // Previous incident detail is retained as stale
        result.ServiceStatus.IncidentTitle.Should().Be("Database latency");
        result.ServiceStatus.LatestUpdateText.Should().Be("Restarting database replicas");
        result.ServiceStatus.LatestUpdateAt.Should().Be(initialIncidentTime);
        // StatusFetchOutcome remains Success because CLI succeeded
        result.StatusFetchOutcome.Should().Be(FetchOutcome.Success);
    }

    [TestMethod]
    public async Task FetcherTimeout_DoesNotDelayRefreshBeyondBound_AndKeepsCliStatus()
    {
        var service = CreateService();

        _wsl.SetCommandResult(
            "codexbar --provider claude --format json --source cli --status",
            new WslResult
            {
                Success = true,
                Output = """{"provider":"claude","usage":{"primary":{"usedPercent":45}},"status":{"indicator":"minor","description":"Degraded"}}"""
            });

        // Fetcher simulates a hang longer than StatusDeadline (4 s from the start of the prefetch)
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await Task.Delay(CodexBarService.StatusDeadline + TimeSpan.FromSeconds(3), ct);
                return new ProviderStatus { Indicator = ProviderStatusIndicator.Minor };
            });

        var sw = Stopwatch.StartNew();
        var result = await service.GetUsageAsync("claude");
        sw.Stop();

        // Refresh must not be delayed significantly beyond the status deadline
        sw.Elapsed.Should().BeLessThan(CodexBarService.StatusDeadline + TimeSpan.FromSeconds(1));

        result.Should().NotBeNull();
        result!.ServiceStatus.Should().NotBeNull();
        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        result.StatusFetchOutcome.Should().Be(FetchOutcome.Success);
    }

    [TestMethod]
    public async Task GeminiFetcherFailure_KeepsPreviousDetailAsStale()
    {
        var service = CreateService();
        var incidentTime = _clock.Now.UtcDateTime.AddHours(-2);

        // Prepopulate cache with last-known-good Gemini status
        var lastGood = new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.Major,
            Description = "Google outage",
            IncidentTitle = "Gemini unavailable",
            LatestUpdateText = "Mitigating",
            LatestUpdateAt = incidentTime
        };
        _cache.Set("gemini", new UsageData
        {
            Provider = "gemini",
            FetchedAt = _clock.Now.UtcDateTime.AddHours(-1),
            LastSuccessfulFetch = _clock.Now.UtcDateTime.AddHours(-1),
            LastSuccessfulStatusFetch = _clock.Now.UtcDateTime.AddHours(-1),
            ServiceStatus = lastGood,
            UsageFetchOutcome = FetchOutcome.Success,
            StatusFetchOutcome = FetchOutcome.Success,
            Session = new UsageWindow { ReportedPercent = 20 }
        });

        // CLI returns usage with NO status
        _wsl.SetCommandResult(
            "codexbar --provider gemini --format json --source cli --status",
            new WslResult
            {
                Success = true,
                Output = """{"provider":"gemini","usage":{"primary":{"usedPercent":25}}}"""
            });

        // Fetcher fails
        _fetcher.Setup(f => f.GetIncidentAsync("gemini", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderStatus?)null);

        var result = await service.GetUsageAsync("gemini");

        result.Should().NotBeNull();
        result!.ServiceStatus.Should().NotBeNull();
        result.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Major);
        result.ServiceStatus.IncidentTitle.Should().Be("Gemini unavailable");
        result.StatusIsStale.Should().BeTrue();
    }

    [TestMethod]
    public async Task Concurrency_TwoSimultaneousRefreshes_CompleteSafely()
    {
        var service = CreateService();

        _wsl.SetCommandResult(
            "codexbar --provider claude --format json --source cli --status",
            new WslResult
            {
                Success = true,
                Output = """{"provider":"claude","usage":{"primary":{"usedPercent":50}},"status":{"indicator":"minor","description":"Degraded"}}"""
            });

        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.Minor,
                IncidentTitle = "Claude Incident",
                LatestUpdateText = "Under review",
                LatestUpdateAt = _clock.Now.UtcDateTime
            });

        var task1 = service.GetUsageAsync("claude");
        var task2 = service.GetUsageAsync("claude");

        var results = await Task.WhenAll(task1, task2);

        results[0].Should().NotBeNull();
        results[1].Should().NotBeNull();
        results[0]!.ServiceStatus!.IncidentTitle.Should().Be("Claude Incident");
        results[1]!.ServiceStatus!.IncidentTitle.Should().Be("Claude Incident");
    }

    [TestMethod]
    public async Task Cancellation_PropagatesPromptly()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await service.GetUsageAsync("claude", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class DelegatingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return send(request, ct);
        }
    }

    [TestMethod]
    public async Task BudgetExpires_ResultArrivesLater_NextCallIsCacheHitWithNoNewRequest()
    {
        var fetchCompletion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegatingHandler((req, ct) => fetchCompletion.Task);
        using var incidentFetcher = new ProviderIncidentFetcher(handler, _clock);
        var service = new CodexBarService(_wsl, _cache, _settings, new MockSampleDataLoader(),
            NullLogger<CodexBarService>.Instance, _clock, incidentFetcher: incidentFetcher);

        _wsl.SetCommandResult(
            "codexbar --provider claude --format json --source cli --status",
            new WslResult
            {
                Success = true,
                Output = """{"provider":"claude","usage":{"primary":{"usedPercent":45}},"status":{"indicator":"minor","description":"Degraded"}}"""
            });

        // Call 1: Status deadline (4 s from the prefetch start) expires while HTTP request is in-flight
        var sw = Stopwatch.StartNew();
        var result1 = await service.GetUsageAsync("claude");
        sw.Stop();

        // Must not be delayed significantly beyond the status deadline
        sw.Elapsed.Should().BeLessThan(CodexBarService.StatusDeadline + TimeSpan.FromSeconds(1));
        result1.Should().NotBeNull();
        result1!.ServiceStatus!.IncidentTitle.Should().BeNull(); // Not yet available
        handler.Requests.Should().Be(1);

        // Now HTTP response arrives in background
        fetchCompletion.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "page": { "url": "https://status.claude.com" },
                  "status": { "indicator": "minor", "description": "Degraded" },
                  "incidents": [
                    {
                      "name": "Incident Title",
                      "status": "monitoring",
                      "resolved_at": null,
                      "incident_updates": [
                        { "body": "Update body text", "display_at": "2026-10-11T10:00:00Z" }
                      ]
                    }
                  ]
                }
                """)
        });

        // Small yield to let background task continue to completion and write to cache
        await Task.Yield();
        await Task.Delay(50);

        // Call 2: Next refresh should be a cache hit with NO new HTTP request
        var result2 = await service.GetUsageAsync("claude");
        result2.Should().NotBeNull();
        result2!.ServiceStatus!.IncidentTitle.Should().Be("Incident Title");
        handler.Requests.Should().Be(1); // No new HTTP request was made!
    }

    [TestMethod]
    public async Task CancellationOfRefreshCt_CancelsTheFetch()
    {
        var cancelledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegatingHandler(async (req, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelledTcs.TrySetResult(true);
                throw;
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        using var incidentFetcher = new ProviderIncidentFetcher(handler, _clock);
        var service = new CodexBarService(_wsl, _cache, _settings, new MockSampleDataLoader(),
            NullLogger<CodexBarService>.Instance, _clock, incidentFetcher: incidentFetcher);

        using var cts = new CancellationTokenSource();
        _wsl.CommandHandler = async (cmd, ct) =>
        {
            cts.Cancel(); // Cancel refresh token while in flight
            await Task.Yield();
            throw new OperationCanceledException(ct);
        };

        var act = async () => await service.GetUsageAsync("claude", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Verify the incident fetch received cancellation
        var wasCancelled = await Task.WhenAny(cancelledTcs.Task, Task.Delay(1000));
        wasCancelled.Should().Be(cancelledTcs.Task);
    }
}
