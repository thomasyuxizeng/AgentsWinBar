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
        AntigravityNativeOptions? options = null, TimeProvider? clock = null)
    {
        var handler = new RecordingHandler(respond ?? Default);
        var client = new AntigravityNativeClient(handler.Client(), new ListLogger<AntigravityNativeClient>(), new FakeCredentialSource(blob),
            options, clock ?? new TestClock(Now));
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
    public async Task FetchAsync_WhenTokenExpired_ReturnsSignInExpiredWithoutCalling()
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
    public void Options_WhenInspected_HasNoRefreshSwitch() =>
        typeof(AntigravityNativeOptions).GetProperty("AllowInMemoryRefresh").Should().BeNull("the opt-in Google token refresh was removed");

    [TestMethod]
    public async Task FetchAsync_WhenTokenExpiredAfterGoodReading_ServesLastGoodReadingAsCached()
    {
        var clock = new TestClock(Now);
        var (client, handler) = Create(Blob(Now.AddMinutes(30)), clock: clock);
        await client.FetchAsync();

        clock.Now = Now.AddMinutes(45);
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.Cached.Should().BeTrue();
        result.CacheUpdatedAt.Should().Be(Now);
        result.Primary!.UsedPercent.Should().BeApproximately(80, 1e-9);
        result.ExpiredFor.Should().NotBeNull();
        result.NetworkCalls.Should().Be(0);
        handler.Requests.Should().HaveCount(2, "the expired read makes no call");
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError, NativeUsageStatus.Failed)]
    [DataRow(HttpStatusCode.BadGateway, NativeUsageStatus.Failed)]
    [DataRow(HttpStatusCode.TooManyRequests, NativeUsageStatus.RateLimited)]
    public async Task FetchAsync_WhenTransientHttpFailureAfterGoodReading_ServesLastGoodReadingAsCached(HttpStatusCode status, NativeUsageStatus expected)
    {
        var failing = false;
        var clock = new TestClock(Now);
        var (client, _) = Create(Blob(Now.AddHours(12)), request => failing ? new HttpResponseMessage(status) : Default(request), clock: clock);
        await client.FetchAsync();

        failing = true;
        clock.Now = Now.AddMinutes(1);
        var result = await client.FetchAsync();

        result.Status.Should().Be(expected);
        result.HttpStatus.Should().Be((int)status);
        result.Cached.Should().BeTrue();
        result.CacheUpdatedAt.Should().Be(Now);
        result.Primary!.UsedPercent.Should().BeApproximately(80, 1e-9);
        result.Secondary!.UsedPercent.Should().BeApproximately(39, 1e-9);
    }

    [TestMethod]
    public async Task FetchAsync_WhenNetworkErrorAfterGoodReading_ServesLastGoodReadingAsCached()
    {
        var failing = false;
        var (client, _) = Create(Blob(Now.AddHours(12)), request => failing ? throw new HttpRequestException("synthetic network failure") : Default(request));
        await client.FetchAsync();

        failing = true;
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        result.Cached.Should().BeTrue();
        result.Primary.Should().NotBeNull();
    }

    [TestMethod]
    public async Task FetchAsync_WhenTimeoutAfterGoodReading_ServesLastGoodReadingAsCached()
    {
        var failing = false;
        var (client, _) = Create(Blob(Now.AddHours(12)), request => failing ? throw new TaskCanceledException("synthetic timeout") : Default(request));
        await client.FetchAsync();

        failing = true;
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        result.Message.Should().Contain("timed out");
        result.Cached.Should().BeTrue();
        result.Primary.Should().NotBeNull();
    }

    [TestMethod]
    public async Task FetchAsync_WhenUnauthorizedAfterGoodReading_DoesNotServeLastGoodReading()
    {
        var failing = false;
        var (client, _) = Create(Blob(Now.AddHours(12)), request => failing ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Default(request));
        await client.FetchAsync();

        failing = true;
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Unauthorized);
        result.Cached.Should().BeFalse();
        result.HasUsage.Should().BeFalse();
    }

    // Review finding 6: the response body is read only up to a fixed cap.
    [TestMethod]
    public async Task FetchAsync_WhenResponseExceedsSizeLimit_FailsWithoutReadingWholeBody()
    {
        var body = new PaddedJsonStream("{\"currentTier\": {\"id\": \"free-tier\"}, \"cloudaicompanionProject\": \"synthetic-project-123\", \"padding\": \"",
            HugePadding, "\"}");
        var (client, _) = Create(Blob(Now.AddHours(12)), request =>
            request.RequestUri!.AbsolutePath == "/v1internal:loadCodeAssist" ? Streamed(body) : Default(request));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        body.Served.Should().BeLessThanOrEqualTo(NativeHttp.MaxResponseBytes + 1);
    }

    // Review finding 20.1: an unreadable (corrupt) credential is a read failure, not a sign-out.
    [TestMethod]
    public async Task FetchAsync_WhenCredentialBlobIsMalformedJson_ReturnsFailedAndPreservesLastGood()
    {
        var source = new SwitchableCredentialSource { Json = Blob(Now.AddHours(12)) };
        var handler = new RecordingHandler(Default);
        var client = new AntigravityNativeClient(handler.Client(), new ListLogger<AntigravityNativeClient>(), source, null, new TestClock(Now));
        await client.FetchAsync();

        source.Json = "{broken";
        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        result.Cached.Should().BeTrue();
        result.Primary.Should().NotBeNull();
    }

    // Review finding 20.6: one bucket id gives one extra window.
    [TestMethod]
    public void MapQuotaSummary_WhenDuplicateExtraBucketIdsReturned_DeduplicatesExtras()
    {
        var json = """
            {"groups": [
              {"displayName": "Claude and GPT models", "buckets": [
                {"bucketId": "3p-5h", "window": "5h", "remainingFraction": 0.9, "resetTime": "2026-11-25T09:00:00Z"},
                {"bucketId": "3p-5h", "window": "5h", "remainingFraction": 0.5, "resetTime": "2026-11-25T09:00:00Z"}
              ]},
              {"displayName": "Gemini models", "buckets": [
                {"bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.2, "resetTime": "2026-11-25T09:38:00Z"}
              ]}
            ]}
            """;
        var summary = System.Text.Json.JsonSerializer.Deserialize(json, NativeJsonContext.Default.QuotaSummaryResponse)!;

        var result = AntigravityNativeClient.MapQuotaSummary(summary, null, Now)!;

        result.ExtraWindows.Select(e => e.Id).Should().Equal("antigravity-quota-summary-3p-5h");
    }

    private sealed class SwitchableCredentialSource : IAntigravityCredentialSource
    {
        public string? Json { get; set; }
        public SensitiveBuffer? ReadBlob() => Json == null ? null : SensitiveBuffer.From(System.Text.Encoding.UTF8.GetBytes(Json));
    }

    [TestMethod]
    public async Task FetchAsync_WhenFailureBeforeAnyGoodReading_ReturnsFailureWithoutCachedRow()
    {
        var (client, _) = Create(Blob(Now.AddHours(12)), _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Failed);
        result.Cached.Should().BeFalse();
        result.HasUsage.Should().BeFalse();
    }

    [TestMethod]
    public async Task FetchAsync_WhenServedLastGoodHasPastReset_ReportsThatWindowAsUnused()
    {
        var failing = false;
        var clock = new TestClock(Now);
        var (client, _) = Create(Blob(Now.AddHours(12)), request => failing ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Default(request), clock: clock);
        await client.FetchAsync();

        // 10:00: the Gemini 5-hour window (09:38) and the Claude/GPT 5-hour window (09:00) have reset; the weekly ones have not.
        failing = true;
        clock.Now = Now.AddHours(4);
        var result = await client.FetchAsync();

        result.Cached.Should().BeTrue();
        result.Primary!.UsedPercent.Should().Be(0);
        result.Primary.ResetsAt.Should().BeNull();
        result.Secondary!.UsedPercent.Should().BeApproximately(39, 1e-9);
        result.Secondary.ResetsAt.Should().NotBeNull();
        result.ExtraWindows[0].Window.UsedPercent.Should().Be(0);
        result.ExtraWindows[0].Window.ResetsAt.Should().BeNull();
        result.ExtraWindows[1].Window.UsedPercent.Should().BeApproximately(25, 1e-9);
        result.ExtraWindows[1].Window.ResetsAt.Should().NotBeNull();
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
