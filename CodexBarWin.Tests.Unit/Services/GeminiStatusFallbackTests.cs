using System.Net;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using Moq;
using Moq.Protected;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class GeminiStatusFallbackTests
{
    private sealed class TestClock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private static readonly DateTimeOffset InitialTime = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private TestClock _clock = null!;
    private Mock<HttpMessageHandler> _mockHandler = null!;

    [TestInitialize]
    public void Setup()
    {
        _clock = new TestClock(InitialTime);
        _mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
    }

    private GeminiStatusFallback CreateFallback()
    {
        var client = new HttpClient(_mockHandler.Object);
        return new GeminiStatusFallback(client, _clock);
    }

    private void SetupHttpResponse(HttpStatusCode code, string content)
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() => Task.FromResult(new HttpResponseMessage
            {
                StatusCode = code,
                Content = new StringContent(content)
            }));
    }

    [TestMethod]
    public async Task GetStatusAsync_NoIncidents_ReturnsNoneAllSystemsOperational()
    {
        SetupHttpResponse(HttpStatusCode.OK, "[]");
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.None);
        status.Description.Should().Be("All systems operational");
        status.Url.Should().Be("https://www.google.com/appsstatus/dashboard/products/npdyhgECDJ6tB66MxXyo/history");
        status.UpdatedAt.Should().Be(InitialTime.UtcDateTime);
    }

    [TestMethod]
    public async Task GetStatusAsync_ActiveOutage_ReturnsCriticalIndicator()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "service_name": "Gemini",
                "status_impact": "SERVICE_OUTAGE",
                "severity": "high",
                "external_desc": "Service outage affecting Gemini",
                "begin": "2026-10-10T10:00:00Z",
                "end": null,
                "modified": "2026-10-10T11:30:00Z"
              }
            ]
            """;
        SetupHttpResponse(HttpStatusCode.OK, json);
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Critical);
        status.Description.Should().Be("Service outage affecting Gemini");
        status.Url.Should().Be("https://www.google.com/appsstatus/dashboard/products/npdyhgECDJ6tB66MxXyo/history");
        status.UpdatedAt.Should().Be(DateTime.Parse("2026-10-10T11:30:00Z").ToUniversalTime());
    }

    [TestMethod]
    public async Task GetStatusAsync_ActiveDisruption_ReturnsMinorIndicator()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "service_name": "Gemini",
                "status_impact": "SERVICE_DISRUPTION",
                "severity": "medium",
                "external_desc": "Elevated error rates with Gemini models",
                "begin": "2026-10-10T10:00:00Z",
                "end": null,
                "modified": "2026-10-10T11:00:00Z"
              }
            ]
            """;
        SetupHttpResponse(HttpStatusCode.OK, json);
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        status.Description.Should().Be("Elevated error rates with Gemini models");
        status.Url.Should().Be("https://www.google.com/appsstatus/dashboard/products/npdyhgECDJ6tB66MxXyo/history");
    }

    [TestMethod]
    public async Task GetStatusAsync_ActiveDisruptionWithHighSeverity_ReturnsMajorIndicator()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "service_name": "Gemini",
                "status_impact": "SERVICE_DISRUPTION",
                "severity": "high",
                "external_desc": "Major disruption on Gemini service",
                "begin": "2026-10-10T10:00:00Z",
                "end": null
              }
            ]
            """;
        SetupHttpResponse(HttpStatusCode.OK, json);
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Major);
        status.Description.Should().Be("Major disruption on Gemini service");
    }

    [TestMethod]
    public async Task GetStatusAsync_ScheduledMaintenance_ReturnsMaintenanceIndicator()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "service_name": "Gemini",
                "status_impact": "SCHEDULED_MAINTENANCE",
                "severity": "low",
                "external_desc": "Scheduled maintenance on Gemini API",
                "begin": "2026-10-10T10:00:00Z",
                "end": null
              }
            ]
            """;
        SetupHttpResponse(HttpStatusCode.OK, json);
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Maintenance);
        status.Description.Should().Be("Scheduled maintenance on Gemini API");
    }

    [TestMethod]
    public async Task GetStatusAsync_EndedIncident_ReturnsNoneAllSystemsOperational()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "service_name": "Gemini",
                "status_impact": "SERVICE_OUTAGE",
                "severity": "high",
                "external_desc": "Resolved outage",
                "begin": "2026-10-09T08:00:00Z",
                "end": "2026-10-09T10:00:00Z",
                "modified": "2026-10-09T10:15:00Z"
              }
            ]
            """;
        SetupHttpResponse(HttpStatusCode.OK, json);
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.None);
        status.Description.Should().Be("All systems operational");
        status.Url.Should().Be("https://www.google.com/appsstatus/dashboard/products/npdyhgECDJ6tB66MxXyo/history");
    }

    [TestMethod]
    public async Task GetStatusAsync_MalformedJson_ReturnsNullWithoutThrowing()
    {
        SetupHttpResponse(HttpStatusCode.OK, "<html><body>Internal Server Error</body></html>");
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().BeNull();
    }

    [TestMethod]
    public async Task GetStatusAsync_HttpFailure_ReturnsNullWithoutThrowing()
    {
        SetupHttpResponse(HttpStatusCode.InternalServerError, "Server Error");
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().BeNull();
    }

    [TestMethod]
    public async Task GetStatusAsync_HttpException_ReturnsNullWithoutThrowing()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().BeNull();
    }

    [TestMethod]
    public async Task GetStatusAsync_CacheTtl_CachesForFiveMinutesThenRefetches()
    {
        var json = "[]";
        SetupHttpResponse(HttpStatusCode.OK, json);
        using var fallback = CreateFallback();

        // 1. Initial fetch at T0
        var status1 = await fallback.GetStatusAsync();
        status1.Should().NotBeNull();

        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());

        // 2. Fetch at T0 + 4 minutes (within 5-min TTL) -> cached
        _clock.Current = InitialTime.AddMinutes(4);
        var status2 = await fallback.GetStatusAsync();
        status2.Should().BeSameAs(status1);

        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());

        // 3. Fetch at T0 + 6 minutes (> 5-min TTL) -> refetches HTTP
        _clock.Current = InitialTime.AddMinutes(6);
        var status3 = await fallback.GetStatusAsync();
        status3.Should().NotBeNull();

        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [TestMethod]
    public async Task GetStatusAsync_WhenMatchingViaAffectedProducts_IdentifiesGeminiIncident()
    {
        var json = """
            [
              {
                "service_key": "other_service_key",
                "service_name": "Google Suite",
                "affected_products": [
                  { "id": "unrelated", "title": "Docs" },
                  { "id": "npdyhgECDJ6tB66MxXyo", "title": "Gemini" }
                ],
                "status_impact": "SERVICE_DISRUPTION",
                "severity": "medium",
                "external_desc": "Gemini disruption via affected products",
                "begin": "2026-10-10T10:00:00Z",
                "end": null
              }
            ]
            """;
        SetupHttpResponse(HttpStatusCode.OK, json);
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        status.Description.Should().Be("Gemini disruption via affected products");
    }

    [TestMethod]
    public async Task GetStatusAsync_WhenUnrelatedProductIncident_IgnoresAndReturnsNone()
    {
        var json = """
            [
              {
                "service_key": "prPt2Yra2CbGsbEm9cpC",
                "service_name": "Gmail",
                "status_impact": "SERVICE_OUTAGE",
                "severity": "high",
                "external_desc": "Gmail is down",
                "begin": "2026-10-10T10:00:00Z",
                "end": null
              }
            ]
            """;
        SetupHttpResponse(HttpStatusCode.OK, json);
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.None);
        status.Description.Should().Be("All systems operational");
    }

    [TestMethod]
    public async Task GetStatusAsync_CleansMarkdownSummaryInExternalDescription()
    {
        var json = """
            [
              {
                "service_key": "npdyhgECDJ6tB66MxXyo",
                "status_impact": "SERVICE_DISRUPTION",
                "severity": "medium",
                "external_desc": "**Summary:**\nElevated error rates on Gemini web.\n**Description:**\nDetails follow...",
                "begin": "2026-10-10T10:00:00Z",
                "end": null
              }
            ]
            """;
        SetupHttpResponse(HttpStatusCode.OK, json);
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Description.Should().Be("Elevated error rates on Gemini web.");
    }

    [TestMethod]
    public async Task GetStatusAsync_SendsConfiguredUserAgent()
    {
        HttpRequestMessage? capturedRequest = null;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("[]")
            });
        using var fallback = CreateFallback();

        await fallback.GetStatusAsync();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.UserAgent.ToString().Should().Contain("CodexBarWin");
    }

    [TestMethod]
    public async Task GetStatusAsync_EmptyFeed_ReturnsOperationalStatus()
    {
        SetupHttpResponse(HttpStatusCode.OK, "[]");
        using var fallback = CreateFallback();

        var status = await fallback.GetStatusAsync();

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.None);
        status.Description.Should().Be("All systems operational");
    }

    [TestMethod]
    public async Task GetStatusAsync_FailedRequest_NegativeCachedFor60Seconds()
    {
        var requests = 0;
        var statusCode = HttpStatusCode.InternalServerError;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback(() => requests++)
            .Returns(() => Task.FromResult(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(statusCode == HttpStatusCode.OK ? "[]" : "Error")
            }));

        using var fallback = CreateFallback();

        // Call 1: HTTP 500 failure
        var status1 = await fallback.GetStatusAsync();
        status1.Should().BeNull();
        requests.Should().Be(1);

        // Call 2 (30s later): negative cache hit
        _clock.Current += TimeSpan.FromSeconds(30);
        statusCode = HttpStatusCode.OK;
        var status2 = await fallback.GetStatusAsync();
        status2.Should().BeNull();
        requests.Should().Be(1);

        // Call 3 (61s after call 1): negative cache expired, retries HTTP
        _clock.Current += TimeSpan.FromSeconds(31);
        var status3 = await fallback.GetStatusAsync();
        status3.Should().NotBeNull();
        status3!.Indicator.Should().Be(ProviderStatusIndicator.None);
        requests.Should().Be(2);
    }

    [TestMethod]
    public async Task GetStatusAsync_CancelledToken_DoesNotNegativeCache()
    {
        using var fallback = CreateFallback();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var status = await fallback.GetStatusAsync(cts.Token);
        status.Should().BeNull();

        // Immediate subsequent call with valid token should not be blocked by negative cache
        SetupHttpResponse(HttpStatusCode.OK, "[]");
        var statusAfter = await fallback.GetStatusAsync();
        statusAfter.Should().NotBeNull();
    }
}
