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

    private (CodexNativeClient Client, RecordingHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new RecordingHandler(respond ?? (_ => Json(UsageJson)));
        return (new CodexNativeClient(handler.Client(), new ListLogger<CodexNativeClient>(), new CodexNativeOptions { AuthPath = _path }, new TestClock(Now)), handler);
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
