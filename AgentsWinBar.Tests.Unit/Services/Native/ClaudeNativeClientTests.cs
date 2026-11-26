using System.Net;
using AwesomeAssertions;
using AgentsWinBar.Services.Native;
using static AgentsWinBar.Tests.Unit.Services.Native.NativeTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native;

[TestClass]
public class ClaudeNativeClientTests
{
    private const string SyntheticToken = "synthetic-claude-access-token";

    // Recorded shape of /api/oauth/usage with synthetic values.
    private const string UsageJson = """
        {
          "five_hour": {"utilization": 37.0, "resets_at": "2026-11-25T08:29:59.912345+00:00"},
          "seven_day": {"utilization": 12.5, "resets_at": "2026-11-30T03:00:00+00:00"},
          "seven_day_oauth_apps": null,
          "seven_day_opus": null,
          "seven_day_sonnet": {"utilization": 4.0, "resets_at": "2026-11-30T03:00:00+00:00"},
          "extra_usage": {"is_enabled": false, "monthly_limit": null, "used_credits": null, "utilization": null},
          "limits": [
            {"kind": "weekly_scoped", "group": "weekly", "percent": 18, "resets_at": "2026-11-28T08:00:00Z",
             "is_active": false, "scope": {"model": {"id": "fable", "display_name": "Fable"}}},
            {"kind": "weekly_scoped", "group": "weekly", "percent": 50, "scope": {"model": {"id": "all-models", "display_name": "All models"}}},
            {"kind": "weekly_scoped", "group": "weekly", "percent": true, "scope": {"model": {"id": "odd", "display_name": "Odd"}}},
            {"kind": "session", "group": "five_hour", "percent": 37}
          ],
          "unknown_future_field": {"nested": [1, 2, 3]}
        }
        """;

    private string _path = null!;

    [TestInitialize]
    public void Setup() => _path = TempFile(Credentials(Now.AddHours(5)));

    [TestCleanup]
    public void Cleanup() => DeleteTemp(_path);

    private static string Credentials(DateTimeOffset expiresAt, string subscription = "team") => $$"""
        {
          "mcpOAuth": {"some-server": {"accessToken": "synthetic-other-token", "expiresAt": 1 } },
          "otherProviderTokens": ["synthetic-a", "synthetic-b"],
          "claudeAiOauth": {
            "accessToken": "{{SyntheticToken}}",
            "refreshToken": "synthetic-claude-refresh-token",
            "expiresAt": {{expiresAt.ToUnixTimeMilliseconds()}},
            "refreshTokenExpiresAt": 1,
            "scopes": ["user:inference", "user:profile"],
            "subscriptionType": "{{subscription}}",
            "rateLimitTier": "default_claude_max_20x"
          }
        }
        """;

    private (ClaudeNativeClient Client, RecordingHandler Handler, TestClock Clock) Create(Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        ClaudeNativeOptions? options = null)
    {
        var handler = new RecordingHandler(respond ?? (_ => Json(UsageJson)));
        var clock = new TestClock(Now);
        var client = new ClaudeNativeClient(handler.Client(), new ListLogger<ClaudeNativeClient>(),
            (options ?? new ClaudeNativeOptions()) with { CredentialsPath = _path, DisplayTimeZone = TimeZoneInfo.Utc }, clock);
        return (client, handler, clock);
    }

    [TestMethod]
    public async Task FetchAsync_WhenUsageReturned_MapsWindowsLikeUpstream()
    {
        var (client, handler, _) = Create();

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Ok);
        result.Provider.Should().Be("claude");
        result.Source.Should().Be("oauth");
        result.NetworkCalls.Should().Be(1);
        result.LoginMethod.Should().Be("Claude Team");
        result.Primary!.UsedPercent.Should().Be(37);
        result.Primary.WindowMinutes.Should().Be(300);
        result.Primary.ResetsAt.Should().Be(new DateTimeOffset(2026, 11, 25, 8, 29, 59, 912, TimeSpan.Zero).AddTicks(3450));
        result.Primary.ResetDescription.Should().Be("Nov 25 at 8:29AM");
        result.Secondary!.UsedPercent.Should().Be(12.5);
        result.Secondary.WindowMinutes.Should().Be(10080);
        result.Tertiary!.UsedPercent.Should().Be(4);
        result.ExtraWindows.Should().ContainSingle();
        result.ExtraWindows[0].Id.Should().Be("claude-weekly-scoped-fable");
        result.ExtraWindows[0].Title.Should().Be("Fable only");
        result.ExtraWindows[0].Window.UsedPercent.Should().Be(18);
        handler.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public async Task FetchAsync_WhenCalled_SendsUpstreamHeaders()
    {
        var (client, handler, _) = Create();

        await client.FetchAsync();

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri.Should().Be(new Uri("https://api.anthropic.com/api/oauth/usage"));
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be(SyntheticToken);
        request.Headers.GetValues("anthropic-beta").Should().Equal("oauth-2025-04-20");
        request.Headers.UserAgent.ToString().Should().Be("claude-code/2.1.0");
    }

    [TestMethod]
    public async Task FetchAsync_WhenTokenExpired_ReturnsSignInExpiredWithoutCalling()
    {
        File.WriteAllText(_path, Credentials(Now.AddMinutes(-42)));
        var (client, handler, _) = Create();

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.ExpiredFor.Should().Be(TimeSpan.FromMinutes(42));
        result.Message.Should().Contain("42 minutes ago");
        result.Message.Should().NotContain(SyntheticToken);
        result.NetworkCalls.Should().Be(0);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenCredentialFileMissing_ReturnsSignInMissing()
    {
        DeleteTemp(_path);
        var (client, handler, _) = Create();

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInMissing);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenClaudeObjectMissing_ReturnsSignInMissing()
    {
        File.WriteAllText(_path, """{"mcpOAuth": {"x": {"accessToken": "synthetic"}}}""");
        var (client, handler, _) = Create();

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInMissing);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_When429_CoolsDownAndDoesNotCallAgain()
    {
        var (client, handler, clock) = Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "30");
            return response;
        });

        var first = await client.FetchAsync();
        clock.Now = Now.AddMinutes(9);
        var second = await client.FetchAsync();

        first.Status.Should().Be(NativeUsageStatus.RateLimited);
        first.HttpStatus.Should().Be(429);
        first.RetryAfter.Should().Be(Now.AddMinutes(10), "the 10-minute cooldown outlasts a 30 s Retry-After");
        second.Status.Should().Be(NativeUsageStatus.RateLimited);
        second.NetworkCalls.Should().Be(0);
        handler.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public async Task FetchAsync_When429_LogsProviderAndCooldownAndReportsItsEnd()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var logger = new ListLogger<ClaudeNativeClient>();
        var clock = new TestClock(Now);
        var client = new ClaudeNativeClient(handler.Client(), logger,
            new ClaudeNativeOptions { CredentialsPath = _path, DisplayTimeZone = TimeZoneInfo.Utc }, clock);

        client.RateLimitedUntil.Should().BeNull();
        await client.FetchAsync();

        logger.Messages.Should().Contain(m => m.StartsWith("claude usage endpoint answered 429; cooling down 600 s until "));
        logger.Messages.Should().NotContain(m => m.Contains(_path, StringComparison.OrdinalIgnoreCase));
        client.RateLimitedUntil.Should().Be(Now.AddMinutes(10));
        clock.Now = Now.AddMinutes(10);
        client.RateLimitedUntil.Should().BeNull();
    }

    [TestMethod]
    public async Task FetchAsync_CacheLifetimeSource_IsAskedOnEveryRead()
    {
        var lifetime = TimeSpan.FromSeconds(90);
        var (client, handler, clock) = Create(options: new ClaudeNativeOptions { CacheLifetimeSource = () => lifetime });

        await client.FetchAsync();
        clock.Now = Now.AddSeconds(80);
        (await client.FetchAsync()).Cached.Should().BeTrue();
        clock.Now = Now.AddSeconds(100);
        (await client.FetchAsync()).Cached.Should().BeFalse();
        lifetime = TimeSpan.FromMinutes(10);
        clock.Now = Now.AddMinutes(5);
        (await client.FetchAsync()).Cached.Should().BeTrue();

        handler.Requests.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task FetchAsync_WhenScheduleIsOn_DueRefreshAtClaudeFloorAlwaysCalls()
    {
        var (client, handler, clock) = Create(options: NativeUsageService.CreateClaudeOptions() with
        {
            CacheLifetimeSource = () => NativeUsageService.ClaudeCacheLifetimeFor(true)
        });

        await client.FetchAsync();
        clock.Now = Now + AgentsWinBar.Helpers.ProviderRefreshPlanner.ClaudeActiveFloor;
        var due = await client.FetchAsync();

        due.Cached.Should().BeFalse();
        handler.Requests.Should().HaveCount(2);
        NativeUsageService.ClaudeCacheLifetimeFor(false).Should().Be(NativeUsageService.ClaudeCacheLifetime);
    }

    [TestMethod]
    public async Task FetchAsync_When429WithLongRetryAfter_HonoursRetryAfter()
    {
        var (client, _, _) = Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "1800");
            return response;
        });

        var result = await client.FetchAsync();

        result.RetryAfter.Should().Be(Now.AddMinutes(30));
        client.BlockedUntil.Should().Be(Now.AddMinutes(30));
    }

    [TestMethod]
    public async Task FetchAsync_WhenCooldownEnds_CallsAgain()
    {
        var calls = 0;
        var (client, handler, clock) = Create(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json(UsageJson));

        await client.FetchAsync();
        clock.Now = Now.AddMinutes(10).AddSeconds(1);
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Ok);
        handler.Requests.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task FetchAsync_WhenRateLimitedAfterSuccess_ServesLastGoodReadingAsCached()
    {
        var calls = 0;
        var (client, handler, clock) = Create(_ => ++calls == 1 ? Json(UsageJson) : new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        await client.FetchAsync();
        clock.Now = Now.AddMinutes(11);
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.RateLimited);
        result.Cached.Should().BeTrue();
        result.CacheUpdatedAt.Should().Be(Now);
        result.Primary!.UsedPercent.Should().Be(37);
        handler.Requests.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task FetchAsync_WithinCacheLifetime_ServesCacheWithoutCalling()
    {
        var (client, handler, clock) = Create();

        await client.FetchAsync();
        clock.Now = Now.AddMinutes(9);
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Ok);
        result.Cached.Should().BeTrue();
        result.NetworkCalls.Should().Be(0);
        handler.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public async Task FetchAsync_WhenCachedWindowHasReset_CallsAgain()
    {
        // The 5-hour window resets at 08:29:59. Read at 08:25, then again at 08:31: within the 10-minute cache
        // lifetime, but past the reset, so the cached numbers are stale and the client must call.
        var (client, handler, clock) = Create();
        clock.Now = new DateTimeOffset(2026, 11, 25, 8, 25, 0, TimeSpan.Zero);
        await client.FetchAsync();
        clock.Now = new DateTimeOffset(2026, 11, 25, 8, 31, 0, TimeSpan.Zero);
        var result = await client.FetchAsync();

        result.Cached.Should().BeFalse();
        handler.Requests.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task FetchAsync_When401_ReturnsUnauthorized()
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Unauthorized);
        result.HttpStatus.Should().Be(401);
    }

    [TestMethod]
    public async Task FetchAsync_WhenResponseHasNoWindows_ReturnsFailed()
    {
        var (client, _, _) = Create(_ => Json("""{"five_hour": null, "seven_day": null}"""));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        result.HasUsage.Should().BeFalse();
    }

    [TestMethod]
    public void ToString_WhenCredentialsHaveToken_OmitsTokenValue()
    {
        var credentials = new ClaudeCredentials(SyntheticToken, Now, "team", "default_claude_max_20x");

        var text = credentials.ToString();

        text.Should().NotContain(SyntheticToken);
        text.Should().Contain("ExpiresAt").And.Contain("team");
    }

    [TestMethod]
    public void ToString_WhenParsedFromFile_OmitsEveryTokenInTheFile()
    {
        var credentials = ClaudeNativeClient.ParseCredentials(System.Text.Encoding.UTF8.GetBytes(Credentials(Now)))!;

        credentials.ToString().Should().NotContain("synthetic-claude");
    }

    [TestMethod]
    public void ResolveCredentialsPath_WhenClaudeConfigDirSet_IgnoresIt()
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(Path.GetTempPath(), "synthetic-claude-config"));

            new ClaudeNativeOptions().ResolveCredentialsPath().Should().Be(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"));
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous); }
    }

    [TestMethod]
    public void ResolveCredentialsPath_WhenExplicitPathGiven_UsesIt()
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(Path.GetTempPath(), "synthetic-claude-config"));
            var explicitPath = Path.Combine(Path.GetTempPath(), "synthetic-explicit", ".credentials.json");

            new ClaudeNativeOptions { CredentialsPath = explicitPath }.ResolveCredentialsPath().Should().Be(explicitPath);
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous); }
    }

    [TestMethod]
    public async Task FetchAsync_WhenTokenExpiredAfterGoodReading_ServesLastGoodReadingAsCached()
    {
        var (client, handler, clock) = Create();
        await client.FetchAsync();
        File.WriteAllText(_path, Credentials(Now.AddMinutes(1)));
        clock.Now = Now.AddMinutes(11);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.Cached.Should().BeTrue();
        result.CacheUpdatedAt.Should().Be(Now);
        result.Primary!.UsedPercent.Should().Be(37);
        result.NetworkCalls.Should().Be(0);
        handler.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public async Task FetchAsync_WhenServedLastGoodHasPastReset_ReportsThatWindowAsUnused()
    {
        // The 5-hour window resets at 08:29:59; the failure happens at 08:40, the weekly windows reset on 11-30.
        var calls = 0;
        var (client, _, clock) = Create(_ => ++calls == 1 ? Json(UsageJson) : new HttpResponseMessage(HttpStatusCode.BadGateway));
        var fresh = await client.FetchAsync();
        clock.Now = new DateTimeOffset(2026, 11, 25, 8, 40, 0, TimeSpan.Zero);

        var result = await client.FetchAsync();

        fresh.Primary!.UsedPercent.Should().Be(37, "a fresh reading is passed through untouched");
        result.Cached.Should().BeTrue();
        result.Primary!.UsedPercent.Should().Be(0);
        result.Primary.ResetsAt.Should().BeNull();
        result.Secondary!.UsedPercent.Should().Be(12.5);
        result.Secondary.ResetsAt.Should().NotBeNull();
        result.ExtraWindows[0].Window.UsedPercent.Should().Be(18);
    }

    [TestMethod]
    public async Task FetchAsync_WhenTokenExpiredWithoutGoodReading_ReturnsSignInExpiredOnly()
    {
        File.WriteAllText(_path, Credentials(Now.AddMinutes(-5)));
        var (client, _, _) = Create();

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.Cached.Should().BeFalse();
        result.HasUsage.Should().BeFalse();
    }

    [TestMethod]
    public void ParseCredentials_WhenOtherKeysPresent_ReadsOnlyClaudeObject()
    {
        var credentials = ClaudeNativeClient.ParseCredentials(System.Text.Encoding.UTF8.GetBytes(Credentials(Now)));

        credentials!.AccessToken.Should().Be(SyntheticToken);
        credentials.ExpiresAt.Should().Be(Now);
        credentials.SubscriptionType.Should().Be("team");
        credentials.RateLimitTier.Should().Be("default_claude_max_20x");
    }

    // Review finding 1: an absurd Retry-After must neither throw nor block Claude for more than a day.
    [TestMethod]
    public async Task FetchAsync_When429HasHugeRetryAfter_CapsLiveCooldownToMaxPersistedCooldown()
    {
        var store = new RecordingCooldownStore();
        var handler = new RecordingHandler(_ => TooManyRequests("31536000"));
        var client = new ClaudeNativeClient(handler.Client(), new ListLogger<ClaudeNativeClient>(),
            new ClaudeNativeOptions { CredentialsPath = _path, DisplayTimeZone = TimeZoneInfo.Utc }, new TestClock(Now), store);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.RateLimited);
        result.RetryAfter.Should().Be(Now + ClaudeNativeClient.MaxPersistedCooldown);
        client.BlockedUntil.Should().Be(Now + ClaudeNativeClient.MaxPersistedCooldown);
        store.Saved.Should().Equal(Now + ClaudeNativeClient.MaxPersistedCooldown);
    }

    [TestMethod]
    [DataRow("Infinity")]
    [DataRow("1e20")]
    [DataRow("999999999999")]
    [DataRow("Fri, 31 Dec 9999 23:59:59 GMT")]
    public async Task FetchAsync_When429HasAbsurdRetryAfter_ServesLastGoodWithBoundedCooldown(string retryAfter)
    {
        var calls = 0;
        var (client, _, clock) = Create(_ => ++calls == 1 ? Json(UsageJson) : TooManyRequests(retryAfter));
        await client.FetchAsync();
        clock.Now = Now.AddMinutes(11);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.RateLimited);
        result.Cached.Should().BeTrue("a 429 keeps the last good reading");
        result.Primary!.UsedPercent.Should().Be(37);
        client.BlockedUntil.Should().BeOnOrBefore(clock.Now + ClaudeNativeClient.MaxPersistedCooldown);
    }

    // Review finding 5: a missing or rejected sign-in must not keep showing the old reading.
    [TestMethod]
    public async Task FetchAsync_WhenUnauthorizedAfterGoodReading_DoesNotServeLastGoodReading()
    {
        var calls = 0;
        var (client, _, clock) = Create(_ => ++calls == 1 ? Json(UsageJson) : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await client.FetchAsync();
        clock.Now = Now.AddMinutes(11);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Unauthorized);
        result.Cached.Should().BeFalse();
        result.HasUsage.Should().BeFalse();
    }

    [TestMethod]
    public async Task FetchAsync_WhenSignInMissingAfterGoodReading_DoesNotServeLastGoodReading()
    {
        var (client, _, clock) = Create();
        await client.FetchAsync();
        File.Delete(_path);
        clock.Now = Now.AddMinutes(11);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInMissing);
        result.Cached.Should().BeFalse();
        result.HasUsage.Should().BeFalse();
    }

    [TestMethod]
    public async Task FetchAsync_WhenTransientFailureFollowsRejectedSignIn_DoesNotServeReadingFromBeforeRejection()
    {
        var calls = 0;
        var (client, _, clock) = Create(_ => ++calls switch
        {
            1 => Json(UsageJson),
            2 => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        });
        await client.FetchAsync();
        clock.Now = Now.AddMinutes(11);
        await client.FetchAsync();
        clock.Now = Now.AddMinutes(22);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        result.Cached.Should().BeFalse("the sign-in that produced the old reading was rejected");
        result.HasUsage.Should().BeFalse();
    }

    // Review finding 6: the response body is read only up to a fixed cap.
    [TestMethod]
    public async Task FetchAsync_WhenResponseExceedsSizeLimit_FailsWithoutReadingWholeBody()
    {
        var body = new PaddedJsonStream("{\"five_hour\": {\"utilization\": 37.0, \"resets_at\": \"2026-11-25T08:29:59Z\"}, \"padding\": \"",
            HugePadding, "\"}");
        var (client, _, _) = Create(_ => Streamed(body));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        body.Served.Should().BeLessThanOrEqualTo(NativeHttp.MaxResponseBytes + 1);
    }

    // Review finding 7: a known field holding an object or array must not derail the reader.
    [TestMethod]
    public void ParseCredentials_WhenKnownFieldPrecedingTokenIsObjectOrArray_StillReadsAccessToken()
    {
        var json = """
            {"claudeAiOauth": {"subscriptionType": ["pro"], "expiresAt": {"ms": 1}, "rateLimitTier": {"nested": 1},
                               "accessToken": "synthetic-claude-access-token"}}
            """;

        var credentials = ClaudeNativeClient.ParseCredentials(System.Text.Encoding.UTF8.GetBytes(json));

        credentials.Should().NotBeNull();
        credentials!.AccessToken.Should().Be(SyntheticToken);
        credentials.SubscriptionType.Should().BeNull();
    }

    // Review finding 9: a clock that steps backward must not stretch the cache or the cooldown.
    [TestMethod]
    public async Task FetchAsync_WhenClockMovesBackward_DoesNotServeStaleFreshCache()
    {
        var (client, handler, clock) = Create();
        await client.FetchAsync();
        clock.Now = Now.AddHours(-2);

        var result = await client.FetchAsync();

        result.Cached.Should().BeFalse();
        handler.Requests.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task FetchAsync_WhenClockMovesBackwardDuringCooldown_BlocksNoLongerThanTheCooldown()
    {
        var calls = 0;
        var (client, handler, clock) = Create(_ => ++calls == 1 ? TooManyRequests(null) : Json(UsageJson));
        await client.FetchAsync();
        clock.Now = Now.AddHours(-2);
        var during = await client.FetchAsync();
        clock.Now = Now.AddHours(-2).AddMinutes(10).AddSeconds(1);

        var after = await client.FetchAsync();

        during.Status.Should().Be(NativeUsageStatus.RateLimited);
        during.RetryAfter.Should().BeOnOrBefore(Now.AddHours(-2).AddMinutes(10));
        after.Status.Should().Be(NativeUsageStatus.Ok);
        handler.Requests.Should().HaveCount(2);
    }

    // Review finding 15: overlapping refreshes share one call instead of spending the rate limit twice.
    [TestMethod]
    public async Task FetchAsync_WhenCalledConcurrentlyOnColdCache_MakesOnlyOneHttpRequest()
    {
        using var release = new ManualResetEventSlim(false);
        var (client, handler, _) = Create(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(30));
            return Json(UsageJson);
        });

        var first = Task.Run(() => client.FetchAsync());
        await WaitUntil(() => handler.Requests.Count == 1);
        var second = Task.Run(() => client.FetchAsync());
        await WaitUntil(() => handler.Requests.Count >= 2, TimeSpan.FromMilliseconds(750));
        release.Set();
        var results = await Task.WhenAll(first, second);

        handler.Requests.Should().ContainSingle();
        results.Should().OnlyContain(r => r.Status == NativeUsageStatus.Ok);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    private static HttpResponseMessage TooManyRequests(string? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter != null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private sealed class RecordingCooldownStore : IClaudeCooldownStore
    {
        public List<DateTimeOffset> Saved { get; } = [];
        public DateTimeOffset? Load() => null;
        public Task SaveAsync(DateTimeOffset blockedUntil, CancellationToken ct = default)
        {
            lock (Saved) Saved.Add(blockedUntil);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    [DataRow("max", "default_claude_max_20x", "Claude Max 20x")]
    [DataRow("max", "default_claude_max_5x", "Claude Max 5x")]
    [DataRow("pro", null, "Claude Pro")]
    [DataRow("team", "default_claude_max_20x", "Claude Team")]
    [DataRow(null, "default_claude_max_20x", "Claude Max 20x")]
    [DataRow(null, null, null)]
    public void LoginMethod_WhenGivenPlanFields_MatchesUpstreamLabels(string? subscription, string? tier, string? expected) =>
        ClaudeNativeClient.LoginMethod(subscription, tier).Should().Be(expected);
}
