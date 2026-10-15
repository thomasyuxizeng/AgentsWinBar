using System.Net;
using AwesomeAssertions;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Tests.Unit.Services;

public partial class ApiBillingServiceTests
{
    private const string TokenCosts = """
        {"data":[{"results":[{"currency":"USD","amount":"9517","cost_type":"tokens","model":"model-a","service_tier":"standard","context_window":"0-200k","token_type":"output_tokens","inference_geo":"global"},
        {"currency":"USD","amount":"16","cost_type":"code_execution","model":null,"token_type":null}]}],"has_more":false}
        """;
    private const string Keys = """
        {"data":[{"id":"key-a","name":"Invented key","status":"inactive","partial_key_hint":"test-only...marker","created_by":{"id":"user-a"}},
        {"id":"key-a2","partial_key_hint":null,"created_by":{"id":"user-a"}},
        {"id":"key-b","partial_key_hint":"other...hint","created_by":{"id":"user-b"}}],"has_more":false}
        """;
    private const string Users = """{"data":[{"id":"user-a","name":"Alex","role":"admin"},{"id":"user-b","name":"Morgan","role":"user"}]}""";
    private const string TokenUsage = """
        {"data":[{"results":[{"api_key_id":"key-a","model":"model-a","service_tier":"standard","context_window":"0-200k","output_tokens":50,"inference_geo":null,"cache_creation":null},
        {"api_key_id":"key-a2","model":"model-a","service_tier":"standard","context_window":"0-200k","output_tokens":25},
        {"api_key_id":"key-b","model":"model-a","service_tier":"standard","context_window":"0-200k","output_tokens":25}]}],"has_more":false}
        """;
    private static Task<HttpResponseMessage> JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
    private static string ReportFor(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/v1/organizations/api_keys" => Keys,
        "/v1/organizations/users" => Users,
        "/v1/organizations/usage_report/messages" => TokenUsage,
        _ => throw new InvalidOperationException("Unexpected fake request")
    };
    private void EnableBreakdown()
    {
        _handler.Respond = (_, _) => JsonResponse(TokenCosts);
        _handler.BreakdownRespond = (request, _) => JsonResponse(ReportFor(request));
    }
    private int RequestCount(string path) => _handler.Requests.Count(url => new Uri(url).AbsolutePath.EndsWith(path, StringComparison.Ordinal));

    [TestMethod]
    [DataRow("cost_report")]
    [DataRow("api_keys")]
    [DataRow("users")]
    [DataRow("usage_report/messages")]
    public async Task GetAsync_CanceledDuringUncooperativeResponse_StopsFollowupsAndKeepsPreviousCache(string endpoint)
    {
        EnableBreakdown();
        var previous = await _service.GetAsync(false, default);
        using var cts = new CancellationTokenSource();
        _handler.Requests.Clear();
        Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken _)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(endpoint, StringComparison.Ordinal)) cts.Cancel();
            return JsonResponse(request.RequestUri.AbsolutePath.EndsWith("cost_report", StringComparison.Ordinal)
                ? TokenCosts.Replace("9517", "15000") : ReportFor(request));
        }
        _handler.Respond = Respond; _handler.BreakdownRespond = Respond;
        (await _service.GetAsync(true, cts.Token)).State.Should().Be(ApiCreditsState.Unavailable);
        _handler.Requests.Last().Should().Contain(endpoint);
        var requestCount = _handler.Requests.Count;
        var cached = await _service.GetAsync(false, default);
        cached.Should().BeEquivalentTo(previous);
        _handler.Requests.Should().HaveCount(requestCount);
    }

    [TestMethod]
    public async Task GetAsync_OwnershipStopsMatching_DiscardsPreviousYouBreakdown()
    {
        EnableBreakdown();
        (await _service.GetAsync(false, default)).HasBreakdown.Should().BeTrue();
        _handler.BreakdownRespond = (request, _) => JsonResponse(ReportFor(request).Replace("test-only...marker", "different...marker"));
        var result = await _service.GetAsync(true, default);
        result.State.Should().Be(ApiCreditsState.Ok); result.HasBreakdown.Should().BeFalse();
        result.Contributors.Should().BeEmpty(); result.UnattributedUsd.Should().Be(95.33m);
    }

    [TestMethod]
    public async Task GetAsync_ChangedCredentialAndUsageFails_DoesNotReuseOtherMembersIdentity()
    {
        EnableBreakdown();
        await _service.GetAsync(false, default);
        _processKey = "other-middle-hint";
        _handler.BreakdownRespond = (request, _) => request.RequestUri!.AbsolutePath.Contains("usage_report")
            ? JsonResponse("", HttpStatusCode.Forbidden) : JsonResponse(ReportFor(request));
        var result = await _service.GetAsync(true, default);
        result.State.Should().Be(ApiCreditsState.Ok); result.HasBreakdown.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAsync_EmptySpendAndUsage_IncludesZeroSpendOwnersAndToleratesNullFields()
    {
        EnableBreakdown();
        _handler.Respond = (_, _) => JsonResponse("""{"data":[],"has_more":false}""");
        _handler.BreakdownRespond = (request, _) => JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/v1/organizations/usage_report/messages" => """{"data":[{"results":[null,{"api_key_id":null,"cache_creation":null}]}],"has_more":false}""",
            "/v1/organizations/users" => """{"data":[null,{"id":"user-a","name":null}]}""",
            _ => Keys
        });
        var result = await _service.GetAsync(false, default);
        result.HasBreakdown.Should().BeTrue(); result.SpentUsd.Should().Be(0);
        result.Contributors.Should().HaveCount(2).And.OnlyContain(member => member.SpentUsd == 0 && member.Name == "Teammate");
        result.UnattributedUsd.Should().Be(0);
    }

    [TestMethod]
    public async Task GetAsync_FullBreakdown_ReturnsTeamTotalAndMemberSpendWithRequiredQueries()
    {
        EnableBreakdown();
        _handler.BreakdownRespond = (request, _) =>
        {
            request.Headers.GetValues("x-api-key").Single().Should().Be(Marker);
            request.Headers.GetValues("anthropic-version").Single().Should().Be("2023-06-01");
            request.Headers.UserAgent.ToString().Should().StartWith("CodexBarWin/");
            return JsonResponse(ReportFor(request));
        };
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Ok); result.SpentUsd.Should().Be(95.33m);
        result.HasBreakdown.Should().BeTrue(); result.UnattributedUsd.Should().Be(0.16m);
        result.Contributors.Select(c => c.SpentUsd).Should().Equal(71.3775m, 23.7925m);
        result.Contributors.Select(c => c.Name).Should().Equal("Alex", "Morgan");
        result.Contributors[0].IsYou.Should().BeTrue();
        _handler.Requests[0].Should().Contain("group_by[]=description");
        var usage = Uri.UnescapeDataString(_handler.Requests.Single(url => url.Contains("usage_report")));
        usage.Should().Contain("starting_at=2026-10-01T00:00:00Z").And.Contain("ending_at=2026-10-16T00:00:00Z");
        usage.Should().Contain("group_by[]=api_key_id&group_by[]=model&group_by[]=service_tier&group_by[]=context_window");
        _handler.Requests.Should().HaveCount(4);
        result.RemainingUsd.Should().Be(104.67m);
    }

    [TestMethod]
    [DataRow("api_keys", "http")]
    [DataRow("users", "http")]
    [DataRow("usage_report/messages", "http")]
    [DataRow("api_keys", "parse")]
    [DataRow("users", "parse")]
    [DataRow("usage_report/messages", "parse")]
    [DataRow("api_keys", "timeout")]
    [DataRow("users", "timeout")]
    [DataRow("usage_report/messages", "timeout")]
    [DataRow("usage_report/messages", "network")]
    public async Task GetAsync_OptionalCallFails_KeepsOkTotalAndNeverLogsSecrets(string endpoint, string failure)
    {
        EnableBreakdown();
        _handler.BreakdownRespond = (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith(endpoint, StringComparison.Ordinal)) return JsonResponse(ReportFor(request));
            return failure switch
            {
                "http" => JsonResponse("untrusted " + Marker, HttpStatusCode.Forbidden),
                "parse" => JsonResponse("untrusted " + Marker),
                "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException(Marker)),
                _ => Task.FromException<HttpResponseMessage>(new HttpRequestException(Marker))
            };
        };
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Ok); result.SpentUsd.Should().Be(95.33m);
        result.HasBreakdown.Should().BeFalse(); result.Contributors.Should().BeEmpty();
        result.UnattributedUsd.Should().Be(95.33m); result.IsStale.Should().BeFalse();
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning);
        _logger.Entries.Should().OnlyContain(entry => !entry.Text.Contains(Marker) && entry.Exception == null);
        (result.Message ?? "").Should().NotContain(Marker);
    }

    [TestMethod]
    public async Task GetAsync_NoMatchingHint_OmitsBreakdown()
    {
        EnableBreakdown();
        _handler.BreakdownRespond = (request, _) => JsonResponse(ReportFor(request).Replace("test-only...marker", "other-prefix...marker"));
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Ok); result.HasBreakdown.Should().BeFalse();
        result.Contributors.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAsync_MetadataCachedForOneHour_ForceBypassesBothCaches()
    {
        EnableBreakdown();
        await _service.GetAsync(false, default);
        _clock.Now = _clock.Now.AddMinutes(4);
        (await _service.GetAsync(false, default)).HasBreakdown.Should().BeTrue();
        _handler.Requests.Should().HaveCount(4);
        _clock.Now = _clock.Now.AddMinutes(2);
        (await _service.GetAsync(false, default)).HasBreakdown.Should().BeTrue();
        RequestCount("api_keys").Should().Be(1); RequestCount("users").Should().Be(1);
        RequestCount("usage_report/messages").Should().Be(2); _handler.Calls.Should().Be(2);
        _clock.Now = _clock.Now.AddMinutes(54);
        await _service.GetAsync(false, default);
        RequestCount("api_keys").Should().Be(2); RequestCount("users").Should().Be(2);
        await _service.GetAsync(true, default);
        RequestCount("api_keys").Should().Be(3); RequestCount("users").Should().Be(3);
        RequestCount("usage_report/messages").Should().Be(4); _handler.Calls.Should().Be(4);
    }

    [TestMethod]
    [DataRow(150)]
    [DataRow(80)]
    public async Task GetAsync_BreakdownFailsAfterSuccess_OmitsOldAmountsFromFreshTotal(int spent)
    {
        EnableBreakdown();
        (await _service.GetAsync(false, default)).HasBreakdown.Should().BeTrue();
        _clock.Now = _clock.Now.AddDays(1);
        _handler.Respond = (_, _) => JsonResponse("{\"data\":[{\"results\":[{\"currency\":\"USD\",\"amount\":\"" + spent * 100 + "\"}]}],\"has_more\":false}");
        _handler.BreakdownRespond = (_, _) => JsonResponse("", HttpStatusCode.ServiceUnavailable);
        var fallback = await _service.GetAsync(true, default);
        fallback.State.Should().Be(ApiCreditsState.Ok); fallback.IsStale.Should().BeFalse();
        fallback.SpentUsd.Should().Be(spent); fallback.FetchedAtUtc.Should().Be(_clock.Now.UtcDateTime);
        fallback.HasBreakdown.Should().BeFalse(); fallback.Contributors.Should().BeEmpty();
        fallback.UnattributedUsd.Should().Be(spent);
        (await _service.GetAsync(false, default)).HasBreakdown.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetAsync_NegativeAdjustmentExceedsUnattributedCost_WithholdsUnreconciledBreakdown(bool onlyAdjustment)
    {
        EnableBreakdown();
        var costs = onlyAdjustment ? """{"data":[{"results":[{"currency":"USD","amount":"-2000","cost_type":"adjustment"}]}],"has_more":false}"""
            : TokenCosts.Replace("9517", "10000").Replace("\"16\"", "\"-2000\"");
        _handler.Respond = (_, _) => JsonResponse(costs);
        var result = await _service.GetAsync(false, default);
        result.State.Should().Be(ApiCreditsState.Ok); result.IsStale.Should().BeFalse();
        result.SpentUsd.Should().Be(onlyAdjustment ? -20m : 80m);
        result.RemainingUsd.Should().Be(onlyAdjustment ? 220m : 120m);
        result.HasBreakdown.Should().BeFalse(); result.Contributors.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAsync_CostFailsAfterBreakdownSuccess_PreservesStaleBreakdown()
    {
        EnableBreakdown(); var first = await _service.GetAsync(false, default);
        Status(HttpStatusCode.ServiceUnavailable);
        var fallback = await _service.GetAsync(true, default);
        fallback.IsStale.Should().BeTrue(); fallback.HasBreakdown.Should().BeTrue();
        fallback.Contributors.Should().Equal(first.Contributors); fallback.UnattributedUsd.Should().Be(0.16m);
    }

    [TestMethod]
    public async Task GetAsync_DeveloperSample_UsesExactMemberAmountsAndNoCredentialOrNetwork()
    {
        _values.DeveloperModeEnabled = true;
        var result = await _service.GetAsync(true, default);
        result.HasBreakdown.Should().BeTrue(); result.SpentUsd.Should().Be(95.33m); result.UnattributedUsd.Should().Be(0.16m);
        result.Contributors.Select(c => c.Name).Should().Equal("You", "Riotline");
        result.Contributors.Select(c => c.SpentUsd).Should().Equal(71.40m, 23.77m);
        result.Contributors[0].IsYou.Should().BeTrue();
        _handler.Requests.Should().BeEmpty(); _environmentReads.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAsync_PaginatedOptionalReports_UsesEscapedAfterIdAndPage()
    {
        EnableBreakdown();
        var seen = new Dictionary<string, int>();
        _handler.BreakdownRespond = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            seen[path] = seen.GetValueOrDefault(path) + 1;
            var json = ReportFor(request);
            if (path.EndsWith("users", StringComparison.Ordinal)) json = json[..^1] + ",\"has_more\":false}";
            if (seen[path] == 1)
                json = json.Replace("\"has_more\":false", "\"has_more\":true,\"last_id\":\"next+&id\",\"next_page\":\"next+&id\"");
            else
                request.RequestUri.OriginalString.Should().Contain((path.Contains("usage_report") ? "page=" : "after_id=") + "next%2B%26id");
            return JsonResponse(json);
        };
        var result = await _service.GetAsync(false, default);
        result.HasBreakdown.Should().BeTrue(); result.SpentUsd.Should().Be(95.33m);
        result.UnattributedUsd.Should().Be(0.16m);
        seen.Values.Should().OnlyContain(count => count == 2);
    }

    [TestMethod]
    [DataRow("api_keys", false)]
    [DataRow("users", false)]
    [DataRow("usage_report/messages", false)]
    [DataRow("api_keys", true)]
    [DataRow("users", true)]
    [DataRow("usage_report/messages", true)]
    public async Task GetAsync_OptionalPaginationIncomplete_OmitsBreakdownWithoutPartialSpend(string endpoint, bool cap)
    {
        EnableBreakdown(); var pages = 0;
        _handler.BreakdownRespond = (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith(endpoint, StringComparison.Ordinal)) return JsonResponse(ReportFor(request));
            pages++;
            return JsonResponse("{\"data\":[],\"has_more\":true,\"last_id\":\"" + (cap ? pages.ToString() : "same")
                + "\",\"next_page\":\"" + (cap ? pages.ToString() : "same") + "\"}");
        };
        var result = await _service.GetAsync(false, default);
        result.HasBreakdown.Should().BeFalse(); result.State.Should().Be(ApiCreditsState.Ok); result.SpentUsd.Should().Be(95.33m);
        pages.Should().Be(cap ? 5 : 2);
    }

    [TestMethod]
    public async Task GetAsync_UnpricedUsage_LogsDebugCounterWithoutSecrets()
    {
        EnableBreakdown();
        _handler.BreakdownRespond = (request, _) => JsonResponse(ReportFor(request).Replace("\"output_tokens\":50", "\"uncached_input_tokens\":50"));
        var result = await _service.GetAsync(false, default);
        result.HasBreakdown.Should().BeTrue();
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Debug && entry.Text.Contains("unpriced token groups"));
        _logger.Entries.Should().OnlyContain(entry => !entry.Text.Contains(Marker) && entry.Exception == null);
    }
}
