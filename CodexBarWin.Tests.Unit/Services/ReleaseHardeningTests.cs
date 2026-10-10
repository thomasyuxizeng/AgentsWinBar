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
public class ReleaseHardeningTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow.AddMinutes(-1);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private string _directory = null!;
    private readonly Clock _clock = new();
    private readonly MockSettingsService _settings = new();
    private readonly MockWslService _wsl = new();
    private readonly Mock<IGeminiStatusFallback> _fallback = new();
    private const string GoodUsage = """{"provider":"gemini","usage":{"primary":{"usedPercent":42}},"status":{"indicator":"major"}}""";

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settings.SetProviders([new() { Id = "gemini", IsEnabled = true }]);
        _fallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ProviderStatus?)null);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private CacheService Cache() => new(NullLogger<CacheService>.Instance, _settings, Path.Combine(_directory, "cache.json"));
    private CodexBarService Service(ICacheService? cache = null) => new(_wsl, cache ?? Cache(), _settings,
        new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance, _clock, geminiStatusFallback: _fallback.Object);
    private void Response(string json, bool success = true) => _wsl.CommandHandler = (_, _) =>
        Task.FromResult(new WslResult { Success = success, Output = json, Error = success ? "" : "Disconnected" });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Refresh_WhenFallbackIgnoresCancellation_UsageCompletesWithinShortBound(bool stream)
    {
        var pending = new TaskCompletionSource<ProviderStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        _fallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(ct => token = ct).Returns(pending.Task);
        Response(GoodUsage);
        var watch = Stopwatch.StartNew();
        var service = Service();
        UsageData data;
        try
        {
            if (stream)
            {
                var readings = new List<UsageData>();
                await foreach (var reading in service.GetAllUsageStreamAsync()) readings.Add(reading);
                data = readings.Single();
            }
            else data = (await service.GetAllUsageAsync()).Single();
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            data.Session!.Percent.Should().Be(42);
            token.IsCancellationRequested.Should().BeFalse();
            _fallback.Verify(f => f.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { pending.TrySetResult(null); }
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenCallerCancelsDuringStatus_StopsBeforeUsageCommand()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        var commands = 0;
        _wsl.CommandHandler = (_, _) => { commands++; return Task.FromResult(new WslResult()); };
        using var cancellation = new CancellationTokenSource();
        var task = Service().GetUsageAsync("gemini", cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        Func<Task> act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
        commands.Should().Be(0);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenUsageFailsAndFallbackSucceeds_RetainsUsageWithFreshStatus()
    {
        var cache = Cache(); var service = Service(cache); Response(GoodUsage);
        var good = (await service.GetAllUsageAsync()).Single();
        _clock.Now += TimeSpan.FromMinutes(10); Response("", false);
        var status = new ProviderStatus { Indicator = ProviderStatusIndicator.None };
        _fallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(status);
        var partial = (await service.GetAllUsageAsync()).Single();
        partial.Session!.Percent.Should().Be(42);
        partial.UsageIsStale.Should().BeTrue();
        partial.LastSuccessfulFetch.Should().Be(good.LastSuccessfulFetch);
        partial.ServiceStatus.Should().Be(status);
        partial.StatusIsStale.Should().BeFalse();
        partial.LastSuccessfulStatusFetch.Should().Be(_clock.Now.UtcDateTime);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenUsageSucceedsAndFallbackThrows_RetainsStatusWithOriginalAge()
    {
        var service = Service(); Response(GoodUsage);
        var good = (await service.GetAllUsageAsync()).Single();
        _clock.Now += TimeSpan.FromMinutes(10);
        Response("""{"provider":"gemini","usage":{"primary":{"usedPercent":12}}}""");
        _fallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("feed down"));
        var partial = (await service.GetAllUsageAsync()).Single();
        partial.Session!.Percent.Should().Be(12); partial.UsageIsStale.Should().BeFalse();
        partial.StatusIsStale.Should().BeTrue();
        partial.LastSuccessfulStatusFetch.Should().Be(good.LastSuccessfulStatusFetch);
        partial.HasError.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenOnlyFallbackSucceeds_StatusSurvivesRestartAndFailedFetch()
    {
        Response("", false);
        _fallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderStatus { Indicator = ProviderStatusIndicator.Minor });
        var first = (await Service().GetAllUsageAsync()).Single();
        first.HasUsage.Should().BeFalse(); first.StatusIsStale.Should().BeFalse();
        _fallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ProviderStatus?)null);
        var restored = Cache(); await restored.LoadAsync();
        _clock.Now += TimeSpan.FromMinutes(10);
        var stale = (await Service(restored).GetAllUsageAsync()).Single();
        stale.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        stale.StatusIsStale.Should().BeTrue();
        stale.LastSuccessfulStatusFetch.Should().Be(first.LastSuccessfulStatusFetch);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WhenGeminiDisabledThenEnabled_FetchesStatusOnlyWhenEnabled()
    {
        _settings.SetProviders([new() { Id = "gemini", IsEnabled = false }, new() { Id = "codex", IsEnabled = true }]);
        Response("""{"provider":"codex","usage":{"primary":{"usedPercent":12}}}""");
        var service = Service(); await service.GetAllUsageAsync();
        _fallback.Verify(f => f.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
        _settings.SetProviders([new() { Id = "gemini", IsEnabled = true }]); Response(GoodUsage);
        await service.GetAllUsageAsync();
        _fallback.Verify(f => f.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenCachedStatusExpiredAndUsageFresh_DiscardsStatusIndependently()
    {
        var cache = new Mock<ICacheService>();
        cache.Setup(c => c.GetLastKnown("gemini")).Returns(new UsageData { Provider = "gemini",
            Session = new() { ReportedPercent = 42 }, FetchedAt = _clock.Now.UtcDateTime,
            ServiceStatus = new() { Indicator = ProviderStatusIndicator.Critical },
            LastSuccessfulStatusFetch = _clock.Now.UtcDateTime.AddDays(-8) });
        Response("", false);
        var data = await Service(cache.Object).GetUsageAsync("gemini");
        data!.Session!.Percent.Should().Be(42);
        data.ServiceStatus.Should().BeNull(); data.StatusIsStale.Should().BeFalse();
        data.LastSuccessfulStatusFetch.Should().BeNull();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cache_WhenStatusExpired_PrunesOnReadLoadAndSave(bool legacy)
    {
        var now = DateTime.UtcNow;
        var old = now.AddDays(-8);
        var data = new UsageData { Provider = "gemini", FetchedAt = now, Session = new() { ReportedPercent = 42 },
            ServiceStatus = new() { Indicator = ProviderStatusIndicator.Critical, UpdatedAt = old },
            LastSuccessfulStatusFetch = legacy ? null : old };
        var cache = Cache(); cache.Set("gemini", data);
        cache.Get("gemini")!.ServiceStatus.Should().BeNull();
        cache.GetLastKnown("gemini")!.ServiceStatus.Should().BeNull();
        cache.GetAll()["gemini"].ServiceStatus.Should().BeNull();
        // Write raw data so load must perform retention itself, independently of save.
        await File.WriteAllTextAsync(Path.Combine(_directory, "cache.json"), JsonSerializer.Serialize(
            new CacheData { Timestamp = now, Items = new() { ["gemini"] = data } }));
        var loaded = Cache(); await loaded.LoadAsync();
        loaded.GetLastKnown("gemini")!.ServiceStatus.Should().BeNull();
        await cache.SaveAsync();
        var saved = Cache(); await saved.LoadAsync();
        saved.GetLastKnown("gemini")!.Session!.Percent.Should().Be(42);
        saved.GetLastKnown("gemini")!.ServiceStatus.Should().BeNull();
    }

    [TestMethod]
    public async Task Cache_WhenUsageExpiredButStatusFresh_RetainsOnlyStatusAcrossRestart()
    {
        var cache = Cache(); var now = DateTime.UtcNow;
        cache.Set("gemini", new() { Provider = "gemini", FetchedAt = now.AddDays(-8),
            Session = new() { ReportedPercent = 42 }, LastSuccessfulFetch = now.AddDays(-8),
            ServiceStatus = new() { Indicator = ProviderStatusIndicator.Minor }, LastSuccessfulStatusFetch = now });
        var retained = cache.GetLastKnown("gemini");
        retained!.HasUsage.Should().BeFalse(); retained.ServiceStatus.Should().NotBeNull();
        await cache.SaveAsync(); var loaded = Cache(); await loaded.LoadAsync();
        loaded.GetLastKnown("gemini")!.HasUsage.Should().BeFalse();
        loaded.GetLastKnown("gemini")!.ServiceStatus.Should().NotBeNull();
    }

    [TestMethod]
    [DataRow(-7, true)]
    [DataRow(-8, false)]
    [DataRow(1, false)]
    public void PruneExpired_StatusRetentionBoundary_UsesStatusFetchTimestamp(int days, bool kept)
    {
        var now = _clock.Now.UtcDateTime;
        var data = CacheRetention.PruneExpired(new() { Provider = "gemini", FetchedAt = now,
            ServiceStatus = new(), LastSuccessfulStatusFetch = now.AddDays(days) }, now);
        (data!.ServiceStatus != null).Should().Be(kept);
    }

    [TestMethod]
    public async Task GetUsageAsync_WhenNewerGenerationIsStatusOnlySuccess_DoesNotSupersedeConcurrentOlderRealUsageSuccess()
    {
        var cache = Cache();
        var service = Service(cache);

        var call1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCall1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callCount = 0;
        _wsl.CommandHandler = (cmd, ct) =>
        {
            var count = Interlocked.Increment(ref callCount);
            if (count == 1)
            {
                call1Started.SetResult();
                return releaseCall1.Task.ContinueWith(_ => new WslResult
                {
                    Success = true,
                    Output = """{"provider":"gemini","usage":{"primary":{"usedPercent":75}}}"""
                }, TaskScheduler.Default);
            }
            else
            {
                return Task.FromResult(new WslResult
                {
                    Success = false,
                    Error = "CLI failed"
                });
            }
        };

        var status2 = new ProviderStatus { Indicator = ProviderStatusIndicator.Minor, Description = "Partial disruption" };
        _fallback.Setup(f => f.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(status2);

        // Start call 1 (older generation)
        var task1 = service.GetUsageAsync("gemini");
        await call1Started.Task;

        // Start and finish call 2 (newer generation) while call 1 is in flight
        var result2 = await service.GetUsageAsync("gemini");

        result2.Should().NotBeNull();
        result2!.HasUsage.Should().BeFalse();
        result2.ServiceStatus.Should().NotBeNull();
        result2.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);

        // Release call 1
        releaseCall1.SetResult();
        var result1 = await task1;

        // Assert: Call 1's real usage success must not be discarded
        result1.Should().NotBeNull();
        result1!.HasUsage.Should().BeTrue();
        result1.UsageFetchOutcome.Should().Be(FetchOutcome.Success);
        result1.Session!.Percent.Should().Be(75);
        result1.UsageIsStale.Should().BeFalse();

        // Cache must have call 1's usage and call 2's status
        var cached = cache.GetLastKnown("gemini");
        cached.Should().NotBeNull();
        cached!.HasUsage.Should().BeTrue();
        cached.Session!.Percent.Should().Be(75);
        cached.ServiceStatus.Should().NotBeNull();
        cached.ServiceStatus!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
    }
}
