using System.Net;
using AwesomeAssertions;
using CodexBarWin.Services.Native;
using static CodexBarWin.Tests.Unit.Services.Native.NativeTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native;

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
