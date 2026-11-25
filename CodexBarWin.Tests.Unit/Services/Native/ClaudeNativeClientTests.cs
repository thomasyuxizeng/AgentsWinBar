using System.Net;
using AwesomeAssertions;
using CodexBarWin.Services.Native;
using static CodexBarWin.Tests.Unit.Services.Native.NativeTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native;

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
    public void ParseCredentials_WhenOtherKeysPresent_ReadsOnlyClaudeObject()
    {
        var credentials = ClaudeNativeClient.ParseCredentials(System.Text.Encoding.UTF8.GetBytes(Credentials(Now)));

        credentials!.AccessToken.Should().Be(SyntheticToken);
        credentials.ExpiresAt.Should().Be(Now);
        credentials.SubscriptionType.Should().Be("team");
        credentials.RateLimitTier.Should().Be("default_claude_max_20x");
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
