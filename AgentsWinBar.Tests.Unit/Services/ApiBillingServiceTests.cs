using System.Net;
using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public partial class ApiBillingServiceTests
{
    // A fake marker, never a credential. No test calls the real API or reads an actual key.
    private const string Marker = "test-only-admin-marker";
    private const string Success = """{"data":[{"results":[{"amount":"9533.0","currency":"USD"}]}],"has_more":false}""";
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> BreakdownRespond { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Success) });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.OriginalString);
            if (!request.RequestUri.AbsolutePath.EndsWith("/cost_report", StringComparison.Ordinal))
                return BreakdownRespond(request, ct);
            Calls++; return Respond(request, ct);
        }
    }
    private sealed class CapturingLogger : ILogger<ApiBillingService>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, formatter(state, exception), exception));
    }
    private readonly AppSettings _values = new();
    private readonly Clock _clock = new();
    private readonly Handler _handler = new();
    private readonly CapturingLogger _logger = new();
    private readonly List<EnvironmentVariableTarget> _environmentReads = [];
    private HttpClient _http = null!;
    private ApiBillingService _service = null!;
    private string? _processKey = Marker;
    private string? _userKey;

    [TestInitialize]
    public void Initialize()
    {
        var settings = new Mock<ISettingsService>(); settings.SetupGet(s => s.Settings).Returns(_values);
        _http = new HttpClient(_handler);
        _service = new ApiBillingService(settings.Object, _logger, _http, _clock, (name, target) =>
        {
            name.Should().Be(ApiBillingService.AdminKeyEnvironmentVariable);
            _environmentReads.Add(target);
            return target == EnvironmentVariableTarget.Process ? _processKey : _userKey;
        });
    }

    [TestCleanup]
    public void Cleanup() { _service.Dispose(); _http.Dispose(); }

    private void Status(HttpStatusCode status) => _handler.Respond = (_, _) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("untrusted body " + Marker) });

    [TestMethod]
    public async Task GetAsync_NoEnvironmentKey_ReturnsNoKeyWithoutHttpOrWarnings()
    {
        _processKey = null;
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.NoKey); _handler.Calls.Should().Be(0);
        _environmentReads.Should().Equal(EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User);
        _logger.Entries.Should().OnlyContain(e => e.Level == LogLevel.Debug);
    }

    [TestMethod]
    public async Task GetAsync_Success_UsesCorrectHeadersUtcRangeAndDecimalDollars()
    {
        _handler.Respond = (request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.Headers.GetValues("x-api-key").Single().Should().Be(Marker);
            request.Headers.GetValues("anthropic-version").Single().Should().Be("2023-06-01");
            request.Headers.UserAgent.ToString().Should().StartWith("AgentsWinBar/");
            var uri = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            uri.Should().Contain("starting_at=2026-10-01T00:00:00Z");
            uri.Should().Contain("ending_at=2026-10-16T00:00:00Z");
            uri.Should().Contain("bucket_width=1d&limit=31");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Success) });
        };
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Ok); result.SpentUsd.Should().Be(95.33m);
        result.FetchedAtUtc.Should().Be(_clock.Now.UtcDateTime); _handler.Calls.Should().Be(1);
        _environmentReads.Should().Equal(EnvironmentVariableTarget.Process);
    }

    [TestMethod]
    public async Task GetAsync_UserKeyFallback_ReadsUserScopeAtFetchTime()
    {
        _processKey = " "; _userKey = Marker;
        (await _service.GetAsync(false, default)).State.Should().Be(ApiCreditsState.Ok);
        _environmentReads.Should().Equal(EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User);
        _userKey = null;
        (await _service.GetAsync(false, default)).State.Should().Be(ApiCreditsState.NoKey);
        _handler.Calls.Should().Be(1);
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    public async Task GetAsync_RejectedKey_ReturnsStatusWithoutResponseDetails(int status)
    {
        Status((HttpStatusCode)status);
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Rejected); result.Message.Should().Contain(status.ToString());
        result.Message.Should().NotContain(Marker);
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
        _logger.Entries.Should().OnlyContain(e => !e.Text.Contains(Marker) && e.Exception == null);
    }

    [TestMethod]
    [DataRow(500)]
    [DataRow(503)]
    [DataRow(429)]
    public async Task GetAsync_HttpFailureWithoutGoodFetch_ReturnsUnavailable(int status)
    {
        Status((HttpStatusCode)status);
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Unavailable); result.FetchedAtUtc.Should().BeNull();
        result.Message.Should().NotContain(Marker);
    }

    [TestMethod]
    [DataRow(500)]
    [DataRow(429)]
    public async Task GetAsync_HttpFailureAfterGoodFetch_ReturnsStaleLastKnownSpend(int status)
    {
        var first = await _service.GetAsync(false, default);
        _clock.Now = _clock.Now.AddMinutes(6); Status((HttpStatusCode)status);
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Ok); result.IsStale.Should().BeTrue();
        result.SpentUsd.Should().Be(first.SpentUsd); result.FetchedAtUtc.Should().Be(first.FetchedAtUtc);
        (await _service.GetAsync(false, default)).IsStale.Should().BeTrue();
        _handler.Calls.Should().Be(2);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task GetAsync_TimeoutOrNetworkFailure_PreservesGoodValueAndNeverLogsException(bool previousGood, bool timeout)
    {
        ApiCreditsInfo? previous = previousGood ? await _service.GetAsync(false, default) : null;
        _handler.Respond = (_, _) => timeout
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout " + Marker))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("headers contain " + Marker));
        var result = await _service.GetAsync(true, default);
        result.State.Should().Be(previousGood ? ApiCreditsState.Ok : ApiCreditsState.Unavailable);
        result.IsStale.Should().Be(previousGood);
        if (previousGood) result.FetchedAtUtc.Should().Be(previous!.FetchedAtUtc);
        _logger.Entries.Should().OnlyContain(e => !e.Text.Contains(Marker) && e.Exception == null);
        (result.Message ?? "").Should().NotContain(Marker);
    }

    [TestMethod]
    public async Task GetAsync_WithinFiveMinutes_ThrottlesButForceAndExpiryBypass()
    {
        await _service.GetAsync(false, default);
        _clock.Now = _clock.Now.AddMinutes(4).AddSeconds(59);
        await _service.GetAsync(false, default); _handler.Calls.Should().Be(1);
        await _service.GetAsync(true, default); _handler.Calls.Should().Be(2);
        _clock.Now = _clock.Now.AddMinutes(5);
        await _service.GetAsync(false, default); _handler.Calls.Should().Be(3);
    }

    [TestMethod]
    public async Task GetAsync_MultiplePages_SumsAllBucketsAndEscapesCursor()
    {
        _handler.Respond = (request, _) =>
        {
            var json = _handler.Calls == 1
                ? """{"data":[{"results":[]},{"results":[{"amount":"9500.0","currency":"USD"},{"amount":"1000","currency":"EUR"}]}],"has_more":true,"next_page":"a+b&c"}"""
                : """{"data":[{"results":[{"amount":"33.0","currency":"USD"}]}],"has_more":false}""";
            if (_handler.Calls == 2) request.RequestUri!.OriginalString.Should().Contain("page=a%2Bb%26c");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        };
        (await _service.GetAsync(false, default)).SpentUsd.Should().Be(95.33m);
        _handler.Calls.Should().Be(2);
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Text.Contains("non-USD"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetAsync_IncompletePagination_DoesNotPublishPartialTotals(bool cap)
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[],\"has_more\":true,\"next_page\":"
                + (cap ? "\"" + _handler.Calls + "\"" : "null") + "}")
        });
        (await _service.GetAsync(false, default)).State.Should().Be(ApiCreditsState.Unavailable);
        _handler.Calls.Should().Be(cap ? 5 : 1);
    }

    [TestMethod]
    public async Task GetAsync_RepeatedPaginationCursor_StopsAndReturnsUnavailable()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"data":[],"has_more":true,"next_page":"same"}""") });
        (await _service.GetAsync(false, default)).State.Should().Be(ApiCreditsState.Unavailable);
        _handler.Calls.Should().Be(2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetAsync_ParseError_UsesStaleFallbackWhenAvailable(bool previousGood)
    {
        if (previousGood) await _service.GetAsync(false, default);
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("bad JSON " + Marker) });
        var result = await _service.GetAsync(true, default);
        result.State.Should().Be(previousGood ? ApiCreditsState.Ok : ApiCreditsState.Unavailable);
        result.IsStale.Should().Be(previousGood);
        _logger.Entries.Should().OnlyContain(e => !e.Text.Contains(Marker) && e.Exception == null);
    }

    [TestMethod]
    public async Task GetAsync_DeveloperMode_ReturnsConfiguredSampleWithoutReadingKeyOrHttp()
    {
        _values.DeveloperModeEnabled = true; _values.ApiCreditsMonthlyUsd = 150;
        var result = await _service.GetAsync(true, default);
        result.SpentUsd.Should().Be(95.33m); result.MonthlyUsd.Should().Be(150); result.RemainingUsd.Should().Be(54.67m);
        _handler.Calls.Should().Be(0); _environmentReads.Should().BeEmpty();
        _values.DeveloperModeEnabled = false; _processKey = null;
        (await _service.GetAsync(false, default)).State.Should().Be(ApiCreditsState.NoKey);
    }

    [TestMethod]
    public async Task GetAsync_ChangedSettings_InvalidatesCacheOnNextRefresh()
    {
        await _service.GetAsync(false, default);
        _values.ApiCreditsMonthlyUsd = 300;
        (await _service.GetAsync(false, default)).MonthlyUsd.Should().Be(300); _handler.Calls.Should().Be(2);
        _values.ApiCreditsCycleStartDay = 10;
        (await _service.GetAsync(false, default)).CycleStart.Should().Be(new DateOnly(2026, 10, 10));
        _handler.Calls.Should().Be(3);
    }

    [TestMethod]
    public async Task GetAsync_NewCycleFailure_DoesNotCarryPriorCycleSpend()
    {
        await _service.GetAsync(false, default);
        _clock.Now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero); Status(HttpStatusCode.InternalServerError);
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Unavailable); result.FetchedAtUtc.Should().BeNull();
        result.SpentUsd.Should().Be(0); result.CycleStart.Should().Be(new DateOnly(2026, 11, 1));
    }

    [TestMethod]
    public async Task GetAsync_PoolChangedThenFetchFails_RecalculatesLastGoodSpendAgainstNewPool()
    {
        await _service.GetAsync(false, default);
        _values.ApiCreditsMonthlyUsd = 300; Status(HttpStatusCode.ServiceUnavailable);
        var result = await _service.GetAsync(false, default);
        result.MonthlyUsd.Should().Be(300); result.SpentUsd.Should().Be(95.33m);
        result.RemainingUsd.Should().Be(204.67m); result.IsStale.Should().BeTrue();
        _handler.Calls.Should().Be(2);
    }

    [TestMethod]
    public async Task GetAsync_RejectAfterSuccess_DiscardsGoodValueForSubsequentFailure()
    {
        await _service.GetAsync(false, default); Status(HttpStatusCode.Unauthorized);
        (await _service.GetAsync(true, default)).State.Should().Be(ApiCreditsState.Rejected);
        Status(HttpStatusCode.InternalServerError);
        (await _service.GetAsync(true, default)).State.Should().Be(ApiCreditsState.Unavailable);
    }

    [TestMethod]
    public async Task GetAsync_ConcurrentCalls_SharesThrottledFetch()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Respond = async (_, ct) =>
        { await ready.Task.WaitAsync(ct); return new(HttpStatusCode.OK) { Content = new StringContent(Success) }; };
        var first = _service.GetAsync(false, default); var second = _service.GetAsync(false, default);
        ready.SetResult(); await Task.WhenAll(first, second);
        _handler.Calls.Should().Be(1);
    }

    [TestMethod]
    public async Task GetAsync_CanceledCaller_ReturnsUnavailableWithoutThrowing()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        (await _service.GetAsync(false, canceled.Token)).State.Should().Be(ApiCreditsState.Unavailable);
        _handler.Calls.Should().Be(0);
    }

    [TestMethod]
    public async Task GetAsync_RotateCredentialWithinThrottleWindowWithoutForce_FetchesNewRequestAndDoesNotReturnPreviousSpendOrIdentity()
    {
        EnableBreakdown();
        _processKey = Marker;
        var resultA = await _service.GetAsync(false, default);
        resultA.State.Should().Be(ApiCreditsState.Ok);
        resultA.SpentUsd.Should().Be(95.33m);
        resultA.Contributors.Single(c => c.IsYou).Name.Should().Be("Alex");
        _handler.Calls.Should().Be(1);

        _clock.Now = _clock.Now.AddMinutes(1);
        _processKey = "other-middle-hint";
        const string tokenCostsB = """
            {"data":[{"results":[{"currency":"USD","amount":"4500","cost_type":"tokens","model":"model-a","service_tier":"standard","context_window":"0-200k","token_type":"output_tokens","inference_geo":"global"}]}],"has_more":false}
            """;
        _handler.Respond = (_, _) => JsonResponse(tokenCostsB);

        var resultB = await _service.GetAsync(false, default);
        _handler.Calls.Should().Be(2);
        resultB.State.Should().Be(ApiCreditsState.Ok);
        resultB.SpentUsd.Should().Be(45.00m);
        resultB.SpentUsd.Should().NotBe(resultA.SpentUsd);
        resultB.Contributors.Single(c => c.IsYou).Name.Should().Be("Morgan");
        resultB.Contributors.Single(c => c.Name == "Alex").IsYou.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAsync_RotateCredentialAndNewKeyFailsWithForce_ReturnsUnavailableWithoutFallbackToPreviousSpend()
    {
        var first = await _service.GetAsync(false, default);
        first.State.Should().Be(ApiCreditsState.Ok);
        first.SpentUsd.Should().Be(95.33m);

        _processKey = "other-key-marker";
        Status(HttpStatusCode.ServiceUnavailable);

        var result = await _service.GetAsync(true, default);
        result.State.Should().Be(ApiCreditsState.Unavailable);
        result.FetchedAtUtc.Should().BeNull();
        result.SpentUsd.Should().Be(0);
        _handler.Calls.Should().Be(2);
    }

    [TestMethod]
    public async Task GetAsync_ReplaceRejectedKeyWithValidKeyWithinThrottleWindow_ReturnsOkNotRejected()
    {
        Status(HttpStatusCode.Unauthorized);
        var first = await _service.GetAsync(false, default);
        first.State.Should().Be(ApiCreditsState.Rejected);
        _handler.Calls.Should().Be(1);

        _clock.Now = _clock.Now.AddMinutes(1);
        _processKey = "valid-replacement-key";
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Success) });

        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Ok);
        result.SpentUsd.Should().Be(95.33m);
        _handler.Calls.Should().Be(2);
    }

    [TestMethod]
    public async Task GetAsync_SameKeyTwiceWithinFiveMinutesWithoutForce_ThrottlesToOneRequest()
    {
        var first = await _service.GetAsync(false, default);
        first.State.Should().Be(ApiCreditsState.Ok);
        _handler.Calls.Should().Be(1);

        _clock.Now = _clock.Now.AddMinutes(3);
        var second = await _service.GetAsync(false, default);
        second.State.Should().Be(ApiCreditsState.Ok);
        second.SpentUsd.Should().Be(first.SpentUsd);
        _handler.Calls.Should().Be(1);
    }

    [TestMethod]
    public async Task GetAsync_AnyRequestOrFailure_NeverLogsRawKeyText()
    {
        const string secretKey = "sk-ant-admin-very-secret-credential-value";
        _processKey = secretKey;
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"results":[{"amount":"1000","currency":"USD"}]}],"has_more":false}""")
        });

        await _service.GetAsync(false, default);

        _clock.Now = _clock.Now.AddMinutes(6);
        Status(HttpStatusCode.InternalServerError);
        await _service.GetAsync(false, default);

        Status(HttpStatusCode.Unauthorized);
        await _service.GetAsync(true, default);

        _logger.Entries.Should().NotBeEmpty();
        _logger.Entries.Should().OnlyContain(e => !e.Text.Contains(secretKey) && (e.Exception == null || !e.Exception.ToString().Contains(secretKey)));
    }

    [TestMethod]
    public async Task GetAsync_KeyClearedThenRestoredWithinThrottleWindow_FetchesFreshRequestAndClearsPreviousData()
    {
        var first = await _service.GetAsync(false, default);
        first.State.Should().Be(ApiCreditsState.Ok);
        _handler.Calls.Should().Be(1);

        _clock.Now = _clock.Now.AddMinutes(1);
        _processKey = null;
        var noKey = await _service.GetAsync(false, default);
        noKey.State.Should().Be(ApiCreditsState.NoKey);
        _handler.Calls.Should().Be(1);

        _clock.Now = _clock.Now.AddMinutes(1);
        _processKey = Marker;
        var second = await _service.GetAsync(false, default);
        second.State.Should().Be(ApiCreditsState.Ok);
        _handler.Calls.Should().Be(2);
    }
}
