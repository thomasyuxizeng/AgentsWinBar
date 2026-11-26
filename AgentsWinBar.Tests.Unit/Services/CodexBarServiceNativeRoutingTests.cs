using AwesomeAssertions;
using AwesomeAssertions.Equivalency;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Services.Native;
using AgentsWinBar.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

/// <summary>Usage source routing: "native" providers never reach IWslService; "wsl" providers keep the CLI batch.</summary>
[TestClass]
public class CodexBarServiceNativeRoutingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 6, 0, 0, TimeSpan.Zero);

    private MockCacheService _cache = null!;
    private MockSettingsService _settings = null!;
    private FixedClock _clock = null!;

    [TestInitialize]
    public void Setup()
    {
        _cache = new();
        _settings = new();
        _clock = new(Now);
        _settings.SetProviders([
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "codex", IsEnabled = true },
            new() { Id = "gemini", IsEnabled = true }
        ]);
        _settings.Settings.Normalize();
    }

    private CodexBarService Create(IWslService wsl, INativeUsageService? native, ICacheService? cache = null) =>
        new(wsl, cache ?? _cache, _settings, new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance, _clock,
            nativeUsage: native);

    private static NativeUsageResult Ok(string provider, double percent) => new()
    {
        Provider = provider,
        Source = "oauth",
        Status = NativeUsageStatus.Ok,
        Primary = new(percent, 300, Now.AddHours(2), "in 2h"),
        UpdatedAt = Now
    };

    private static FakeNativeUsage NativeOk() => new(p => Ok(p, p switch { "claude" => 10, "codex" => 20, _ => 30 }));

    private static string CliJson(string provider, double percent) =>
        $$$"""[{"provider":"{{{provider}}}","source":"oauth","usage":{"primary":{"usedPercent":{{{percent}}},"windowMinutes":300,"resetsAt":"2026-11-25T08:00:00Z","resetDescription":"in 2h"},"updatedAt":"2026-11-25T06:00:00Z"}}]""";

    [TestMethod]
    public async Task GetAllUsageAsync_AllProvidersNative_NeverTouchesWsl()
    {
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        var native = NativeOk();

        var results = await Create(wsl.Object, native).GetAllUsageAsync();

        results.Select(r => (r.Provider, r.Session!.Percent)).Should().Equal(("claude", 10), ("codex", 20), ("gemini", 30));
        native.Calls.Should().BeEquivalentTo(["claude", "codex", "gemini"]);
        wsl.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task GetUsageStreamAsync_Subset_ReadsOnlyThoseEnabledProviders()
    {
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        var native = NativeOk();
        _settings.SetProviders([
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "codex", IsEnabled = true },
            new() { Id = "gemini", IsEnabled = false }
        ]);
        var service = Create(wsl.Object, native);

        var results = new List<UsageData>();
        await foreach (var data in service.GetUsageStreamAsync(["CODEX", "gemini", "not-a-provider"])) results.Add(data);

        results.Select(r => r.Provider).Should().Equal("codex");
        native.Calls.Should().Equal("codex");
        wsl.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void GetRateLimitedUntil_NativeReaderInCooldown_ReturnsItsEndInUtc()
    {
        var until = Now.AddMinutes(7);
        var service = Create(new Mock<IWslService>(MockBehavior.Strict).Object,
            new LimitedNativeUsage(p => p == "claude" ? until : null));

        service.GetRateLimitedUntil("Claude").Should().Be(until.UtcDateTime);
        service.GetRateLimitedUntil("codex").Should().BeNull();
        service.GetRateLimitedUntil("nope").Should().BeNull();
    }

    [TestMethod]
    public void GetRateLimitedUntil_ProviderReadThroughWsl_IsNull()
    {
        _settings.Settings.UsageSources["claude"] = UsageSource.Wsl;
        var service = Create(new Mock<IWslService>().Object, new LimitedNativeUsage(_ => Now.AddMinutes(7)));

        service.GetRateLimitedUntil("claude").Should().BeNull();
        service.GetRateLimitedUntil("codex").Should().Be(Now.AddMinutes(7).UtcDateTime);
    }

    private sealed class LimitedNativeUsage(Func<string, DateTimeOffset?> until) : INativeUsageService, INativeRateLimitInfo
    {
        public Task<string> FetchUsageJsonAsync(string provider, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> FetchAllUsageJsonAsync(IReadOnlyList<string> providers,
            CancellationToken ct = default) => throw new NotSupportedException();
        public DateTimeOffset? RateLimitedUntil(string provider) => until(provider);
    }

    [TestMethod]
    public async Task GetAllUsageStreamAsync_AllProvidersNative_YieldsEveryProviderWithoutWsl()
    {
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        var service = Create(wsl.Object, NativeOk());

        var results = new List<UsageData>();
        await foreach (var data in service.GetAllUsageStreamAsync()) results.Add(data);

        results.Select(r => r.Provider).Should().BeEquivalentTo(["claude", "codex", "gemini"]);
        results.Should().OnlyContain(r => r.UsageFetchOutcome == FetchOutcome.Success);
        wsl.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task GetUsageAsync_NativeProvider_NeverTouchesWsl()
    {
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        var native = NativeOk();

        var result = await Create(wsl.Object, native).GetUsageAsync("Codex");

        result!.Provider.Should().Be("codex");
        result.Session!.Percent.Should().Be(20);
        native.Calls.Should().Equal("codex");
        wsl.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task GetUsageAsync_ProviderSetToWsl_RunsCliAndNotNative()
    {
        _settings.Settings.UsageSources["claude"] = UsageSource.Wsl;
        var wsl = new MockWslService();
        wsl.SetCommandResult("codexbar --provider claude --format json --source cli --status",
            new WslResult { Success = true, Output = CliJson("claude", 44) });
        var native = NativeOk();

        var result = await Create(wsl, native).GetUsageAsync("claude");

        result!.Session!.Percent.Should().Be(44);
        wsl.ExecutedCommands.Should().ContainSingle().Which.Should().Contain("--provider claude");
        native.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_OneProviderSetToWsl_OnlyThatProviderGoesToWslBatch()
    {
        _settings.Settings.UsageSources["codex"] = UsageSource.Wsl;
        var wsl = new MockWslService();
        wsl.SetCommandResult("codexbar usage --provider codex --format json --source cli --status",
            new WslResult { Success = true, Output = CliJson("codex", 55) });
        var native = NativeOk();

        var results = await Create(wsl, native).GetAllUsageAsync();

        results.Select(r => (r.Provider, r.Session!.Percent)).Should().Equal(("claude", 10), ("codex", 55), ("gemini", 30));
        wsl.ExecutedBatches.Should().ContainSingle().Which.Should().ContainSingle()
            .Which.Command.Should().Contain("--provider codex");
        native.Calls.Should().BeEquivalentTo(["claude", "gemini"]);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WslBatchFailsForWslProvider_RetriesOnlyThatProvider()
    {
        _settings.Settings.UsageSources["gemini"] = UsageSource.Wsl;
        var wsl = new MockWslService
        {
            BatchHandler = (_, _, _) => throw new InvalidOperationException("batch broken")
        };
        wsl.SetCommandResult("codexbar usage --provider gemini --format json --source cli --status --verbose",
            new WslResult { Success = true, Output = CliJson("gemini", 66) });
        var native = NativeOk();

        var results = await Create(wsl, native).GetAllUsageAsync();

        results.Select(r => (r.Provider, r.Session!.Percent)).Should().Equal(("claude", 10), ("codex", 20), ("gemini", 66));
        wsl.ExecutedCommands.Should().ContainSingle().Which.Should().Contain("--provider gemini");
        native.Calls.Should().BeEquivalentTo(["claude", "codex"]);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_WithoutNativeService_UsesWslForEveryProvider()
    {
        var wsl = new MockWslService();

        await Create(wsl, native: null).GetAllUsageAsync();

        wsl.ExecutedBatches.Should().ContainSingle().Which.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_DeveloperMode_UsesSamplesNotNativeOrWsl()
    {
        _settings.Settings.DeveloperModeEnabled = true;
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        var native = new Mock<INativeUsageService>(MockBehavior.Strict);

        var results = await Create(wsl.Object, native.Object).GetAllUsageAsync();

        results.Should().HaveCount(3);
        wsl.VerifyNoOtherCalls();
        native.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_NativeReading_IsCachedLikeCliReading()
    {
        var results = await Create(new Mock<IWslService>(MockBehavior.Strict).Object, NativeOk()).GetAllUsageAsync();

        _cache.Get("claude")!.Session!.Percent.Should().Be(results[0].Session!.Percent);
        _cache.SetCallCount.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_NativeReadExceedsUsageTimeout_ReportsTimeout()
    {
        _settings.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds = 1;
        var native = new FakeNativeUsage(p => Ok(p, 1)) { Hang = "codex" };

        var results = await Create(new Mock<IWslService>(MockBehavior.Strict).Object, native).GetAllUsageAsync();

        results[1].Provider.Should().Be("codex");
        results[1].Error.Should().Be("Request timed out");
        results[0].Session!.Percent.Should().Be(1);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_NativeServiceThrows_ReportsProviderError()
    {
        var native = new Mock<INativeUsageService>();
        native.Setup(n => n.FetchUsageJsonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bug"));

        var results = await Create(new Mock<IWslService>(MockBehavior.Strict).Object, native.Object).GetAllUsageAsync();

        results.Should().HaveCount(3).And.OnlyContain(r => r.HasError);
    }

    [TestMethod]
    public async Task GetAllUsageAsync_NativeJson_GivesSameUsageDataAsEquivalentCliJson()
    {
        var reset1 = new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
        var reset2 = new DateTimeOffset(2026, 11, 30, 3, 0, 0, TimeSpan.Zero);
        var reset3 = new DateTimeOffset(2026, 11, 28, 8, 0, 0, TimeSpan.Zero);
        var nativeResult = new NativeUsageResult
        {
            Provider = "claude",
            Source = "oauth",
            Status = NativeUsageStatus.Ok,
            LoginMethod = "Claude Max 20x",
            Primary = new(37.5, 300, reset1, "Nov 25 at 8:00AM"),
            Secondary = new(12, 10080, reset2, "Nov 30 at 3:00AM"),
            Tertiary = new(4, 10080, reset2, "Nov 30 at 3:00AM"),
            ExtraWindows = [new("claude-weekly-scoped-fable", "Fable only", new(18, 10080, reset3, "Nov 28 at 8:00AM"))],
            UpdatedAt = Now
        };
        const string cliJson = """
            [{"provider":"claude","source":"oauth","usage":{"loginMethod":"Claude Max 20x",
              "primary":{"usedPercent":37.5,"windowMinutes":300,"resetsAt":"2026-11-25T08:00:00Z","resetDescription":"Nov 25 at 8:00AM"},
              "secondary":{"usedPercent":12,"windowMinutes":10080,"resetsAt":"2026-11-30T03:00:00Z","resetDescription":"Nov 30 at 3:00AM"},
              "tertiary":{"usedPercent":4,"windowMinutes":10080,"resetsAt":"2026-11-30T03:00:00Z","resetDescription":"Nov 30 at 3:00AM"},
              "extraRateWindows":[{"id":"claude-weekly-scoped-fable","title":"Fable only",
                "window":{"usedPercent":18,"windowMinutes":10080,"resetsAt":"2026-11-28T08:00:00Z","resetDescription":"Nov 28 at 8:00AM"}}],
              "updatedAt":"2026-11-25T06:00:00Z"}}]
            """;

        var (fromNative, fromCli) = await ReadBothRoutesAsync("claude", nativeResult, cliJson);

        fromNative.Should().BeEquivalentTo(fromCli, WithoutClockDerivedMembers);
        fromNative.Session!.Percent.Should().Be(37.5);
        fromNative.ExtraWindows.Should().ContainSingle();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_NativeSignInMissing_GivesSameUsageDataAsEquivalentCliJson()
    {
        var nativeResult = new NativeUsageResult
        {
            Provider = "codex", Source = "oauth", Status = NativeUsageStatus.SignInMissing,
            Message = "Not signed in to Codex on Windows."
        };
        const string cliJson = """
            [{"provider":"codex","source":"oauth","error":{"kind":"auth","code":401,"message":"Not signed in to Codex on Windows."},
              "authRequired":true,"authError":"Not signed in to Codex on Windows."}]
            """;

        var (fromNative, fromCli) = await ReadBothRoutesAsync("codex", nativeResult, cliJson);

        fromNative.Should().BeEquivalentTo(fromCli, WithoutClockDerivedMembers);
        fromNative.AuthRequired.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_NativeCachedReading_GivesSameUsageDataAsEquivalentCliJson()
    {
        var nativeResult = Ok("gemini", 61) with { Source = "remote", Cached = true, CacheUpdatedAt = Now.AddMinutes(-4), UpdatedAt = Now.AddMinutes(-4) };
        const string cliJson = """
            [{"provider":"gemini","source":"remote","cached":true,"cacheUpdatedAt":"2026-11-25T05:56:00Z",
              "usage":{"primary":{"usedPercent":61,"windowMinutes":300,"resetsAt":"2026-11-25T08:00:00Z","resetDescription":"in 2h"},
              "updatedAt":"2026-11-25T05:56:00Z"}}]
            """;

        var (fromNative, fromCli) = await ReadBothRoutesAsync("gemini", nativeResult, cliJson);

        fromNative.Should().BeEquivalentTo(fromCli, WithoutClockDerivedMembers);
        fromNative.UsageIsCached.Should().BeTrue();
    }

    /// <summary>Members computed from the wall clock on each read (time left, pace), not from the parsed JSON.</summary>
    private static EquivalencyOptions<UsageData> WithoutClockDerivedMembers(EquivalencyOptions<UsageData> options) =>
        options.Excluding(m => m.Name == "TimeUntilReset" || m.Name == "ElapsedPercent" || m.Name == "Pace");

    /// <summary>Reads one provider natively (real NativeUsageService, fake reader) and through the CLI batch.</summary>
    private async Task<(UsageData Native, UsageData Cli)> ReadBothRoutesAsync(string provider, NativeUsageResult nativeResult, string cliJson)
    {
        _settings.SetProviders([new() { Id = provider, IsEnabled = true }]);
        var nativeService = new NativeUsageService([new FakeClient(provider, nativeResult)], NullLogger<NativeUsageService>.Instance);
        var fromNative = (await Create(new Mock<IWslService>(MockBehavior.Strict).Object, nativeService, new MockCacheService())
            .GetAllUsageAsync()).Single();

        _settings.Settings.UsageSources[provider] = UsageSource.Wsl;
        var wsl = new MockWslService();
        var verbose = provider == "gemini" ? " --verbose" : "";
        wsl.SetCommandResult($"codexbar usage --provider {provider} --format json --source cli --status{verbose}",
            new WslResult { Success = true, Output = cliJson });
        var fromCli = (await Create(wsl, nativeService, new MockCacheService()).GetAllUsageAsync()).Single();
        wsl.ExecutedBatches.Should().ContainSingle();
        return (fromNative, fromCli);
    }

    [TestMethod]
    public async Task GetVersionAsync_AllEnabledProvidersNative_ReportsNativeWithoutWsl()
    {
        var wsl = new Mock<IWslService>(MockBehavior.Strict);
        var service = Create(wsl.Object, NativeOk());

        (await service.GetVersionAsync()).Should().Be(CodexBarService.NativeVersion);
        (await service.IsAvailableAsync()).Should().BeTrue();
        wsl.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task GetVersionAsync_DisabledProviderSetToWsl_StillReportsNative()
    {
        _settings.Settings.Providers[2].IsEnabled = false;
        _settings.Settings.UsageSources["gemini"] = UsageSource.Wsl;
        var wsl = new Mock<IWslService>(MockBehavior.Strict);

        (await Create(wsl.Object, NativeOk()).GetVersionAsync()).Should().Be(CodexBarService.NativeVersion);
        wsl.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task GetVersionAsync_EnabledProviderSetToWsl_RunsCli()
    {
        _settings.Settings.UsageSources["claude"] = UsageSource.Wsl;
        var wsl = new MockWslService();
        wsl.SetCommandResult("codexbar --version", new WslResult { Success = true, Output = "CodexBar 0.20.0\n" });
        wsl.SetCommandResult("which codexbar", new WslResult { Success = true, Output = "/usr/local/bin/codexbar" });
        var service = Create(wsl, NativeOk());

        (await service.GetVersionAsync()).Should().Be("CodexBar 0.20.0");
        (await service.IsAvailableAsync()).Should().BeTrue();
        wsl.ExecutedCommands.Should().Equal("codexbar --version", "which codexbar");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Review finding 2: a replayed last-good reading must not bring back a window that has reset since.
    [TestMethod]
    [DataRow(NativeUsageStatus.SignInExpired)]
    [DataRow(NativeUsageStatus.RateLimited)]
    [DataRow(NativeUsageStatus.Failed)]
    public async Task GetAllUsageAsync_WhenNativeReturnsClearedPastResetReplay_PublishesZeroedWindow(NativeUsageStatus failure)
    {
        var readAt = new DateTimeOffset(2026, 11, 25, 8, 25, 0, TimeSpan.Zero);
        var sessionReset = new DateTimeOffset(2026, 11, 25, 8, 30, 0, TimeSpan.Zero);
        var reading = new NativeUsageResult
        {
            Provider = "claude", Source = "oauth", Status = NativeUsageStatus.Ok,
            Primary = new(80, 300, sessionReset, "Nov 25 at 8:30AM"),
            Secondary = new(40, 10080, readAt.AddDays(3), "Nov 28 at 8:25AM"),
            UpdatedAt = readAt
        };
        var current = reading;
        _settings.SetProviders([new() { Id = "claude", IsEnabled = true }]);
        var service = Create(new Mock<IWslService>(MockBehavior.Strict).Object, new FakeNativeUsage(_ => current));
        _clock.Now = readAt;
        (await service.GetAllUsageAsync()).Single().Session!.Percent.Should().Be(80);

        _clock.Now = readAt.AddMinutes(15);
        current = NativeLastGood.FromCacheOr(reading,
            new NativeUsageResult { Provider = "claude", Status = failure, Message = "synthetic failure" }, _clock.Now);
        var result = (await service.GetAllUsageAsync()).Single();

        result.Session!.Percent.Should().Be(0);
        result.Session.ResetAt.Should().BeNull();
        result.Weekly!.Percent.Should().Be(40, "the weekly window has not reset");
        ((ICacheService)_cache).GetLastKnown("claude")!.Session!.Percent.Should().Be(0, "the cache must not keep the ended window either");
    }

    [TestMethod]
    public async Task GetUsageAndAllUsageAsync_WithTypedUsageService_CallsFetchUsageDtoAsyncWithoutJsonRoundtrip()
    {
        _settings.SetProviders([new() { Id = "claude", IsEnabled = true }]);
        var typed = new Mock<INativeTypedUsageService>(MockBehavior.Strict);
        typed.Setup(n => n.FetchUsageDtoAsync("claude", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NativeUsageDtoMapper.ToDto(Ok("claude", 33)));
        var service = Create(new Mock<IWslService>(MockBehavior.Strict).Object, typed.Object);

        var single = await service.GetUsageAsync("claude");
        var all = await service.GetAllUsageAsync();

        single!.Session!.Percent.Should().Be(33);
        all.Should().ContainSingle().Which.Session!.Percent.Should().Be(33);
        typed.Verify(n => n.FetchUsageDtoAsync("claude", It.IsAny<CancellationToken>()), Times.Exactly(2));
        typed.VerifyNoOtherCalls();
    }

    private sealed class FakeClient(string provider, NativeUsageResult result) : INativeUsageClient
    {
        public string ProviderId => provider;
        public Task<NativeUsageResult> FetchAsync(CancellationToken ct = default) => Task.FromResult(result);
    }

    /// <summary>Returns the JSON the real service would for each provider; records which providers were read.</summary>
    private sealed class FakeNativeUsage(Func<string, NativeUsageResult> read) : INativeUsageService
    {
        private readonly List<string> _calls = [];
        public string? Hang { get; init; }
        public IReadOnlyList<string> Calls { get { lock (_calls) return [.. _calls]; } }

        public async Task<string> FetchUsageJsonAsync(string provider, CancellationToken ct = default)
        {
            var normalized = ProviderConstants.ValidateAndNormalize(provider);
            lock (_calls) _calls.Add(normalized);
            if (normalized == Hang) await Task.Delay(Timeout.Infinite, ct);
            await Task.Yield();
            return NativeUsageService.Serialize(read(normalized));
        }

        public async Task<IReadOnlyDictionary<string, string>> FetchAllUsageJsonAsync(IReadOnlyList<string> providers, CancellationToken ct = default)
        {
            var result = new Dictionary<string, string>();
            foreach (var p in providers) result[ProviderConstants.ValidateAndNormalize(p)] = await FetchUsageJsonAsync(p, ct);
            return result;
        }
    }
}
