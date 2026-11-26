using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services.Native;

public sealed record AntigravityNativeOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Upstream allows the quota summary 2 s; a cold TLS handshake can take longer, so 5 s here.</summary>
    public TimeSpan QuotaSummaryTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Try renewing a token this close to expiry (upstream's refresh safety window).</summary>
    public TimeSpan ExpirySafetyWindow { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Cloud Code serves quota only to the Antigravity "hub" client family. Upstream pins this identity; the
    /// Linux variant is what the working WSL route sends today.
    /// </summary>
    public string UserAgent { get; init; } = "antigravity/hub/2.9.1 linux/amd64";
    /// <summary>The endpoints (a Debug build's test hooks point them at the fake provider server).</summary>
    public Uri LoadCodeAssistUri { get; init; } = AntigravityNativeClient.LoadCodeAssistUri;
    public Uri QuotaSummaryUri { get; init; } = AntigravityNativeClient.QuotaSummaryUri;
    public Uri AvailableModelsUri { get; init; } = AntigravityNativeClient.AvailableModelsUri;
    /// <summary>
    /// Asked each time the sign-in is found expired: true lets the client have agy renew it with <c>agy models</c>
    /// (the app's RenewGeminiSignIn setting). Null never renews.
    /// </summary>
    public Func<bool>? RenewExpiredSignIn { get; init; }
    /// <summary>Delay after the first failed renewal of an expired sign-in.</summary>
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>A renewal that has not finished by then is stopped (its whole process tree).</summary>
    public TimeSpan RenewalTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Stops in-flight renewals when the app shuts down.</summary>
    public CancellationToken ShutdownToken { get; init; }
}

/// <summary>
/// Reads Antigravity quota with agy's Windows sign-in (Credential Manager, read-only) and presents it as the app's
/// Gemini provider exactly as the WSL shim reshapes it: primary = Gemini 5-hour, secondary = Gemini weekly, extra
/// windows "Claude and GPT 5-hour" / "Claude and GPT weekly". Port of upstream CodexBar's
/// AntigravityRemoteUsageFetcher (loadCodeAssist, retrieveUserQuotaSummary, fetchAvailableModels fallback).
/// Differences from upstream: never onboards an account (onboardUser is a write), never persists credentials and never
/// refreshes the token itself: an expired access token has agy renew its own sign-in (<c>agy models</c>, when the
/// RenewGeminiSignIn setting allows) and yields <see cref="NativeUsageStatus.SignInExpired"/> when that does not help. An expired sign-in and transient failures (network, timeout, 5xx, 429) serve the last good reading as cached.
/// </summary>
public sealed class AntigravityNativeClient : INativeUsageClient
{
    public static readonly Uri LoadCodeAssistUri = new("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist");
    public static readonly Uri QuotaSummaryUri = new("https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary");
    public static readonly Uri AvailableModelsUri = new("https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels");
    public const string WindowIdPrefix = "antigravity-quota-summary-";
    public const string GeminiFiveHourId = WindowIdPrefix + "gemini-5h";
    public const string GeminiWeeklyId = WindowIdPrefix + "gemini-weekly";

    private static readonly byte[] LoadCodeAssistBody =
        """{"metadata":{"ideType":"ANTIGRAVITY","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}"""u8.ToArray();
    private static readonly string[] SessionAliases = ["session", "5h", "5-hour", "five hour", "five-hour"];

    private readonly HttpClient _http;
    private readonly ILogger<AntigravityNativeClient> _logger;
    private readonly IAntigravityCredentialSource _source;
    private readonly AntigravityNativeOptions _options;
    private readonly TimeProvider _clock;
    private readonly NativeLastGoodStore _lastGood = new();
    private readonly NativeRateLimitHold _rateLimit = new();
    private readonly IAgySignInRenewer? _renewer;
    private readonly SignInRenewal<AntigravityCredentials> _renewal;

    public AntigravityNativeClient(
        HttpClient http,
        ILogger<AntigravityNativeClient> logger,
        IAntigravityCredentialSource? source = null,
        AntigravityNativeOptions? options = null,
        TimeProvider? clock = null,
        IAgySignInRenewer? renewer = null)
    {
        _http = http;
        _logger = logger;
        _source = source ?? new WindowsCredentialManagerSource();
        _options = options ?? new AntigravityNativeOptions();
        _clock = clock ?? TimeProvider.System;
        _renewer = renewer;
        _renewal = new(_clock, _options.RenewalInterval, credential => credential.Expiry);
        ProviderId = ProviderConstants.ValidateAndNormalize("gemini");
    }

    /// <summary>"gemini": Antigravity quota is shown as the app's Gemini provider.</summary>
    public string ProviderId { get; }

    public async Task<NativeUsageResult> FetchAsync(CancellationToken ct = default) =>
        _lastGood.Apply(await FetchCoreAsync(ct).ConfigureAwait(false), _clock.GetUtcNow());

    private AntigravityCredentials? ReadCredentials()
    {
        using var blob = _source.ReadBlob();
        return blob == null ? null : AntigravityCredentialParser.Parse(blob.Span);
    }

    private Task<AntigravityCredentials?> TryRenewSignInAsync(DateTimeOffset expiry, CancellationToken ct) =>
        _renewer != null && !_options.ShutdownToken.IsCancellationRequested && _options.RenewExpiredSignIn?.Invoke() == true
            ? _renewal.TryRenewAsync(expiry, RenewAndReadAsync, ct)
            : Task.FromResult<AntigravityCredentials?>(null);

    private async Task<AntigravityCredentials?> RenewAndReadAsync()
    {
        if (await RenewAsync().ConfigureAwait(false) is null) return null;
        try
        {
            return ReadCredentials();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            _logger.LogWarning("Antigravity sign-in could not be read after renewal ({Type})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>One renewal attempt. Logs only whether it ran, its exit code and its duration; never its output.</summary>
    private async Task<AgyModelsRun?> RenewAsync()
    {
        try
        {
            if (_renewer!.FindAgyExecutable() is not { } executable)
            {
                _logger.LogInformation("agy was not found; the expired Antigravity sign-in was not renewed");
                return null;
            }
            var run = await _renewer.RunModelsAsync(executable, _options.RenewalTimeout, _options.ShutdownToken).ConfigureAwait(false);
            if (run.TimedOut)
                _logger.LogWarning("Antigravity sign-in renewal timed out after {Seconds:0.0} s and was stopped", run.Duration.TotalSeconds);
            else
                _logger.LogInformation("Antigravity sign-in renewal finished with exit code {ExitCode} in {Seconds:0.0} s", run.ExitCode, run.Duration.TotalSeconds);
            return run;
        }
        catch (OperationCanceledException) when (_options.ShutdownToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Antigravity sign-in renewal could not run ({Type})", ex.GetType().Name);
            return null;
        }
    }

    private async Task<NativeUsageResult> FetchCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow();
        AntigravityCredentials? credentials;
        try
        {
            credentials = ReadCredentials();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // A corrupt credential is a read failure (last good reading kept), not a sign-out.
            _logger.LogWarning("Antigravity credential could not be read ({Type})", ex.GetType().Name);
            return Fail(NativeUsageStatus.Failed, null, "The Antigravity sign-in could not be read.");
        }
        _renewal.ObserveExpiry(credentials?.Expiry);
        if (credentials == null)
            return Fail(NativeUsageStatus.SignInMissing, null, "Not signed in to Antigravity on Windows. Sign in with agy on Windows.");
        // A rate limit holds only for the sign-in it was answered to; a new sign-in is asked again.
        if (_rateLimit.Get(now, credentials.AccessToken) is { } held) return held;

        var calls = 0;
        if (credentials.Expiry is { } expiry && expiry - _options.ExpirySafetyWindow <= now)
        {
            // agy renews its own sign-in when it lists models; the app never writes the credential itself.
            if (await TryRenewSignInAsync(expiry, ct).ConfigureAwait(false) is { } renewed)
            {
                credentials = renewed;
            }
            else
            {
                var ago = _clock.GetUtcNow() - expiry;
                if (ago >= TimeSpan.Zero)
                {
                    _logger.LogInformation("Antigravity access token expired {Ago} ago; not renewed", NativeParse.Ago(ago));
                    return Fail(NativeUsageStatus.SignInExpired, null,
                        $"Antigravity sign-in expired {NativeParse.Ago(ago)} ago. Run agy on Windows to renew it.") with { ExpiredFor = ago };
                }
            }
        }
        var accessToken = credentials.AccessToken;

        try
        {
            // 1. loadCodeAssist: plan and Cloud Code project.
            calls++;
            var (codeStatus, codeAssist, codeRetryAfter) = await PostAsync(_options.LoadCodeAssistUri, accessToken, LoadCodeAssistBody,
                _options.RequestTimeout, NativeJsonContext.Default.CodeAssistResponse, ct).ConfigureAwait(false);
            if (codeStatus != HttpStatusCode.OK || codeAssist == null) return HttpFailure(codeStatus, calls, "loadCodeAssist", codeRetryAfter, accessToken);
            var projectId = credentials.ProjectId ?? codeAssist.ProjectId;
            if (projectId == null && codeAssist.RejectsClientForConsumerTier)
                return Fail(NativeUsageStatus.Unauthorized, 403,
                    "This Antigravity account was signed in with an OAuth client that cannot read its quota. Sign in to it again.") with { NetworkCalls = calls };
            if (projectId == null)
                _logger.LogInformation("Antigravity account has no Cloud Code project; not onboarding (that would change the account)");
            var plan = ResolvePlan(codeAssist, credentials.Email);
            var body = projectId == null ? "{}"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["project"] = projectId },
                NativeJsonDictionaryContext.Default.DictionaryStringString);

            // 2. retrieveUserQuotaSummary (daily host). Any failure other than cancellation falls back, like upstream.
            try
            {
                calls++;
                var (summaryStatus, summary, summaryRetryAfter) = await PostAsync(_options.QuotaSummaryUri, accessToken, body,
                    _options.QuotaSummaryTimeout, NativeJsonContext.Default.QuotaSummaryResponse, ct).ConfigureAwait(false);
                if (summaryStatus is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
                    return HttpFailure(summaryStatus, calls, "retrieveUserQuotaSummary", summaryRetryAfter, accessToken);
                if (summaryStatus == HttpStatusCode.OK && summary is { CodeIsError: false } &&
                    MapQuotaSummary(summary, plan, _clock.GetUtcNow()) is { } mapped)
                    return mapped with { NetworkCalls = calls };
                _logger.LogInformation("Antigravity quota summary unavailable (HTTP {Status}); using fetchAvailableModels", (int)summaryStatus);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogInformation("Antigravity quota summary failed ({Type}); using fetchAvailableModels", ex.GetType().Name);
            }

            // 3. Fallback: per-model quotas.
            calls++;
            var (modelsStatus, models, modelsRetryAfter) = await PostAsync(_options.AvailableModelsUri, accessToken, body,
                _options.RequestTimeout, NativeJsonContext.Default.AvailableModelsResponse, ct).ConfigureAwait(false);
            if (modelsStatus != HttpStatusCode.OK || models == null) return HttpFailure(modelsStatus, calls, "fetchAvailableModels", modelsRetryAfter, accessToken);
            var onboarded = codeAssist.ProjectId != null || NativeParse.NonEmpty(codeAssist.CurrentTier?.Id) != null;
            return (MapAvailableModels(models, plan, _clock.GetUtcNow(), onboarded)
                    ?? Fail(NativeUsageStatus.Failed, 200, "Antigravity reported no usable quota.")) with { NetworkCalls = calls };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Antigravity request timed out");
            return Fail(NativeUsageStatus.Failed, null, "Antigravity usage request timed out.") with { NetworkCalls = calls };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
            or FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogWarning("Antigravity request failed ({Type})", ex.GetType().Name);
            return Fail(NativeUsageStatus.Failed, null, "Antigravity usage request failed (network or response error).") with { NetworkCalls = calls };
        }
    }

    private async Task<(HttpStatusCode Status, T? Body, DateTimeOffset? RetryAfter)> PostAsync<T>(Uri uri, string token, byte[] body, TimeSpan timeout,
        JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        NativeHttp.SetBearer(request, token);
        NativeHttp.SetUserAgent(request, _options.UserAgent);
        using var deadline = NativeHttp.Deadline(ct, timeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, null, response.StatusCode == HttpStatusCode.TooManyRequests
                ? NativeHttp.RetryAfter(response, _clock.GetUtcNow()) : null);
        return (response.StatusCode, await NativeHttp.ReadJsonAsync(response, type, deadline.Token).ConfigureAwait(false), null);
    }

    private NativeUsageResult HttpFailure(HttpStatusCode status, int calls, string step, DateTimeOffset? retryAfter, string accessToken)
    {
        var code = (int)status;
        _logger.LogWarning("Antigravity {Step} answered {Status}", step, code);
        if (status == HttpStatusCode.TooManyRequests)
            return _rateLimit.Remember(
                Fail(NativeUsageStatus.RateLimited, code, "Antigravity quota is rate limited right now.") with { NetworkCalls = calls },
                retryAfter, _clock.GetUtcNow(), accessToken);
        return (status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Fail(NativeUsageStatus.Unauthorized, code,
                $"Antigravity rejected the Windows sign-in (HTTP {code}). Sign in with agy on Windows again."),
            _ => Fail(NativeUsageStatus.Failed, code, $"Antigravity {step} failed (HTTP {code}).")
        }) with { NetworkCalls = calls };
    }

    /// <summary>Port of upstream resolvePlan. Without an id_token, a Google-hosted domain is inferred from the stored email.</summary>
    public static string? ResolvePlan(CodeAssistResponse response, string? email)
    {
        if (response.PaidTier is { } paid && (NativeParse.NonEmpty(paid.Name) != null || NativeParse.NonEmpty(paid.Id) != null))
            return NativeParse.NonEmpty(paid.Name) ?? "Paid";
        if (NativeParse.NonEmpty(response.PlanInfo?.PlanType) is { } planType) return planType;
        var consumerEmail = email == null || email.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase) ||
                            email.EndsWith("@googlemail.com", StringComparison.OrdinalIgnoreCase);
        return NativeParse.NonEmpty(response.CurrentTier?.Id) switch
        {
            "standard-tier" => "Paid",
            "free-tier" => consumerEmail ? "Free" : "Workspace",
            "legacy-tier" => "Legacy",
            _ => NativeParse.NonEmpty(response.CurrentTier?.Name)
        };
    }

    private sealed record NamedWindow(string Id, string Title, NativeUsageWindow? Window);

    /// <summary>
    /// Builds upstream's quota-summary windows, then applies the WSL shim's Gemini reshaping. Returns null when no
    /// bucket has a known reading. Disabled buckets and buckets without a remaining fraction carry no reading.
    /// </summary>
    public static NativeUsageResult? MapQuotaSummary(QuotaSummaryResponse response, string? plan, DateTimeOffset now)
    {
        var named = new List<NamedWindow>();
        var groups = (response.ResolvedGroups ?? []).Where(g => g?.Buckets?.Count > 0).Select(g => g!)
            .OrderBy(g => GroupTitle(g) switch { "Gemini" => 0, "Claude/GPT" => 1, _ => 2 });
        foreach (var group in groups)
        {
            var groupTitle = GroupTitle(group);
            foreach (var bucket in group.Buckets!.Where(b => NativeParse.NonEmpty(b?.BucketId ?? b?.Id) != null).Select(b => b!)
                         .OrderBy(b => BucketKind(b) switch { "session" => 0, "weekly" => 1, _ => 2 }))
            {
                var id = NativeParse.NonEmpty(bucket.BucketId ?? bucket.Id)!;
                var kind = BucketKind(bucket);
                var title = $"{groupTitle} {kind switch { "session" => "5-hour", "weekly" => "weekly", _ => NativeParse.NonEmpty(bucket.DisplayName ?? bucket.Name) ?? id }}";
                var remaining = bucket.RemainingFraction ?? bucket.Remaining?.Fraction;
                NativeUsageWindow? window = bucket.Disabled == true || remaining is not { } fraction || !double.IsFinite(fraction) ? null :
                    new(100 - Math.Clamp(fraction * 100, 0, 100), kind switch { "session" => 300, "weekly" => 10080, _ => 0 },
                        ParseResetTime(bucket.ResetTime), bucket.Description);
                named.Add(new(WindowIdPrefix + id, title, window));
            }
        }
        if (!named.Any(n => n.Window != null)) return null;

        var known = named.Where(n => n.Window != null).ToList();
        var byId = new Dictionary<string, NamedWindow>(StringComparer.Ordinal);
        foreach (var n in named) byId[n.Id] = n;

        // Upstream's representatives, then the shim's override with Gemini's own 5-hour and weekly windows.
        var primary = byId.GetValueOrDefault(GeminiFiveHourId)?.Window ?? Representative(known, "gemini")?.Window;
        var secondary = byId.GetValueOrDefault(GeminiWeeklyId);
        if (secondary?.Window == null) secondary = Representative(known, "claude-gpt");

        var extraIds = new HashSet<string>(StringComparer.Ordinal);
        var extras = known
            .Where(n => n.Id != secondary?.Id &&
                !n.Id.StartsWith(WindowIdPrefix + "gemini", StringComparison.Ordinal) && extraIds.Add(n.Id))
            .Select(n => new NativeExtraWindow(n.Id, n.Id switch
            {
                WindowIdPrefix + "3p-5h" => "Claude and GPT 5-hour",
                WindowIdPrefix + "3p-weekly" => "Claude and GPT weekly",
                _ => n.Title
            }, n.Window!))
            .ToList();

        // Upstream drops the plan label for free accounts that report a 5-hour window.
        if (string.Equals(plan, "free", StringComparison.OrdinalIgnoreCase) && known.Any(n => n.Window!.WindowMinutes == 300)) plan = null;
        return new NativeUsageResult
        {
            Provider = "gemini",
            Source = "remote",
            Status = NativeUsageStatus.Ok,
            LoginMethod = plan,
            Primary = WithResetsIn(primary, now),
            Secondary = WithResetsIn(secondary?.Window, now),
            ExtraWindows = extras,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Simplified fallback when the quota summary is unavailable: the most used Gemini model as primary and the most
    /// used Claude/GPT model as secondary. Upstream's model normalization is richer; see the spike report.
    /// </summary>
    public static NativeUsageResult? MapAvailableModels(AvailableModelsResponse response, string? plan, DateTimeOffset now,
        bool onboarded = false)
    {
        var quotas = (response.Models ?? [])
            .Where(m => m.Value?.QuotaInfo?.RemainingFraction is { } f && double.IsFinite(f))
            .Where(m => !m.Key.Contains("image", StringComparison.OrdinalIgnoreCase) && !m.Key.Contains("lite", StringComparison.OrdinalIgnoreCase) &&
                        !m.Key.Contains("autocomplete", StringComparison.OrdinalIgnoreCase) && !m.Key.Contains("tab_", StringComparison.OrdinalIgnoreCase))
            .Select(m => (Id: m.Key, Fraction: m.Value!.QuotaInfo!.RemainingFraction!.Value, Reset: ParseResetTime(m.Value.QuotaInfo.ResetTime)))
            .ToList();
        // An unonboarded client can receive placeholder quotas at 100% remaining.
        if (quotas.Count == 0 || (!onboarded && quotas.All(q => q.Fraction >= 0.999))) return null;
        NativeUsageWindow? Pick(Func<string, bool> family) => quotas.Where(q => family(q.Id.ToLowerInvariant()))
            .OrderBy(q => q.Fraction).Select(q => new NativeUsageWindow(100 - Math.Clamp(q.Fraction * 100, 0, 100), 0, q.Reset, null))
            .FirstOrDefault();
        var primary = WithResetsIn(Pick(id => id.Contains("gemini")), now);
        var secondary = WithResetsIn(Pick(id => id.Contains("claude") || id.Contains("gpt")), now);
        if (primary == null && secondary == null) return null;
        return new NativeUsageResult
        {
            Provider = "gemini", Source = "remote", Status = NativeUsageStatus.Ok, LoginMethod = plan,
            Primary = primary, Secondary = secondary, UpdatedAt = now
        };
    }

    private static NativeUsageWindow? WithResetsIn(NativeUsageWindow? window, DateTimeOffset now) =>
        window?.ResetsAt is { } reset ? window with { ResetDescription = NativeParse.ResetsInDescription(reset, now) } : window;

    private static NamedWindow? Representative(List<NamedWindow> known, string family)
    {
        var members = known.Where(n => Family(n) == family).ToList();
        // Prefer the family's standard session and weekly buckets over specialized quotas.
        string[] prefixes = family == "gemini" ? ["gemini-"] : ["3p-", "third-party-"];
        var ordinaryIds = prefixes.SelectMany(prefix => SessionAliases.Append("weekly")
            .Select(alias => WindowIdPrefix + prefix + alias)).ToHashSet(StringComparer.Ordinal);
        var ordinary = members.Where(n =>
        {
            var id = n.Id.ToLowerInvariant().Replace('_', '-');
            if (id.EndsWith(" limit", StringComparison.Ordinal)) id = id[..^6];
            return ordinaryIds.Contains(id);
        }).ToList();
        return (ordinary.Count > 0 ? ordinary : members)
            .OrderByDescending(n => n.Window!.UsedPercent)
            .ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? Family(NamedWindow window)
    {
        var id = window.Id.ToLowerInvariant();
        if (id.Contains("gemini")) return "gemini";
        if (id.Contains("3p") || id.Contains("third-party")) return "claude-gpt";
        var title = window.Title.ToLowerInvariant();
        if (title.Contains("gemini")) return "gemini";
        return title.Contains("claude") || title.Contains("gpt") ? "claude-gpt" : null;
    }

    private static string GroupTitle(QuotaSummaryGroup group)
    {
        var title = (group.DisplayName ?? group.Name ?? string.Empty).Trim();
        var lower = title.ToLowerInvariant();
        // A versioned group such as "Gemini 4 Argon" is a separate quota and keeps its own name.
        if (lower.Contains("gemini") && !title.Any(char.IsDigit)) return "Gemini";
        if (lower.Contains("claude") || lower.Contains("gpt")) return "Claude/GPT";
        return title.Length == 0 ? "Quota" : title;
    }

    /// <summary>"session", "weekly" or "other", from the explicit window or else the bucket id and name.</summary>
    private static string BucketKind(QuotaSummaryBucket bucket)
    {
        var explicitWindow = NativeParse.NonEmpty(bucket.Window);
        var values = explicitWindow != null ? [explicitWindow] : new[] { bucket.BucketId ?? bucket.Id, bucket.DisplayName ?? bucket.Name };
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            var normalized = raw?.Trim().ToLowerInvariant().Replace('_', '-');
            if (string.IsNullOrEmpty(normalized)) continue;
            var forms = normalized.EndsWith(" limit", StringComparison.Ordinal) ? new[] { normalized, normalized[..^6] } : [normalized];
            foreach (var form in forms)
            {
                candidates.Add(form);
                foreach (var alias in SessionAliases.Append("weekly"))
                    if (form.EndsWith("-" + alias, StringComparison.Ordinal)) candidates.Add(alias);
            }
        }
        if (SessionAliases.Any(candidates.Contains)) return "session";
        return candidates.Contains("weekly") ? "weekly" : "other";
    }

    /// <summary>ISO 8601, or epoch seconds as upstream's parseDate accepts.</summary>
    private static DateTimeOffset? ParseResetTime(string? value) =>
        NativeParse.ParseTimestamp(value) ??
        (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
         seconds is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : null);

    private static NativeUsageResult Fail(NativeUsageStatus status, int? http, string message) =>
        new() { Provider = "gemini", Source = "remote", Status = status, HttpStatus = http, Message = message };
}

/// <summary>Source-generated serializer for the small request bodies.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class NativeJsonDictionaryContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
