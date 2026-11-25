using System.Net;
using AwesomeAssertions;
using CodexBarWin.Services.Native;
using static CodexBarWin.Tests.Unit.Services.Native.NativeTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native;

/// <summary>The 429 cooldown survives a restart. Synthetic sign-in and fake HTTP only.</summary>
[TestClass]
public class ClaudeCooldownStoreTests
{
    private string _credentialsPath = null!;
    private string _cooldownPath = null!;

    [TestInitialize]
    public void Setup()
    {
        _credentialsPath = TempFile($$$"""
            {"claudeAiOauth": {"accessToken": "synthetic-claude-access-token", "expiresAt": {{{Now.AddHours(5).ToUnixTimeMilliseconds()}}}}}
            """);
        _cooldownPath = Path.Combine(Path.GetDirectoryName(_credentialsPath)!, ClaudeCooldownFile.FileName);
    }

    [TestCleanup]
    public void Cleanup() => DeleteTemp(_credentialsPath);

    private ClaudeNativeClient CreateClient(RecordingHandler handler, TestClock clock, IClaudeCooldownStore? store) =>
        new(handler.Client(), new ListLogger<ClaudeNativeClient>(),
            new ClaudeNativeOptions { CredentialsPath = _credentialsPath, DisplayTimeZone = TimeZoneInfo.Utc }, clock, store);

    [TestMethod]
    public async Task SaveAsync_ThenLoad_RoundTripsBlockedUntil()
    {
        var store = new ClaudeCooldownFile(new ListLogger<ClaudeCooldownFile>(), _cooldownPath);
        var until = Now.AddMinutes(10);

        await store.SaveAsync(until);

        store.Load().Should().Be(until);
        new ClaudeCooldownFile(new ListLogger<ClaudeCooldownFile>(), _cooldownPath).Load().Should().Be(until);
        File.Exists(_cooldownPath + ".tmp").Should().BeFalse();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("not json")]
    [DataRow("{}")]
    [DataRow("{\"blockedUntil\": 5}")]
    [DataRow("[]")]
    public void Load_MissingOrDamagedFile_ReturnsNull(string? content)
    {
        if (content != null) File.WriteAllText(_cooldownPath, content);
        var store = new ClaudeCooldownFile(new ListLogger<ClaudeCooldownFile>(), _cooldownPath);

        store.Load().Should().BeNull();
    }

    [TestMethod]
    public async Task FetchAsync_When429_SavesBlockedUntil()
    {
        var store = new FakeCooldownStore();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var client = CreateClient(handler, new TestClock(Now), store);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.RateLimited);
        store.Saved.Should().ContainSingle().Which.Should().Be(Now.AddMinutes(10));
    }

    [TestMethod]
    public async Task FetchAsync_WhenOk_DoesNotSaveCooldown()
    {
        var store = new FakeCooldownStore();
        var handler = new RecordingHandler(_ => Json("""{"five_hour": {"utilization": 10.0}}"""));
        var client = CreateClient(handler, new TestClock(Now), store);

        (await client.FetchAsync()).Status.Should().Be(NativeUsageStatus.Ok);

        store.Saved.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Constructor_SavedCooldownStillRunning_DoesNotCallEndpoint()
    {
        var store = new FakeCooldownStore { Stored = Now.AddMinutes(4) };
        var handler = new RecordingHandler(_ => Json("""{"five_hour": {"utilization": 10.0}}"""));
        var client = CreateClient(handler, new TestClock(Now), store);

        var result = await client.FetchAsync();

        client.BlockedUntil.Should().Be(Now.AddMinutes(4));
        result.Status.Should().Be(NativeUsageStatus.RateLimited);
        result.RetryAfter.Should().Be(Now.AddMinutes(4));
        result.NetworkCalls.Should().Be(0);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Constructor_SavedCooldownPassed_CallsEndpoint()
    {
        var store = new FakeCooldownStore { Stored = Now.AddMinutes(-1) };
        var handler = new RecordingHandler(_ => Json("""{"five_hour": {"utilization": 10.0}}"""));
        var client = CreateClient(handler, new TestClock(Now), store);

        (await client.FetchAsync()).Status.Should().Be(NativeUsageStatus.Ok);

        handler.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public void Constructor_SavedCooldownTooFarAhead_IsCapped()
    {
        var store = new FakeCooldownStore { Stored = Now.AddYears(5) };
        var client = CreateClient(new RecordingHandler(_ => Json("{}")), new TestClock(Now), store);

        client.BlockedUntil.Should().Be(Now + ClaudeNativeClient.MaxPersistedCooldown);
    }

    [TestMethod]
    public async Task FetchAsync_429ThenRestart_NewClientStaysInCooldown()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var before = CreateClient(handler, new TestClock(Now), new ClaudeCooldownFile(new ListLogger<ClaudeCooldownFile>(), _cooldownPath));
        await before.FetchAsync();

        // "Restart": a new client and store over the same file, two minutes later.
        var after = CreateClient(handler, new TestClock(Now.AddMinutes(2)),
            new ClaudeCooldownFile(new ListLogger<ClaudeCooldownFile>(), _cooldownPath));
        var result = await after.FetchAsync();

        after.BlockedUntil.Should().Be(Now.AddMinutes(10));
        result.Status.Should().Be(NativeUsageStatus.RateLimited);
        handler.Requests.Should().ContainSingle("the restarted client must not call during the cooldown");
    }

    private sealed class FakeCooldownStore : IClaudeCooldownStore
    {
        public DateTimeOffset? Stored { get; set; }
        public List<DateTimeOffset> Saved { get; } = [];
        public DateTimeOffset? Load() => Stored;
        public Task SaveAsync(DateTimeOffset blockedUntil, CancellationToken ct = default)
        {
            Saved.Add(blockedUntil);
            return Task.CompletedTask;
        }
    }
}
