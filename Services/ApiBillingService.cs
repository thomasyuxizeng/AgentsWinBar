using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

/// <summary>Fetches organization cost totals without persisting credentials or spend.</summary>
public sealed class ApiBillingService : IApiBillingService, IDisposable
{
    public const string AdminKeyEnvironmentVariable = "ANTHROPIC_ADMIN_KEY";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly ISettingsService _settings;
    private readonly ILogger<ApiBillingService> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeProvider _clock;
    private readonly Func<string, EnvironmentVariableTarget, string?> _readEnvironment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ApiCreditsInfo? _cached;
    private ApiCreditsInfo? _lastGood;
    private Ownership? _ownership;
    private sealed record Ownership(ApiOrganizationKey[] Keys, Dictionary<string, string> Names, DateTime FetchedAt);
    private DateTime _attemptedAt;
    private CacheScope? _scope;
    private readonly record struct CacheScope(decimal Monthly, int Day, bool Developer, DateTime CycleStart);
    private string? _credentialFingerprint;

    public ApiBillingService(ISettingsService settings, ILogger<ApiBillingService> logger,
        HttpClient? httpClient = null, TimeProvider? timeProvider = null,
        Func<string, EnvironmentVariableTarget, string?>? environmentReader = null)
    {
        _settings = settings; _logger = logger; _clock = timeProvider ?? TimeProvider.System;
        _readEnvironment = environmentReader ?? Environment.GetEnvironmentVariable;
        // Never forward an admin credential to a redirect destination.
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };
        _ownsClient = httpClient == null;
    }

    public async Task<ApiCreditsInfo> GetAsync(bool force, CancellationToken ct)
    {
        var entered = false;
        var now = _clock.GetUtcNow().UtcDateTime;
        var monthly = ApiCreditsCalculator.NormalizeMonthlyUsd(_settings.Settings.ApiCreditsMonthlyUsd);
        var day = _settings.Settings.ApiCreditsCycleStartDay is >= 1 and <= 28
            ? _settings.Settings.ApiCreditsCycleStartDay : 1;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            now = _clock.GetUtcNow().UtcDateTime;
            monthly = ApiCreditsCalculator.NormalizeMonthlyUsd(_settings.Settings.ApiCreditsMonthlyUsd);
            day = _settings.Settings.ApiCreditsCycleStartDay is >= 1 and <= 28 ? _settings.Settings.ApiCreditsCycleStartDay : 1;
            var developer = _settings.Settings.DeveloperModeEnabled;
            var window = ApiCreditsCalculator.GetCycleWindow(day, now);
            var scope = new CacheScope(monthly, day, developer, window.StartUtc);
            if (_scope != scope)
            {
                // Pool edits invalidate the throttle but keep same-cycle spend for failure fallback.
                // A different reporting window or mode must never reuse old totals or a sample.
                _cached = null;
                if (_scope is not { } previous || previous.CycleStart != scope.CycleStart ||
                    previous.Day != scope.Day || previous.Developer != scope.Developer)
                { _lastGood = null; }
                _scope = scope;
            }
            if (developer)
            {
                _credentialFingerprint = null;
                return Store(ApiCreditsCalculator.BuildOk(monthly, 95.33m, day, now, now) with
                {
                    HasBreakdown = true, UnattributedUsd = 0.16m,
                    Contributors = new ApiCreditsContributor[]
                    {
                        new() { Name = "You", UserId = "sample-you", SpentUsd = 71.40m, IsYou = true },
                        new() { Name = "Teammate", UserId = "sample-teammate", SpentUsd = 23.77m }
                    }
                }, now);
            }

            // Read at fetch time, process first; never retain the key in service state.
            var key = _readEnvironment(AdminKeyEnvironmentVariable, EnvironmentVariableTarget.Process);
            if (string.IsNullOrWhiteSpace(key))
                key = _readEnvironment(AdminKeyEnvironmentVariable, EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(key))
            {
                _cached = null; _lastGood = null; _ownership = null;
                _credentialFingerprint = null;
                _logger.LogDebug("Anthropic admin key environment variable is not set");
                return ApiCreditsCalculator.BuildNoKey(monthly, day, now);
            }
            var fingerprint = ComputeFingerprint(key);
            if (!string.Equals(_credentialFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _cached = null; _lastGood = null; _ownership = null;
                _credentialFingerprint = fingerprint;
            }
            if (!force && _cached != null && now >= _attemptedAt && now - _attemptedAt < CacheTtl)
                return Recalculate(_cached, monthly, day, now);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            decimal totalCents = 0;
            var costLines = new List<ApiCostResult>();
            string? page = null;
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            var version = typeof(ApiBillingService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0] ?? typeof(ApiBillingService).Assembly.GetName().Version?.ToString() ?? "1.0";
            for (var count = 0; count < 5; count++)
            {
                static string Timestamp(DateTime date) => Uri.EscapeDataString(date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                var url = "https://api.anthropic.com/v1/organizations/cost_report?starting_at=" + Timestamp(window.StartUtc)
                    + "&ending_at=" + Timestamp(now.Date.AddDays(1)) + "&bucket_width=1d&limit=31&group_by[]=description"
                    + (page == null ? "" : "&page=" + Uri.EscapeDataString(page));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Headers.UserAgent.ParseAdd("AgentsWinBar/" + version);
                using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _lastGood = null; _ownership = null;
                    _logger.LogWarning("Anthropic admin key rejected (HTTP {Status})", (int)response.StatusCode);
                    return Store(ApiCreditsCalculator.BuildRejected(monthly, day, now, (int)response.StatusCode), now);
                }
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Anthropic cost report unavailable (HTTP {Status})", (int)response.StatusCode);
                    return Failure(monthly, day, now);
                }
                var json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                if (!ApiCreditsCalculator.TryParseCostReport(json, out var cents, out var next, out var more, out var ignored))
                {
                    _logger.LogWarning("Anthropic cost report could not be parsed");
                    return Failure(monthly, day, now);
                }
                if (ignored > 0) _logger.LogDebug("Ignored {Count} non-USD cost results", ignored);
                totalCents = checked(totalCents + cents);
                var report = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ApiCostReport)!;
                costLines.AddRange(report.Data!.SelectMany(bucket => bucket.Results!));
                if (!more)
                {
                    var fetched = _clock.GetUtcNow().UtcDateTime;
                    var total = ApiCreditsCalculator.BuildOk(monthly, totalCents / 100m, day, now, fetched);
                    var result = await AddBreakdownAsync(total, costLines, key, force, window.StartUtc,
                        now.Date.AddDays(1), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return Store(result, fetched);
                }
                if (string.IsNullOrWhiteSpace(next) || !seenPages.Add(next)) break;
                page = next;
            }
            // Partial totals are misleading; a missing cursor, repeated cursor or cap is a failed fetch.
            _logger.LogWarning("Anthropic cost report pagination was incomplete");
            return Failure(monthly, day, now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return ApiCreditsCalculator.BuildUnavailable(monthly, day, now); }
        catch (Exception)
        {
            // Transport/parse exceptions can embed credentials, headers or response bodies. Never log them.
            if (ct.IsCancellationRequested) _logger.LogDebug("Anthropic cost report request canceled");
            else _logger.LogWarning("Anthropic cost report request failed or timed out");
            return entered ? Failure(monthly, day, now) : ApiCreditsCalculator.BuildUnavailable(monthly, day, now);
        }
        finally { if (entered) _gate.Release(); }
    }

    private async Task<ApiCreditsInfo> AddBreakdownAsync(ApiCreditsInfo total, List<ApiCostResult> costs,
        string key, bool force, DateTime start, DateTime end, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var now = _clock.GetUtcNow().UtcDateTime;
            var ownership = _ownership;
            if (force || ownership == null || now < ownership.FetchedAt || now - ownership.FetchedAt >= TimeSpan.FromHours(1))
            {
                var keys = await ReadPagesAsync("api_keys?limit=100", key, AppJsonSerializerContext.Default.ApiKeysReport,
                    report => report.Data, report => report.HasMore, report => report.LastId, "after_id", deadline.Token).ConfigureAwait(false);
                var users = await ReadPagesAsync("users?limit=100", key, AppJsonSerializerContext.Default.ApiUsersReport,
                    report => report.Data, report => report.HasMore, report => report.LastId, "after_id", deadline.Token).ConfigureAwait(false);
                ownership = new(keys.ToArray(), users.Where(user => !string.IsNullOrWhiteSpace(user.Id))
                    .GroupBy(user => user.Id!, StringComparer.Ordinal).ToDictionary(group => group.Key,
                        group => group.Last().Name ?? "Teammate", StringComparer.Ordinal), now);
                _ownership = ownership;
            }
            var you = ApiCreditsAttribution.ResolveYou(ownership.Keys, key);
            if (you == null)
            {
                _logger.LogWarning("Anthropic API spend breakdown ownership could not be resolved");
                return total with { UnattributedUsd = Math.Max(0, total.SpentUsd) };
            }
            static string Timestamp(DateTime date) => Uri.EscapeDataString(date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            var usage = await ReadPagesAsync("usage_report/messages?starting_at=" + Timestamp(start)
                + "&ending_at=" + Timestamp(end) + "&bucket_width=1d&limit=31"
                + "&group_by[]=api_key_id&group_by[]=model&group_by[]=service_tier&group_by[]=context_window",
                key, AppJsonSerializerContext.Default.ApiUsageReport,
                report => report.Data?.SelectMany(bucket => bucket?.Results ?? throw new JsonException()).ToList(),
                report => report.HasMore ?? throw new JsonException(), report => report.NextPage, "page", deadline.Token).ConfigureAwait(false);
            var creators = ownership.Keys.Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.CreatedBy?.Id))
                .GroupBy(item => item.Id!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().CreatedBy!.Id!, StringComparer.Ordinal);
            var attribution = ApiCreditsAttribution.Calculate(total.SpentUsd, costs, usage, creators, ownership.Names, you);
            if (attribution.UnpricedTokenGroups > 0)
                _logger.LogDebug("Anthropic attribution has {Count} unpriced token groups ({Tokens} tokens)",
                    attribution.UnpricedTokenGroups, attribution.UnpricedTokens);
            // Signed adjustments can make the token allocation exceed the authoritative total.
            // Withhold attribution rather than silently dropping the negative reconciliation.
            if (!attribution.Reconciled)
                return total with { UnattributedUsd = Math.Max(0, total.SpentUsd) };
            if (!attribution.Contributors.Any(member => member.IsYou)) throw new InvalidOperationException();
            var result = total with { Contributors = attribution.Contributors, UnattributedUsd = attribution.UnattributedUsd, HasBreakdown = true };
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Optional reports must never hide a successful total, or expose untrusted response/exception text.
            _logger.LogWarning("Anthropic API spend breakdown unavailable or ownership could not be resolved");
            return total with { UnattributedUsd = Math.Max(0, total.SpentUsd) };
        }
    }

    private async Task<List<TRow>> ReadPagesAsync<TReport, TRow>(string path, string key,
        JsonTypeInfo<TReport> type, Func<TReport, List<TRow?>?> rows,
        Func<TReport, bool?> hasMore, Func<TReport, string?> cursor, string cursorParameter, CancellationToken ct)
        where TReport : class where TRow : class
    {
        var results = new List<TRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        for (var count = 0; count < 5; count++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/organizations/" + path
                + (page == null ? "" : "&" + cursorParameter + "=" + Uri.EscapeDataString(page)));
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
            var version = typeof(ApiBillingService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0] ?? typeof(ApiBillingService).Assembly.GetName().Version?.ToString() ?? "1.0";
            request.Headers.UserAgent.ParseAdd("AgentsWinBar/" + version);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var report = JsonSerializer.Deserialize(json, type) ?? throw new JsonException();
            var items = rows(report) ?? throw new JsonException();
            results.AddRange(items.OfType<TRow>());
            if (hasMore(report) != true) return results;
            page = cursor(report);
            if (string.IsNullOrWhiteSpace(page) || !seen.Add(page)) break;
        }
        throw new InvalidOperationException();
    }

    private ApiCreditsInfo Failure(decimal monthly, int day, DateTime now) => Store(
        _lastGood != null ? Recalculate(_lastGood, monthly, day, now) with { IsStale = true }
            : ApiCreditsCalculator.BuildUnavailable(monthly, day, now), now);

    private ApiCreditsInfo Store(ApiCreditsInfo result, DateTime now)
    {
        _cached = result; _attemptedAt = now;
        if (result.State == ApiCreditsState.Ok && !result.IsStale) _lastGood = result;
        return result;
    }

    private static ApiCreditsInfo Recalculate(ApiCreditsInfo result, decimal monthly, int day, DateTime now) =>
        result.State == ApiCreditsState.Ok && result.FetchedAtUtc is { } fetched
            ? ApiCreditsCalculator.BuildOk(monthly, result.SpentUsd, day, now, fetched) with
                { IsStale = result.IsStale || now - fetched > TimeSpan.FromMinutes(15),
                    Contributors = result.Contributors, UnattributedUsd = result.UnattributedUsd, HasBreakdown = result.HasBreakdown }
            : result;

    private static string ComputeFingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8);

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
