using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class CodexBarServiceReviewRegressionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();
    private readonly MockWslService _wsl = new();
    private readonly MockCacheService _cache = new();
    private readonly MockSettingsService _settings = new();
    private readonly Mock<IProviderIncidentFetcher> _fetcher = new(MockBehavior.Strict);

    private CodexBarService Service(IGeminiStatusFallback? fallback = null) => new(_wsl, _cache, _settings,
        new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance, _clock,
        geminiStatusFallback: fallback, incidentFetcher: _fetcher.Object);

    private static ProviderStatus Incident(string title = "New incident") => new()
    {
        Indicator = ProviderStatusIndicator.Major, Description = "Feed reports outage",
        IncidentTitle = title, LatestUpdateText = "Investigating", Url = "https://status.claude.com/feed"
    };

    private static WslResult Reading(string provider, DateTime observation, bool cached = false,
        string? indicator = "minor", double percent = 42) => new()
    {
        Success = true,
        Output = JsonSerializer.Serialize(new
        {
            provider, cached, cacheUpdatedAt = observation,
            usage = new { updatedAt = observation, primary = new { usedPercent = percent } },
            status = indicator == null ? null : new { indicator, description = "CLI status", url = "https://status.claude.com/cli" }
        })
    };

    private void Respond(WslResult reading) => _wsl.CommandHandler = (_, _) => Task.FromResult(reading);

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    public async Task GetUsageAsync_PendingStatus_StartsWslImmediatelyAndUsesStatusCompletedDuringWsl(string provider)
    {
        var status = new TaskCompletionSource<ProviderStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fetcher.Setup(f => f.GetIncidentAsync(provider, It.IsAny<CancellationToken>())).Returns(status.Task);
        _wsl.CommandHandler = (_, _) => cli.Task;
        var pending = Service().GetUsageAsync(provider);
        try
        {
            // The old code awaited the status budget here before ever starting WSL.
            _wsl.ExecutedCommands.Should().HaveCount(1);
            pending.IsCompleted.Should().BeFalse();
        }
        finally
        {
            status.TrySetResult(Incident());
            cli.TrySetResult(Reading(provider, _clock.Now.UtcDateTime));
        }
        var result = (await pending.WaitAsync(TimeSpan.FromSeconds(2)))!;
        result.ServiceStatus!.IncidentTitle.Should().Be("New incident");
        result.StatusIsStale.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_StatusPendingDuringCallbacks_DoesNotBlockAndAwaitsStatusAfterBatch()
    {
        string[] providers = ["claude", "codex", "gemini"];
        _settings.SetProviders(providers.Select(p => new ProviderConfig { Id = p, IsEnabled = true }).ToList());
        var statuses = providers.ToDictionary(p => p, _ => new TaskCompletionSource<ProviderStatus?>(TaskCreationOptions.RunContinuationsAsynchronously));
        foreach (var p in providers)
            _fetcher.Setup(f => f.GetIncidentAsync(p, It.IsAny<CancellationToken>())).Returns(statuses[p].Task);
        var callbackDuration = TimeSpan.Zero;
        _wsl.BatchHandler = (commands, callback, _) =>
        {
            var results = providers.Select(p => Reading(p, _clock.Now.UtcDateTime, indicator: null)).ToArray();
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < results.Length; i++) callback!(i, results[i]);
            callbackDuration = timer.Elapsed;
            foreach (var status in statuses.Values) status.SetResult(Incident());
            return Task.FromResult<IReadOnlyList<WslResult>>(results);
        };
        var results = await Service().GetAllUsageAsync();
        callbackDuration.Should().BeLessThan(TimeSpan.FromMilliseconds(250));
        results.Should().HaveCount(3);
        foreach (var result in results)
        {
            result.ServiceStatus!.IncidentTitle.Should().Be("New incident");
            result.StatusIsStale.Should().BeFalse();
            result.LastSuccessfulStatusFetch.Should().Be(_clock.Now.UtcDateTime);
        }
    }

    [TestMethod]
    public async Task GetAllUsageAsync_StatusNeverCompletes_UsesBoundedAsyncWaitAndKeepsCliStatus()
    {
        _settings.SetProviders([new() { Id = "claude", IsEnabled = true }]);
        var status = new TaskCompletionSource<ProviderStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).Returns(status.Task);
        Respond(Reading("claude", _clock.Now.UtcDateTime));
        try
        {
            var results = await Service().GetAllUsageAsync().WaitAsync(TimeSpan.FromSeconds(2));
            results.Single().ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
            results.Single().StatusIsStale.Should().BeFalse(); // Fresh CLI status succeeds independently.
            status.Task.IsCompleted.Should().BeFalse();
        }
        finally { status.TrySetResult(null); }
    }

    [TestMethod]
    public async Task GetUsageAsync_HeldObservationTenMinutesAhead_ReplayKeepsReadingAndSuccessTime()
    {
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync((ProviderStatus?)null);
        var observation = _clock.Now.UtcDateTime.AddMinutes(10);
        var service = Service();
        Respond(Reading("claude", observation));
        var first = (await service.GetUsageAsync("claude"))!;
        first.HasUsage.Should().BeTrue();
        var successAt = first.LastSuccessfulFetch;
        _clock.Now += TimeSpan.FromMinutes(1);
        Respond(Reading("claude", observation, cached: true, percent: 99));
        var replay = (await service.GetUsageAsync("claude"))!;
        replay.UsageFetchOutcome.Should().Be(FetchOutcome.Success);
        replay.Error.Should().BeNull();
        replay.Session!.ReportedPercent.Should().Be(42);
        replay.DataUpdatedAt.Should().Be(observation);
        replay.LastSuccessfulFetch.Should().Be(successAt);
        replay.UsageIsStale.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetUsageAsync_UnheldFutureCachedObservation_RemainsRejected()
    {
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync((ProviderStatus?)null);
        Respond(Reading("claude", _clock.Now.UtcDateTime.AddMinutes(10), cached: true));
        var result = (await Service().GetUsageAsync("claude"))!;
        result.HasUsage.Should().BeFalse();
        result.UsageFetchOutcome.Should().Be(FetchOutcome.Unavailable);
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    [DataRow("gemini")]
    public async Task GetUsageAsync_ReplayWithFetchedIncident_StatusIsFreshAndUsageTimeUnchanged(string provider)
    {
        var observation = _clock.Now.UtcDateTime;
        var cliIndicator = provider == "gemini" ? null : "minor";
        _fetcher.Setup(f => f.GetIncidentAsync(provider, It.IsAny<CancellationToken>())).ReturnsAsync(Incident("Old incident"));
        Respond(Reading(provider, observation, indicator: cliIndicator));
        var service = Service();
        var first = (await service.GetUsageAsync(provider))!;
        _clock.Now += TimeSpan.FromMinutes(1);
        _fetcher.Setup(f => f.GetIncidentAsync(provider, It.IsAny<CancellationToken>())).ReturnsAsync(Incident());
        Respond(Reading(provider, observation, cached: true, indicator: cliIndicator));
        var replay = (await service.GetUsageAsync(provider))!;
        replay.StatusIsStale.Should().BeFalse();
        replay.StatusFetchOutcome.Should().Be(FetchOutcome.Success);
        replay.LastSuccessfulStatusFetch.Should().Be(_clock.Now.UtcDateTime);
        replay.ServiceStatus!.IncidentTitle.Should().Be("New incident");
        replay.ServiceStatus.Indicator.Should().Be(cliIndicator == null ? ProviderStatusIndicator.Major : ProviderStatusIndicator.Minor);
        replay.LastSuccessfulFetch.Should().Be(first.LastSuccessfulFetch);
        var saved = _cache.Get(provider)!;
        saved.LastSuccessfulStatusFetch.Should().Be(_clock.Now.UtcDateTime);
        saved.StatusIsStale.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetUsageAsync_ReplayWithFailedIncidentFetch_StatusStaysStaleAndKeepsOldTime()
    {
        var observation = _clock.Now.UtcDateTime;
        var service = Service();
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync(Incident("Old incident"));
        Respond(Reading("claude", observation));
        var first = (await service.GetUsageAsync("claude"))!;
        _clock.Now += TimeSpan.FromMinutes(1);
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Offline"));
        Respond(Reading("claude", observation, cached: true));
        var replay = (await service.GetUsageAsync("claude"))!;
        replay.StatusIsStale.Should().BeTrue();
        replay.LastSuccessfulStatusFetch.Should().Be(first.LastSuccessfulStatusFetch);
        replay.ServiceStatus!.IncidentTitle.Should().Be("Old incident");
        replay.UsageFetchOutcome.Should().Be(FetchOutcome.Success);
    }

    [TestMethod]
    public async Task GetUsageAsync_ReplayInsideShimTtl_CountsAsFetchedNow_AndKeepsObservationTime()
    {
        var observation = _clock.Now.UtcDateTime;
        var service = Service();
        Respond(Reading("claude", observation));
        var first = (await service.GetUsageAsync("claude"))!;
        _clock.Now += TimeSpan.FromMinutes(6);
        Respond(Reading("claude", observation, cached: true));
        var replay = (await service.GetUsageAsync("claude"))!;
        replay.UsageIsStale.Should().BeFalse();
        replay.Error.Should().BeNull();
        // The replay is a current answer: it must not age the card past the 5-minute "Stale data" rule.
        replay.FetchedAt.Should().Be(_clock.Now.UtcDateTime);
        // ...but the original observation time is preserved, so a replay can never renew its own expiry.
        replay.LastSuccessfulFetch.Should().Be(first.LastSuccessfulFetch);
    }

    [TestMethod]
    public async Task GetUsageAsync_ReplayBeyondShimTtl_KeepsOriginalFetchedAt_AndIsStale()
    {
        var observation = _clock.Now.UtcDateTime;
        var service = Service();
        Respond(Reading("claude", observation));
        var first = (await service.GetUsageAsync("claude"))!;
        _clock.Now += TimeSpan.FromMinutes(12);
        Respond(Reading("claude", observation, cached: true));
        var replay = (await service.GetUsageAsync("claude"))!;
        replay.UsageIsStale.Should().BeTrue();
        replay.FetchedAt.Should().Be(first.FetchedAt);
        replay.LastSuccessfulFetch.Should().Be(first.LastSuccessfulFetch);
    }

    [TestMethod]
    public async Task GetUsageAsync_ReplayCliHealthy_FetchedOpenIncidentWins()
    {
        var observation = _clock.Now.UtcDateTime;
        var service = Service();
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync(Incident("Live outage"));
        Respond(Reading("claude", observation));
        await service.GetUsageAsync("claude");
        _clock.Now += TimeSpan.FromMinutes(1);
        Respond(Reading("claude", observation, cached: true, indicator: "none"));
        var replay = (await service.GetUsageAsync("claude"))!;
        replay.ServiceStatus!.Indicator.Should().NotBe(ProviderStatusIndicator.None);
        replay.ServiceStatus.IncidentTitle.Should().Be("Live outage");
        replay.StatusIsStale.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("minor")]
    [DataRow("major")]
    public async Task GetUsageAsync_ReplayCliOutage_FetchedHealthyWins(string cliIndicator)
    {
        var observation = _clock.Now.UtcDateTime;
        var service = Service();
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync(Incident("Initial outage"));
        Respond(Reading("claude", observation, indicator: cliIndicator));
        await service.GetUsageAsync("claude");
        _clock.Now += TimeSpan.FromMinutes(1);
        var healthy = new ProviderStatus { Indicator = ProviderStatusIndicator.None, Description = "All systems operational" };
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync(healthy);
        Respond(Reading("claude", observation, cached: true, indicator: cliIndicator));
        var replay = (await service.GetUsageAsync("claude"))!;
        replay.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.None);
        replay.ServiceStatus.IncidentTitle.Should().BeNull();
        replay.StatusIsStale.Should().BeFalse();
        replay.LastSuccessfulStatusFetch.Should().Be(_clock.Now.UtcDateTime);
    }

    [TestMethod]
    [DataRow("minor", ProviderStatusIndicator.Minor)]
    [DataRow("major", ProviderStatusIndicator.Major)]
    public async Task GetUsageAsync_ReplayCliOutage_NoPrefetchAvailable_StaysCachedOutageAsStale(string cliIndicator, ProviderStatusIndicator expectedIndicator)
    {
        var observation = _clock.Now.UtcDateTime;
        var service = Service();
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync(Incident("Initial outage"));
        Respond(Reading("claude", observation, indicator: cliIndicator));
        var first = (await service.GetUsageAsync("claude"))!;
        _clock.Now += TimeSpan.FromMinutes(1);
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync((ProviderStatus?)null);
        Respond(Reading("claude", observation, cached: true, indicator: cliIndicator));
        var replay = (await service.GetUsageAsync("claude"))!;
        replay.ServiceStatus!.Indicator.Should().Be(expectedIndicator);
        replay.StatusIsStale.Should().BeTrue();
        replay.LastSuccessfulStatusFetch.Should().Be(first.LastSuccessfulStatusFetch);
    }

    [TestMethod]
    [DataRow("minor", ProviderStatusIndicator.Minor)]
    [DataRow("major", ProviderStatusIndicator.Major)]
    public async Task GetUsageAsync_FreshCliOutage_FetchedHealthy_CliOutageStillWins(string cliIndicator, ProviderStatusIndicator expectedIndicator)
    {
        var service = Service();
        var healthy = new ProviderStatus { Indicator = ProviderStatusIndicator.None, Description = "All systems operational" };
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync(healthy);
        Respond(Reading("claude", _clock.Now.UtcDateTime, cached: false, indicator: cliIndicator));
        var result = (await service.GetUsageAsync("claude"))!;
        result.ServiceStatus!.Indicator.Should().Be(expectedIndicator);
        result.StatusIsStale.Should().BeFalse();
        result.LastSuccessfulStatusFetch.Should().Be(_clock.Now.UtcDateTime);
    }

    [TestMethod]
    [DataRow(true)]
    public async Task GetUsageAsync_ReplayCliHealthy_ClearsIncidentWhenFetcherFails(bool fetcherFails)
    {
        var observation = _clock.Now.UtcDateTime;
        var service = Service();
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync(Incident("Old incident"));
        Respond(Reading("claude", observation));
        var first = (await service.GetUsageAsync("claude"))!;
        _clock.Now += TimeSpan.FromMinutes(1);
        if (fetcherFails)
            _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync((ProviderStatus?)null);
        Respond(Reading("claude", observation, cached: true, indicator: "none"));
        var replay = (await service.GetUsageAsync("claude"))!;
        replay.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.None);
        replay.ServiceStatus.IncidentTitle.Should().BeNull();
        replay.ServiceStatus.LatestUpdateText.Should().BeNull();
        replay.StatusIsStale.Should().Be(fetcherFails);
        replay.LastSuccessfulStatusFetch.Should().Be(fetcherFails ? first.LastSuccessfulStatusFetch : _clock.Now.UtcDateTime);
    }

    [TestMethod]
    [DataRow("claude", false)]
    [DataRow("claude", true)]
    [DataRow("codex", false)]
    [DataRow("codex", true)]
    public async Task GetUsageAsync_NoCliStatus_AdoptsFreshFetchedStatusForNonGemini(string provider, bool replay)
    {
        _fetcher.Setup(f => f.GetIncidentAsync(provider, It.IsAny<CancellationToken>())).ReturnsAsync(Incident());
        Respond(Reading(provider, _clock.Now.UtcDateTime.AddMinutes(-1), cached: replay, indicator: null));
        var result = (await Service().GetUsageAsync(provider))!;
        result.ServiceStatus!.IncidentTitle.Should().Be("New incident");
        result.StatusFetchOutcome.Should().Be(FetchOutcome.Success);
        result.StatusIsStale.Should().BeFalse();
        result.LastSuccessfulStatusFetch.Should().Be(_clock.Now.UtcDateTime);
    }

    [TestMethod]
    public async Task GetUsageAsync_WslThrows_StillPublishesFetchedStatus()
    {
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync(Incident());
        _wsl.CommandHandler = (_, _) => throw new IOException("WSL unavailable");
        var result = (await Service().GetUsageAsync("claude"))!;
        result.UsageFetchOutcome.Should().Be(FetchOutcome.Failed);
        result.ServiceStatus!.IncidentTitle.Should().Be("New incident");
        result.StatusIsStale.Should().BeFalse();
        result.LastSuccessfulStatusFetch.Should().Be(_clock.Now.UtcDateTime);
    }

    [TestMethod]
    public async Task GetUsageAsync_NoStatusFromEitherSource_DoesNotPublishFreshStatusSuccess()
    {
        _fetcher.Setup(f => f.GetIncidentAsync("claude", It.IsAny<CancellationToken>())).ReturnsAsync((ProviderStatus?)null);
        Respond(Reading("claude", _clock.Now.UtcDateTime, indicator: null));
        var result = (await Service().GetUsageAsync("claude"))!;
        result.ServiceStatus.Should().BeNull();
        result.LastSuccessfulStatusFetch.Should().BeNull();
        result.StatusFetchOutcome.Should().NotBe(FetchOutcome.Success);
    }

    [TestMethod]
    public async Task GetUsageAsync_IncidentFetcherRegistered_DoesNotUseLegacyGeminiFallback()
    {
        var fallback = new Mock<IGeminiStatusFallback>(MockBehavior.Strict);
        _fetcher.Setup(f => f.GetIncidentAsync("gemini", It.IsAny<CancellationToken>())).ReturnsAsync(Incident());
        Respond(Reading("gemini", _clock.Now.UtcDateTime, indicator: null));
        var result = (await Service(fallback.Object).GetUsageAsync("gemini"))!;
        result.ServiceStatus!.IncidentTitle.Should().Be("New incident");
        fallback.Verify(f => f.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
