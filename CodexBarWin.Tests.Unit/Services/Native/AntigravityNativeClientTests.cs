using System.Net;
using AwesomeAssertions;
using CodexBarWin.Services.Native;
using static CodexBarWin.Tests.Unit.Services.Native.NativeTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native;

[TestClass]
public class AntigravityNativeClientTests
{
    private const string SyntheticAccess = "synthetic-antigravity-access-token";
    private const string LoadCodeAssistJson = """
        {"currentTier": {"id": "free-tier", "name": "Antigravity Free"}, "allowedTiers": [{"id": "free-tier", "isDefault": true}],
         "cloudaicompanionProject": "synthetic-project-123", "gcpManaged": false}
        """;

    // Recorded shape of retrieveUserQuotaSummary (as upstream's fixtures and the CLI report), synthetic values.
    private const string SummaryJson = """
        {
          "groups": [
            {"displayName": "Claude and GPT models", "buckets": [
              {"bucketId": "3p-weekly", "window": "weekly", "remainingFraction": 0.75, "resetTime": "2026-11-30T00:00:00Z",
               "description": "Shared weekly quota for Claude and GPT models in Antigravity, resets every Sunday."},
              {"bucketId": "3p-5h", "window": "5h", "remainingFraction": 0.9, "resetTime": "2026-11-25T09:00:00Z"}
            ]},
            {"displayName": "Gemini models", "buckets": [
              {"bucketId": "gemini-weekly", "window": "weekly", "remainingFraction": 0.61, "resetTime": "2026-11-29T06:00:00Z"},
              {"bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.2, "resetTime": "2026-11-25T09:38:00.123456789Z"}
            ]}
          ]
        }
        """;

    private static string Blob(DateTimeOffset expiry) => $$"""
        {"token": {"access_token": "{{SyntheticAccess}}", "token_type": "Bearer", "refresh_token": "synthetic-refresh",
                   "expiry": "{{expiry.ToOffset(TimeSpan.FromHours(11)):yyyy-MM-ddTHH:mm:ss}}.123456789+11:00", "expires_in": 3599},
         "auth_method": "consumer", "id_token_email": "someone@gmail.com", "writer_id": "synthetic-writer"}
        """;

    private static (AntigravityNativeClient Client, RecordingHandler Handler) Create(string? blob, Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        AntigravityNativeOptions? options = null, AntigravityTokenRefresher? refresher = null)
    {
        var handler = new RecordingHandler(respond ?? Default);
        var client = new AntigravityNativeClient(handler.Client(), new ListLogger<AntigravityNativeClient>(), new FakeCredentialSource(blob),
            options, new TestClock(Now), refresher);
        return (client, handler);
    }

    private static HttpResponseMessage Default(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/v1internal:loadCodeAssist" => Json(LoadCodeAssistJson),
        "/v1internal:retrieveUserQuotaSummary" => Json(SummaryJson),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
    };

    [TestMethod]
    public async Task FetchAsync_WhenSummaryReturned_ReshapesLikeTheShim()
    {
        var (client, handler) = Create(Blob(Now.AddMinutes(30)));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Ok);
        result.Provider.Should().Be("gemini");
        result.NetworkCalls.Should().Be(2);
        result.Primary!.UsedPercent.Should().BeApproximately(80, 1e-9);
        result.Primary.WindowMinutes.Should().Be(300);
        result.Primary.ResetsAt.Should().Be(new DateTimeOffset(2026, 11, 25, 9, 38, 0, 123, TimeSpan.Zero).AddTicks(4567));
        result.Primary.ResetDescription.Should().Be("Resets in 3h 38m");
        result.Secondary!.UsedPercent.Should().BeApproximately(39, 1e-9);
        result.Secondary.WindowMinutes.Should().Be(10080);
        result.Secondary.ResetDescription.Should().Be("Resets in 4d 0h");
        result.ExtraWindows.Select(e => (e.Id, e.Title)).Should().Equal(
            ("antigravity-quota-summary-3p-5h", "Claude and GPT 5-hour"),
            ("antigravity-quota-summary-3p-weekly", "Claude and GPT weekly"));
        result.ExtraWindows[0].Window.UsedPercent.Should().BeApproximately(10, 1e-9);
        result.ExtraWindows[1].Window.ResetDescription.Should().StartWith("Shared weekly quota");
        result.LoginMethod.Should().BeNull("upstream drops the Free label when a 5-hour window is reported");
        handler.Bodies[1].Should().Be("""{"project":"synthetic-project-123"}""");
    }

    [TestMethod]
    public async Task FetchAsync_WhenCalled_SendsPinnedClientIdentity()
    {
        var (client, handler) = Create(Blob(Now.AddMinutes(30)));

        await client.FetchAsync();

        handler.Requests.Select(r => r.RequestUri!.ToString()).Should().Equal(
            "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
            "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary");
        handler.Requests.Should().AllSatisfy(r =>
        {
            r.Method.Should().Be(HttpMethod.Post);
            r.Headers.UserAgent.ToString().Should().Be("antigravity/hub/2.9.1 linux/amd64");
            r.Headers.Authorization!.Parameter.Should().Be(SyntheticAccess);
        });
        handler.Bodies[0].Should().Be("""{"metadata":{"ideType":"ANTIGRAVITY","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}""");
    }

    [TestMethod]
    public async Task FetchAsync_WhenTokenExpiredAndRefreshOff_ReturnsSignInExpiredWithoutCalling()
    {
        var (client, handler) = Create(Blob(Now.AddMinutes(-6)));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.ExpiredFor!.Value.TotalMinutes.Should().BeApproximately(6, 0.01);
        // The blob's expiry carries a .123 s fraction, so 5 min 59.9 s have passed: reported as "5 minutes".
        result.Message.Should().Contain("5 minutes ago").And.NotContain(SyntheticAccess);
        result.NetworkCalls.Should().Be(0);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenTokenWithinSafetyWindow_TreatsAsExpired()
    {
        var (client, handler) = Create(Blob(Now.AddSeconds(30)));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.Message.Should().Contain("under a minute");
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenNoCredential_ReturnsSignInMissing()
    {
        var (client, handler) = Create(null);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInMissing);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenRefreshOnAndExpired_RefreshesInMemoryOnce()
    {
        var refreshCalls = 0;
        var (client, handler) = Create(Blob(Now.AddMinutes(-6)), request =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                refreshCalls++;
                return Json("""{"access_token": "synthetic-refreshed-token", "expires_in": 3599, "token_type": "Bearer"}""");
            }
            return Default(request);
        }, new AntigravityNativeOptions { AllowInMemoryRefresh = true });
        // A refresher whose client comes from a synthetic resolver, never from the installed binary.
        var refresher = new AntigravityTokenRefresher(handler.Client(), new ListLogger<AntigravityTokenRefresher>(),
            () => new AntigravityOAuthClient("123-synthetic.apps.googleusercontent.com", "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0"), new TestClock(Now));
        client = new AntigravityNativeClient(handler.Client(), new ListLogger<AntigravityNativeClient>(),
            new FakeCredentialSource(Blob(Now.AddMinutes(-6))), new AntigravityNativeOptions { AllowInMemoryRefresh = true }, new TestClock(Now), refresher);

        var first = await client.FetchAsync();
        var second = await client.FetchAsync();

        first.Status.Should().Be(NativeUsageStatus.Ok);
        first.NetworkCalls.Should().Be(3);
        second.NetworkCalls.Should().Be(2, "the refreshed token is reused from memory");
        refreshCalls.Should().Be(1);
        handler.Bodies[0].Should().Contain("grant_type=refresh_token").And.Contain("client_id=123-synthetic");
        handler.Requests.Where(r => r.RequestUri!.Host != "oauth2.googleapis.com")
            .Should().AllSatisfy(r => r.Headers.Authorization!.Parameter.Should().Be("synthetic-refreshed-token"));
    }

    [TestMethod]
    public async Task FetchAsync_WhenRefreshRefused_ReturnsSignInExpired()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Host == "oauth2.googleapis.com"
            ? Json("""{"error": "invalid_grant"}""", HttpStatusCode.BadRequest) : Default(request));
        var refresher = new AntigravityTokenRefresher(handler.Client(), new ListLogger<AntigravityTokenRefresher>(),
            () => new AntigravityOAuthClient("123-synthetic.apps.googleusercontent.com", "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0"), new TestClock(Now));
        var client = new AntigravityNativeClient(handler.Client(), new ListLogger<AntigravityNativeClient>(),
            new FakeCredentialSource(Blob(Now.AddMinutes(-6))), new AntigravityNativeOptions { AllowInMemoryRefresh = true }, new TestClock(Now), refresher);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.NetworkCalls.Should().Be(1);
        handler.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public async Task FetchAsync_WhenNoProject_DoesNotOnboard()
    {
        var (client, handler) = Create(Blob(Now.AddMinutes(30)), request => request.RequestUri!.AbsolutePath switch
        {
            "/v1internal:loadCodeAssist" => Json("""{"currentTier": {"id": "free-tier"}, "allowedTiers": [{"id": "free-tier", "isDefault": true}]}"""),
            "/v1internal:retrieveUserQuotaSummary" => Json(SummaryJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Ok);
        handler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.Contains("onboardUser"));
        handler.Bodies[1].Should().Be("{}");
    }

    [TestMethod]
    public async Task FetchAsync_WhenClientRejectedForConsumerTier_ReturnsUnauthorized()
    {
        var (client, _) = Create(Blob(Now.AddMinutes(30)), _ =>
            Json("""{"ineligibleTiers": [{"reasonCode": "GOOGLE_TOS_NOT_SUPPORTED_BY_CLIENT"}]}"""));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Unauthorized);
        result.NetworkCalls.Should().Be(1);
    }

    [TestMethod]
    public async Task FetchAsync_WhenSummaryFails_FallsBackToAvailableModels()
    {
        var (client, handler) = Create(Blob(Now.AddMinutes(30)), request => request.RequestUri!.AbsolutePath switch
        {
            "/v1internal:loadCodeAssist" => Json(LoadCodeAssistJson),
            "/v1internal:retrieveUserQuotaSummary" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "/v1internal:fetchAvailableModels" => Json("""
                {"models": {
                  "gemini-3-pro-high": {"displayName": "Gemini 3 Pro", "quotaInfo": {"remainingFraction": 0.4, "resetTime": "2026-11-25T09:00:00Z"}},
                  "gemini-3-flash-lite": {"quotaInfo": {"remainingFraction": 0.1}},
                  "claude-sonnet-4-6": {"quotaInfo": {"remainingFraction": 0.8, "resetTime": "2026-11-25T10:00:00Z"}}
                }}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Ok);
        result.NetworkCalls.Should().Be(3);
        result.Primary!.UsedPercent.Should().BeApproximately(60, 1e-9);
        result.Secondary!.UsedPercent.Should().BeApproximately(20, 1e-9);
        handler.Requests.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task FetchAsync_WhenLoadCodeAssistUnauthorized_ReturnsUnauthorized()
    {
        var (client, _) = Create(Blob(Now.AddMinutes(30)), _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Unauthorized);
        result.HttpStatus.Should().Be(401);
    }

    [TestMethod]
    public void MapQuotaSummary_WhenGeminiWindowsMissing_UsesUpstreamRepresentatives()
    {
        var summary = System.Text.Json.JsonSerializer.Deserialize("""
            {"response": {"groups": [
              {"displayName": "Claude and GPT models", "buckets": [{"bucketId": "3p-5h", "window": "5h", "remainingFraction": 0.5}]},
              {"displayName": "Gemini 4 Argon", "buckets": [{"bucketId": "argon-daily", "displayName": "Daily", "remainingFraction": 0.9},
                                                           {"bucketId": "argon-off", "displayName": "Off", "disabled": true, "remainingFraction": 0.1}]}
            ]}}
            """, NativeJsonContext.Default.QuotaSummaryResponse)!;

        var result = AntigravityNativeClient.MapQuotaSummary(summary, "Paid", Now)!;

        result.Primary!.UsedPercent.Should().BeApproximately(10, 1e-9, "the versioned Gemini group is Gemini family by title");
        result.Primary.WindowMinutes.Should().Be(0);
        result.Secondary!.UsedPercent.Should().BeApproximately(50, 1e-9);
        result.ExtraWindows.Select(e => e.Title).Should().Equal("Claude and GPT 5-hour", "Gemini 4 Argon Daily");
        result.LoginMethod.Should().Be("Paid");
    }

    [TestMethod]
    public void MapQuotaSummary_WhenCodeIsError_IsIgnoredByClient()
    {
        var summary = System.Text.Json.JsonSerializer.Deserialize("""{"code": 7, "groups": []}""", NativeJsonContext.Default.QuotaSummaryResponse)!;

        summary.CodeIsError.Should().BeTrue();
        AntigravityNativeClient.MapQuotaSummary(summary, null, Now).Should().BeNull();
    }

    [TestMethod]
    [DataRow("""{"paidTier": {"id": "g1-pro-tier", "name": "Google AI Pro"}}""", "someone@gmail.com", "Google AI Pro")]
    [DataRow("""{"paidTier": {"id": "g1-pro-tier"}}""", "someone@gmail.com", "Paid")]
    [DataRow("""{"currentTier": {"id": "standard-tier"}}""", "someone@gmail.com", "Paid")]
    [DataRow("""{"currentTier": {"id": "free-tier"}}""", "someone@gmail.com", "Free")]
    [DataRow("""{"currentTier": {"id": "free-tier"}}""", "someone@example.org", "Workspace")]
    [DataRow("""{"currentTier": {"id": "legacy-tier"}}""", null, "Legacy")]
    public void ResolvePlan_WhenTierGiven_MatchesUpstream(string json, string? email, string expected)
    {
        var response = System.Text.Json.JsonSerializer.Deserialize(json, NativeJsonContext.Default.CodeAssistResponse)!;

        AntigravityNativeClient.ResolvePlan(response, email).Should().Be(expected);
    }
}
