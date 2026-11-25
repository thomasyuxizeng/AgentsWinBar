using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services.Native;
using static CodexBarWin.Tests.Unit.Services.Native.NativeTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native;

[TestClass]
public class NativeUsageServiceTests
{
    private static NativeUsageResult Ok(string provider, double percent) => new()
    {
        Provider = provider,
        Source = provider == "gemini" ? "remote" : "oauth",
        Status = NativeUsageStatus.Ok,
        Primary = new(percent, 300, Now.AddHours(2), null),
        UpdatedAt = Now
    };

    private static NativeUsageService Create(params INativeUsageClient[] clients) =>
        new(clients, new ListLogger<NativeUsageService>());

    private static List<UsageDataDto> Parse(string json) =>
        JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageDataDto)!;

    [TestMethod]
    public async Task FetchUsageJsonAsync_WhenOk_ReturnsCliArrayShape()
    {
        var service = Create(new FakeClient("claude", _ => Task.FromResult(Ok("claude", 42))));

        var json = await service.FetchUsageJsonAsync("claude");

        json.TrimStart().Should().StartWith("[");
        var dto = Parse(json).Should().ContainSingle().Subject;
        dto.Provider.Should().Be("claude");
        dto.Source.Should().Be("oauth");
        dto.Usage!.Primary!.UsedPercent.Should().Be(42);
        dto.Usage.Primary.WindowMinutes.Should().Be(300);
        dto.Error.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    public async Task FetchUsageJsonAsync_GeminiProvider_UsesAntigravityClient()
    {
        var claude = new FakeClient("claude", _ => Task.FromResult(Ok("claude", 1)));
        var gemini = new FakeClient("gemini", _ => Task.FromResult(Ok("gemini", 77)));
        var service = Create(claude, gemini);

        var dto = Parse(await service.FetchUsageJsonAsync("gemini")).Single();

        gemini.Calls.Should().Be(1);
        claude.Calls.Should().Be(0);
        dto.Provider.Should().Be("gemini");
        dto.Usage!.Primary!.UsedPercent.Should().Be(77);
    }

    [TestMethod]
    public async Task FetchUsageJsonAsync_MixedCaseProvider_IsNormalized()
    {
        var claude = new FakeClient("claude", _ => Task.FromResult(Ok("claude", 5)));

        var dto = Parse(await Create(claude).FetchUsageJsonAsync("  CLAUDE ")).Single();

        dto.Provider.Should().Be("claude");
        claude.Calls.Should().Be(1);
    }

    [TestMethod]
    [DataRow("openai")]
    [DataRow("claude; rm -rf /")]
    [DataRow("")]
    public async Task FetchUsageJsonAsync_InvalidProvider_ThrowsArgumentException(string provider)
    {
        var service = Create(new FakeClient("claude", _ => Task.FromResult(Ok("claude", 5))));

        var act = () => service.FetchUsageJsonAsync(provider);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task FetchUsageJsonAsync_WhenClientThrows_ReturnsCliErrorObject()
    {
        var service = Create(new FakeClient("codex", _ => throw new InvalidOperationException("bug")));

        var dto = Parse(await service.FetchUsageJsonAsync("codex")).Single();

        dto.Provider.Should().Be("codex");
        dto.Usage.Should().BeNull();
        dto.Error.GetProperty("message").GetString().Should().Be("Native usage read failed.");
        dto.ToUsageData(Now.UtcDateTime).HasError.Should().BeTrue();
    }

    [TestMethod]
    public async Task FetchUsageJsonAsync_WhenCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        var service = Create(new FakeClient("codex", async ct => { await Task.Delay(Timeout.Infinite, ct); return Ok("codex", 1); }));

        var fetch = service.FetchUsageJsonAsync("codex", cts.Token);
        cts.Cancel();

        await ((Func<Task>)(() => fetch)).Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task FetchUsageJsonAsync_ProviderWithoutClient_ReturnsErrorObject()
    {
        var dto = Parse(await Create().FetchUsageJsonAsync("codex")).Single();

        dto.Provider.Should().Be("codex");
        dto.ToUsageData(Now.UtcDateTime).HasError.Should().BeTrue();
    }

    [TestMethod]
    public async Task FetchAllUsageJsonAsync_ThreeProviders_ReadsInParallel()
    {
        // Each read finishes only once all three have started, so a sequential service would never finish.
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<NativeUsageResult>> Read(string provider) => async ct =>
        {
            if (Interlocked.Increment(ref started) == 3) allStarted.TrySetResult();
            await allStarted.Task.WaitAsync(ct);
            return Ok(provider, 10);
        };
        var service = Create(new FakeClient("claude", Read("claude")), new FakeClient("codex", Read("codex")),
            new FakeClient("gemini", Read("gemini")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var all = await service.FetchAllUsageJsonAsync(["claude", "CODEX", "gemini", "claude"], timeout.Token);

        all.Keys.Should().BeEquivalentTo(["claude", "codex", "gemini"]);
        Parse(all["codex"]).Single().Provider.Should().Be("codex");
    }

    [TestMethod]
    public void CreateClaudeOptions_CacheLifetime_IsNineAndAHalfMinutes()
    {
        NativeUsageService.CreateClaudeOptions().CacheLifetime.Should().Be(TimeSpan.FromMinutes(9.5));
        // At a 300 s refresh interval the endpoint is called every second refresh: the 600 s reading is past the
        // lifetime, the 300 s one is not.
        TimeSpan.FromSeconds(300).Should().BeLessThan(NativeUsageService.ClaudeCacheLifetime);
        TimeSpan.FromSeconds(600).Should().BeGreaterThan(NativeUsageService.ClaudeCacheLifetime);
    }

    [TestMethod]
    public async Task ClaudeClient_WithAppOptions_ServesCacheAtFiveMinutesAndCallsAtTen()
    {
        var path = TempFile($$$"""
            {"claudeAiOauth": {"accessToken": "synthetic-claude-access-token", "expiresAt": {{{Now.AddDays(1).ToUnixTimeMilliseconds()}}}}}
            """);
        try
        {
            var handler = new RecordingHandler(_ => Json("""{"five_hour": {"utilization": 10.0}}"""));
            var clock = new TestClock(Now);
            var client = new ClaudeNativeClient(handler.Client(), new ListLogger<ClaudeNativeClient>(),
                NativeUsageService.CreateClaudeOptions() with { CredentialsPath = path, DisplayTimeZone = TimeZoneInfo.Utc }, clock);

            await client.FetchAsync();
            clock.Now = Now.AddSeconds(300);
            var second = await client.FetchAsync();
            clock.Now = Now.AddSeconds(600);
            await client.FetchAsync();

            second.Cached.Should().BeTrue();
            handler.Requests.Should().HaveCount(2);
        }
        finally { DeleteTemp(path); }
    }

    [TestMethod]
    public void Serialize_SignInMissing_KeepsAuthFieldsForInterpretation()
    {
        var json = NativeUsageService.Serialize(new NativeUsageResult
        {
            Provider = "claude", Status = NativeUsageStatus.SignInMissing, Message = "Not signed in to Claude on Windows."
        });

        var data = Parse(json).Single().ToUsageData(Now.UtcDateTime);

        data.AuthRequired.Should().BeTrue();
        data.HasError.Should().BeTrue();
    }

    [TestMethod]
    public async Task FetchUsageDtoAsync_WhenOk_MatchesSerializedJsonDto()
    {
        var service = Create(new FakeClient("claude", _ => Task.FromResult(Ok("claude", 42))));

        var dto = await service.FetchUsageDtoAsync("  CLAUDE ");
        var fromJson = Parse(await service.FetchUsageJsonAsync("claude")).Single();

        dto.Provider.Should().Be("claude");
        dto.Source.Should().Be("oauth");
        dto.Usage!.Primary!.UsedPercent.Should().Be(42);
        dto.ToUsageData(Now.UtcDateTime).Should().BeEquivalentTo(fromJson.ToUsageData(Now.UtcDateTime),
            options => options.Excluding(m => m.Name == "TimeUntilReset" || m.Name == "ElapsedPercent" || m.Name == "Pace"));
    }

    [TestMethod]
    public async Task FetchUsageDtoAsync_WhenClientThrows_ReturnsErrorDto()
    {
        var service = Create(new FakeClient("codex", _ => throw new InvalidOperationException("bug")));

        var dto = await service.FetchUsageDtoAsync("codex");

        dto.Provider.Should().Be("codex");
        dto.Usage.Should().BeNull();
        dto.ToUsageData(Now.UtcDateTime).HasError.Should().BeTrue();
    }

    private sealed class FakeClient(string provider, Func<CancellationToken, Task<NativeUsageResult>> read) : INativeUsageClient
    {
        private int _calls;
        public int Calls => _calls;
        public string ProviderId => provider;
        public Task<NativeUsageResult> FetchAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return read(ct);
        }
    }
}
