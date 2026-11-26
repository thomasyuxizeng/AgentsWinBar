using System.Net;
using AwesomeAssertions;
using AgentsWinBar.Services.Native;
using static AgentsWinBar.Tests.Unit.Services.Native.NativeTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native;

[TestClass]
public class CodexNativeClientTests
{
    // Recorded shape of wham/usage with synthetic values (primary/secondary deliberately swapped to test normalizing).
    private const string UsageJson = """
        {
          "plan_type": "team",
          "rate_limit": {
            "allowed": true,
            "limit_reached": false,
            "primary_window": {"used_percent": 9, "limit_window_seconds": 604800, "reset_after_seconds": 400000, "reset_at": 1764400000},
            "secondary_window": {"used_percent": 23, "limit_window_seconds": 18000, "reset_after_seconds": 9000, "reset_at": 1764060000}
          },
          "credits": {"has_credits": false, "unlimited": false, "balance": "0"},
          "additional_rate_limits": [
            {"limit_name": "GPT-5.3-Codex-Spark", "metered_feature": "codex_bengalfox",
             "rate_limit": {"primary_window": {"used_percent": 2, "limit_window_seconds": 18000, "reset_at": 1764060000},
                            "secondary_window": {"used_percent": 1, "limit_window_seconds": 604800, "reset_at": 1764400000}}},
            {"limit_name": "Research preview", "metered_feature": "research_preview",
             "rate_limit": {"primary_window": {"used_percent": 40, "limit_window_seconds": 86400, "reset_at": 1764100000}}}
          ]
        }
        """;

    private string _path = null!;

    [TestInitialize]
    public void Setup() => _path = TempFile(Auth(Now.AddDays(7)));

    [TestCleanup]
    public void Cleanup() => DeleteTemp(_path);

    private static string Auth(DateTimeOffset expiry, bool withAccountId = true) => $$"""
        {
          "auth_mode": "chatgpt",
          "OPENAI_API_KEY": null,
          "tokens": {
            "id_token": "{{Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"synthetic-account-from-id"}}""")}}",
            "access_token": "{{Jwt($$"""{"exp":{{expiry.ToUnixTimeSeconds()}},"sub":"synthetic"}""")}}",
            "refresh_token": "synthetic-refresh",
            {{(withAccountId ? "\"account_id\": \"synthetic-account\"" : "\"unused\": 0")}}
          },
          "last_refresh": "2026-11-23T01:02:03.456789Z"
        }
        """;

    private (CodexNativeClient Client, RecordingHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, TestClock? clock = null)
    {
        var handler = new RecordingHandler(respond ?? (_ => Json(UsageJson)));
        return (new CodexNativeClient(handler.Client(), new ListLogger<CodexNativeClient>(), new CodexNativeOptions { AuthPath = _path }, clock ?? new TestClock(Now)), handler);
    }

    /// <summary>wham/usage with a 5-hour window resetting in an hour and a weekly window that reset an hour ago.</summary>
    private static string UsageWithOneStaleWindow() => $$"""
        {"plan_type": "team", "rate_limit": {
          "primary_window": {"used_percent": 23, "limit_window_seconds": 18000, "reset_at": {{Now.AddHours(1).ToUnixTimeSeconds()}}},
          "secondary_window": {"used_percent": 61, "limit_window_seconds": 604800, "reset_at": {{Now.AddHours(-1).ToUnixTimeSeconds()}} }
        }
        }
        """;

    [TestMethod]
    public void ToString_WhenCredentialsHaveTokens_OmitsEveryTokenValue()
    {
        var credentials = new CodexCredentials("synthetic-codex-access-token", "synthetic-account-id", Now, Now.AddDays(-1));

        var text = credentials.ToString();

        text.Should().NotContain("synthetic-codex-access-token").And.NotContain("synthetic-account-id");
        text.Should().Contain("ExpiresAt");
    }

    [TestMethod]
    public void ResolveAuthPath_WhenCodexHomeSet_IgnoresIt()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(Path.GetTempPath(), "synthetic-codex-home"));

            new CodexNativeOptions().ResolveAuthPath().Should().Be(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json"));
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", previous); }
    }

    [TestMethod]
    public void ResolveAuthPath_WhenExplicitPathGiven_UsesIt()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(Path.GetTempPath(), "synthetic-codex-home"));
            var explicitPath = Path.Combine(Path.GetTempPath(), "synthetic-explicit", "auth.json");

            new CodexNativeOptions { AuthPath = explicitPath }.ResolveAuthPath().Should().Be(explicitPath);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", previous); }
    }

    [TestMethod]
    public async Task FetchAsync_WhenTokenExpiredAfterGoodReading_ServesLastGoodReadingAsCached()
    {
        var (client, handler) = Create();
        await client.FetchAsync();
        File.WriteAllText(_path, Auth(Now.AddHours(-3)));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.Cached.Should().BeTrue();
        result.CacheUpdatedAt.Should().Be(Now);
        result.ExpiredFor.Should().Be(TimeSpan.FromHours(3));
        result.Primary!.UsedPercent.Should().Be(0, "the recorded 5-hour window's reset time is long past");
        result.Secondary.Should().NotBeNull();
        result.NetworkCalls.Should().Be(0);
        handler.Requests.Should().ContainSingle();
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError, NativeUsageStatus.Failed)]
    [DataRow(HttpStatusCode.BadGateway, NativeUsageStatus.Failed)]
    [DataRow(HttpStatusCode.TooManyRequests, NativeUsageStatus.RateLimited)]
    public async Task FetchAsync_WhenTransientHttpFailureAfterGoodReading_ServesLastGoodReadingAsCached(HttpStatusCode status, NativeUsageStatus expected)
    {
        var failing = false;
        var (client, _) = Create(_ => failing ? new HttpResponseMessage(status) : Json(UsageWithOneStaleWindow()));
        await client.FetchAsync();

        failing = true;
        var result = await client.FetchAsync();

        result.Status.Should().Be(expected);
        result.HttpStatus.Should().Be((int)status);
        result.Cached.Should().BeTrue();
        result.CacheUpdatedAt.Should().Be(Now);
        result.Primary!.UsedPercent.Should().Be(23);
        result.Primary.ResetsAt.Should().Be(Now.AddHours(1));
    }

    [TestMethod]
    [DataRow(null, 600)]
    [DataRow("90", 90)]
    [DataRow("999999", 86400)]
    public async Task FetchAsync_WhenRateLimited_HoldsRequestsUntilRetryAfter(string? header, int seconds)
    {
        var clock = new TestClock(Now);
        var limited = false;
        var (client, handler) = Create(_ =>
        {
            if (!limited) return Json("""{"rate_limit":{"secondary_window":{"used_percent":61,"limit_window_seconds":604800}}}""");
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (header != null) response.Headers.TryAddWithoutValidation("Retry-After", header);
            return response;
        }, clock);
        var good = await client.FetchAsync();
        limited = true;
        client.RateLimitedUntil.Should().BeNull();
        var rateLimited = await client.FetchAsync();
        client.RateLimitedUntil.Should().Be(Now.AddSeconds(seconds));

        clock.Now = Now.AddSeconds(seconds - 1);
        limited = false;
        var held = await client.FetchAsync();

        held.NetworkCalls.Should().Be(0);
        handler.Requests.Should().HaveCount(2);
        held.Cached.Should().BeTrue();
        held.Secondary!.UsedPercent.Should().Be(good.Secondary!.UsedPercent);
        held.RetryAfter.Should().Be(Now.AddSeconds(seconds));
        rateLimited.RetryAfter.Should().Be(held.RetryAfter);
        clock.Now = Now.AddSeconds(seconds);
        (await client.FetchAsync()).Status.Should().Be(NativeUsageStatus.Ok);
        handler.Requests.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task FetchAsync_WhenSignInChangesDuringRateLimitHold_AsksAgainWithTheNewSignIn()
    {
        var clock = new TestClock(Now);
        var limited = true;
        var (client, handler) = Create(_ =>
        {
            if (!limited) return Json(UsageJson);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "86400");
            return response;
        }, clock);
        (await client.FetchAsync()).Status.Should().Be(NativeUsageStatus.RateLimited);
        limited = false;
        clock.Now = Now.AddMinutes(5);

        var sameSignIn = await client.FetchAsync();
        File.WriteAllText(_path, Auth(Now.AddDays(8))); // a new sign-in: another access token
        var newSignIn = await client.FetchAsync();

        sameSignIn.Status.Should().Be(NativeUsageStatus.RateLimited);
        sameSignIn.NetworkCalls.Should().Be(0);
        newSignIn.Status.Should().Be(NativeUsageStatus.Ok);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Headers.Authorization!.Parameter.Should().NotBe(handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [TestMethod]
    public async Task FetchAsync_WhenNetworkErrorAfterGoodReading_ServesLastGoodReadingAsCached()
    {
        var failing = false;
        var (client, _) = Create(_ => failing ? throw new HttpRequestException("synthetic network failure") : Json(UsageWithOneStaleWindow()));
        await client.FetchAsync();

        failing = true;
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        result.Cached.Should().BeTrue();
        result.Primary!.UsedPercent.Should().Be(23);
    }

    [TestMethod]
    public async Task FetchAsync_WhenTimeoutAfterGoodReading_ServesLastGoodReadingAsCached()
    {
        var failing = false;
        var (client, _) = Create(_ => failing ? throw new TaskCanceledException("synthetic timeout") : Json(UsageWithOneStaleWindow()));
        await client.FetchAsync();

        failing = true;
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        result.Message.Should().Contain("timed out");
        result.Cached.Should().BeTrue();
        result.Primary!.UsedPercent.Should().Be(23);
    }

    [TestMethod]
    public async Task FetchAsync_WhenUnauthorizedAfterGoodReading_DoesNotServeLastGoodReading()
    {
        var failing = false;
        var (client, _) = Create(_ => failing ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(UsageWithOneStaleWindow()));
        await client.FetchAsync();

        failing = true;
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Unauthorized);
        result.Cached.Should().BeFalse();
        result.HasUsage.Should().BeFalse();
    }

    // Review finding 1: an absurd Retry-After must not throw out of the client and lose the last good reading.
    [TestMethod]
    [DataRow("Infinity")]
    [DataRow("1e20")]
    [DataRow("999999999999")]
    [DataRow("Fri, 31 Dec 9999 23:59:59 GMT")]
    public async Task FetchAsync_When429HasAbsurdRetryAfter_ServesLastGoodWithBoundedRetryAfter(string retryAfter)
    {
        var failing = false;
        var (client, _) = Create(_ =>
        {
            if (!failing) return Json(UsageWithOneStaleWindow());
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        });
        await client.FetchAsync();

        failing = true;
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.RateLimited);
        result.Cached.Should().BeTrue();
        result.Primary!.UsedPercent.Should().Be(23);
        result.RetryAfter.Should().BeOnOrBefore(Now + NativeHttp.MaxRetryAfter);
    }

    // Review finding 20.3: an absurd window length cannot wrap into a negative number of minutes.
    // 128849018880 s is 2^31 minutes, which an unchecked cast turns into int.MinValue.
    [TestMethod]
    [DataRow(9223372036854775807L)]
    [DataRow(128849018880L)]
    public void Map_WhenLimitWindowSecondsExceedsIntMaxMinutes_ClampsInsteadOfOverflowing(long seconds)
    {
        var usage = System.Text.Json.JsonSerializer.Deserialize($$$"""
            {"rate_limit": {"primary_window": {"used_percent": 5, "limit_window_seconds": {{{seconds}}}}} }
            """, NativeJsonContext.Default.CodexUsageResponse)!;

        var result = CodexNativeClient.Map(usage, Now)!;

        (result.Primary ?? result.Secondary)!.WindowMinutes.Should().BePositive();
    }

    // Review finding 6: the response body is read only up to a fixed cap.
    [TestMethod]
    public async Task FetchAsync_WhenResponseExceedsSizeLimit_FailsWithoutReadingWholeBody()
    {
        var body = new PaddedJsonStream("{\"plan_type\": \"team\", \"rate_limit\": {\"primary_window\": {\"used_percent\": 23, " +
            "\"limit_window_seconds\": 18000, \"reset_at\": 1764060000}}, \"padding\": \"", HugePadding, "\"}");
        var (client, _) = Create(_ => Streamed(body));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        body.Served.Should().BeLessThanOrEqualTo(NativeHttp.MaxResponseBytes + 1);
    }

    // Review finding 7: a known field holding an object or array must not derail the reader.
    [TestMethod]
    public void ParseCredentials_WhenKnownFieldPrecedingTokenIsObjectOrArray_StillReadsAccessToken()
    {
        var access = Jwt("""{"exp":1764050000,"sub":"synthetic"}""");
        var json = $$$"""
            {"OPENAI_API_KEY": {"env": "OPENAI_API_KEY"}, "last_refresh": ["x"],
             "tokens": {"account_id": {"nested": 1}, "id_token": ["x"], "access_token": "{{{access}}}"}}
            """;

        var (kind, credentials) = CodexNativeClient.ParseCredentials(System.Text.Encoding.UTF8.GetBytes(json));

        kind.Should().Be(CodexCredentialKind.ChatGpt);
        credentials!.AccessToken.Should().Be(access);
        credentials.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1764050000));
    }

    [TestMethod]
    public async Task FetchAsync_WhenFailureBeforeAnyGoodReading_ReturnsFailureWithoutCachedRow()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        result.Cached.Should().BeFalse();
        result.HasUsage.Should().BeFalse();
    }

    [TestMethod]
    public async Task FetchAsync_WhenServedLastGoodHasPastReset_ReportsThatWindowAsUnused()
    {
        var failing = false;
        var (client, _) = Create(_ => failing ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Json(UsageWithOneStaleWindow()));
        var fresh = await client.FetchAsync();

        failing = true;
        var cached = await client.FetchAsync();

        fresh.Secondary!.UsedPercent.Should().Be(61, "a fresh reading is passed through untouched");
        cached.Cached.Should().BeTrue();
        cached.Secondary!.UsedPercent.Should().Be(0);
        cached.Secondary.ResetsAt.Should().BeNull();
        cached.Primary!.UsedPercent.Should().Be(23, "the 5-hour window has not reset yet");
        cached.Primary.ResetsAt.Should().Be(Now.AddHours(1));
    }

    [TestMethod]
    public async Task FetchAsync_WhenUsageReturned_NormalizesWindowsByLength()
    {
        var (client, _) = Create();

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Ok);
        result.Provider.Should().Be("codex");
        result.LoginMethod.Should().Be("team");
        result.Primary!.UsedPercent.Should().Be(23);
        result.Primary.WindowMinutes.Should().Be(300);
        result.Primary.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1764060000));
        result.Secondary!.UsedPercent.Should().Be(9);
        result.Secondary.WindowMinutes.Should().Be(10080);
        result.NetworkCalls.Should().Be(1);
    }

    [TestMethod]
    public async Task FetchAsync_WhenAdditionalLimitsReturned_MapsExtraWindowsLikeUpstream()
    {
        var (client, _) = Create();

        var result = await client.FetchAsync();

        result.ExtraWindows.Select(e => (e.Id, e.Title)).Should().Equal(
            ("codex-spark", "Codex Spark 5-hour"),
            ("codex-spark-weekly", "Codex Spark Weekly"),
            ("codex-research-preview", "Research preview"));
        result.ExtraWindows[2].Window.WindowMinutes.Should().Be(1440);
    }

    [TestMethod]
    public async Task FetchAsync_WhenCalled_SendsTokenAndAccountHeader()
    {
        var (client, handler) = Create();

        await client.FetchAsync();

        var request = handler.Requests.Single();
        request.RequestUri.Should().Be(new Uri("https://chatgpt.com/backend-api/wham/usage"));
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.GetValues("ChatGPT-Account-Id").Should().Equal("synthetic-account");
        request.Headers.UserAgent.ToString().Should().Be("CodexBar");
    }

    [TestMethod]
    public async Task FetchAsync_WhenAccountIdMissing_UsesIdTokenClaim()
    {
        File.WriteAllText(_path, Auth(Now.AddDays(7), withAccountId: false));
        var (client, handler) = Create();

        await client.FetchAsync();

        handler.Requests.Single().Headers.GetValues("ChatGPT-Account-Id").Should().Equal("synthetic-account-from-id");
    }

    [TestMethod]
    public async Task FetchAsync_WhenAccessTokenExpired_ReturnsSignInExpiredWithoutCalling()
    {
        File.WriteAllText(_path, Auth(Now.AddHours(-3)));
        var (client, handler) = Create();

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.ExpiredFor.Should().Be(TimeSpan.FromHours(3));
        result.Message.Should().Contain("3 hours ago");
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenAuthFileMissing_ReturnsSignInMissing()
    {
        DeleteTemp(_path);
        var (client, handler) = Create();

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInMissing);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenOnlyApiKey_ReturnsSignInMissingWithExplanation()
    {
        File.WriteAllText(_path, """{"OPENAI_API_KEY": "sk-synthetic", "tokens": null}""");
        var (client, handler) = Create();

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInMissing);
        result.Message.Should().Contain("API key").And.NotContain("sk-synthetic");
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, NativeUsageStatus.Unauthorized)]
    [DataRow(HttpStatusCode.TooManyRequests, NativeUsageStatus.RateLimited)]
    [DataRow(HttpStatusCode.BadGateway, NativeUsageStatus.Failed)]
    public async Task FetchAsync_WhenHttpError_MapsStatus(HttpStatusCode status, NativeUsageStatus expected)
    {
        var (client, _) = Create(_ => new HttpResponseMessage(status));

        var result = await client.FetchAsync();

        result.Status.Should().Be(expected);
        result.HttpStatus.Should().Be((int)status);
    }

    [TestMethod]
    public void Normalize_WhenOnlyWeeklyWindow_PutsItSecond()
    {
        var weekly = new NativeUsageWindow(5, 10080, null, null);

        var (primary, secondary) = CodexNativeClient.Normalize(weekly, null);

        primary.Should().BeNull();
        secondary.Should().Be(weekly);
    }
}
