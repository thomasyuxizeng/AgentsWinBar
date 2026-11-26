using System.Net;
using System.Text.Json;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services.Native;

/// <summary>The fields of the Codex CLI's auth.json this reader uses. The access token never leaves this process.</summary>
public sealed record CodexCredentials(string AccessToken, string? AccountId, DateTimeOffset? ExpiresAt, DateTimeOffset? LastRefresh)
{
    /// <summary>The generated ToString would print <see cref="AccessToken"/>; this one prints no token or account value.</summary>
    public override string ToString() => $"CodexCredentials {{ ExpiresAt = {ExpiresAt:o}, LastRefresh = {LastRefresh:o} }}";
}

/// <summary>What auth.json holds: a ChatGPT sign-in, only an API key, or nothing usable.</summary>
public enum CodexCredentialKind { Missing, ApiKeyOnly, ChatGpt }

public sealed record CodexNativeOptions
{
    /// <summary>
    /// Defaults to %USERPROFILE%\.codex\auth.json: the default profile only, CODEX_HOME is not honored.
    /// A path set here always wins.
    /// </summary>
    public string? AuthPath { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Upstream identifies itself as "CodexBar"; kept for parity with the working WSL route.</summary>
    public string UserAgent { get; init; } = "CodexBar";
    /// <summary>The usage endpoint (a Debug build's test hooks point it at the fake provider server).</summary>
    public Uri UsageUri { get; init; } = CodexNativeClient.UsageUri;

    public string ResolveAuthPath()
    {
        if (!string.IsNullOrWhiteSpace(AuthPath)) return AuthPath;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
    }
}

/// <summary>
/// Reads Codex plan usage from ChatGPT's wham/usage endpoint with the Windows Codex CLI sign-in, read-only.
/// Port of upstream CodexBar's CodexOAuthUsageFetcher (usage call only) and its window normalizer.
/// Never refreshes or writes the token: an expired token yields <see cref="NativeUsageStatus.SignInExpired"/>. An
/// expired sign-in and transient failures (network, timeout, 5xx, 429) serve the last good reading as cached.
/// </summary>
public sealed class CodexNativeClient : INativeUsageClient
{
    public static readonly Uri UsageUri = new("https://chatgpt.com/backend-api/wham/usage");

    private readonly HttpClient _http;
    private readonly ILogger<CodexNativeClient> _logger;
    private readonly CodexNativeOptions _options;
    private readonly TimeProvider _clock;
    private readonly NativeLastGoodStore _lastGood = new();

    public CodexNativeClient(HttpClient http, ILogger<CodexNativeClient> logger, CodexNativeOptions? options = null, TimeProvider? clock = null)
    {
        _http = http;
        _logger = logger;
        _options = options ?? new CodexNativeOptions();
        _clock = clock ?? TimeProvider.System;
        ProviderId = ProviderConstants.ValidateAndNormalize("codex");
    }

    public string ProviderId { get; }

    public async Task<NativeUsageResult> FetchAsync(CancellationToken ct = default) =>
        _lastGood.Apply(await FetchCoreAsync(ct).ConfigureAwait(false), _clock.GetUtcNow());

    private async Task<NativeUsageResult> FetchCoreAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        (CodexCredentialKind Kind, CodexCredentials? Credentials) read;
        try
        {
            read = await ReadCredentialsAsync(_options.ResolveAuthPath(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            _logger.LogWarning("Codex auth.json could not be read ({Type})", ex.GetType().Name);
            return Fail(NativeUsageStatus.Failed, null, "The Codex sign-in file could not be read.");
        }
        if (read.Credentials is not { } credentials)
            return Fail(NativeUsageStatus.SignInMissing, null, read.Kind == CodexCredentialKind.ApiKeyOnly
                ? "Codex on Windows is signed in with an API key; plan limits need a ChatGPT sign-in (codex login)."
                : "Not signed in to Codex on Windows. Run codex login on Windows.");
        if (credentials.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            var ago = now - expiresAt;
            _logger.LogInformation("Codex access token expired {Ago} ago; not refreshing (read-only)", NativeParse.Ago(ago));
            return Fail(NativeUsageStatus.SignInExpired, null,
                $"Codex sign-in expired {NativeParse.Ago(ago)} ago. Run Codex on Windows to renew it.") with { ExpiredFor = ago };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.UsageUri);
            NativeHttp.SetBearer(request, credentials.AccessToken);
            request.Headers.Accept.ParseAdd("application/json");
            NativeHttp.SetUserAgent(request, _options.UserAgent);
            if (!string.IsNullOrEmpty(credentials.AccountId)) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
            using var deadline = NativeHttp.Deadline(ct, _options.RequestTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await NativeHttp.ReadJsonAsync(response, NativeJsonContext.Default.CodexUsageResponse, deadline.Token).ConfigureAwait(false);
                var result = body == null ? null : Map(body, _clock.GetUtcNow());
                return result == null
                    ? Fail(NativeUsageStatus.Failed, status, "Codex usage response had no usage windows.") with { NetworkCalls = 1 }
                    : result with { NetworkCalls = 1 };
            }
            _logger.LogWarning("Codex usage endpoint answered {Status}", status);
            return (response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Fail(NativeUsageStatus.Unauthorized, status,
                    $"Codex rejected the Windows sign-in (HTTP {status}). Run codex login on Windows."),
                HttpStatusCode.TooManyRequests => Fail(NativeUsageStatus.RateLimited, status, "Codex usage is rate limited right now.")
                    with { RetryAfter = NativeHttp.RetryAfter(response, _clock.GetUtcNow()) },
                _ => Fail(NativeUsageStatus.Failed, status, $"Codex usage request failed (HTTP {status}).")
            }) with { NetworkCalls = 1 };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Codex usage request timed out");
            return Fail(NativeUsageStatus.Failed, null, "Codex usage request timed out.") with { NetworkCalls = 1 };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
            or FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogWarning("Codex usage request failed ({Type})", ex.GetType().Name);
            return Fail(NativeUsageStatus.Failed, null, "Codex usage request failed (network or response error).") with { NetworkCalls = 1 };
        }
    }

    public static async Task<(CodexCredentialKind Kind, CodexCredentials? Credentials)> ReadCredentialsAsync(string path, CancellationToken ct)
    {
        using var buffer = await NativeCredentialFile.ReadAsync(path, ct).ConfigureAwait(false);
        return buffer == null ? (CodexCredentialKind.Missing, null) : ParseCredentials(buffer.Span);
    }

    /// <summary>
    /// Reads tokens.access_token, tokens.account_id (or the id/access token's ChatGPT account claim), last_refresh
    /// and the access token's JWT "exp". The refresh token is skipped unread.
    /// </summary>
    public static (CodexCredentialKind Kind, CodexCredentials? Credentials) ParseCredentials(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(NativeCredentialFile.WithoutBom(json), NativeCredentialFile.ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return (CodexCredentialKind.Missing, null);
        string? access = null, accountId = null, idToken = null;
        DateTimeOffset? lastRefresh = null;
        var hasApiKey = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("tokens"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("access_token"u8) || reader.ValueTextEquals("accessToken"u8)) { reader.Read(); access = String(ref reader); }
                    else if (reader.ValueTextEquals("account_id"u8) || reader.ValueTextEquals("accountId"u8)) { reader.Read(); accountId = String(ref reader); }
                    else if (reader.ValueTextEquals("id_token"u8) || reader.ValueTextEquals("idToken"u8)) { reader.Read(); idToken = String(ref reader); }
                    else reader.Read();
                    reader.Skip(); // steps over an object or array value whole
                }
            }
            else if (reader.ValueTextEquals("last_refresh"u8)) { reader.Read(); lastRefresh = NativeParse.ParseTimestamp(String(ref reader)); reader.Skip(); }
            else if (reader.ValueTextEquals("OPENAI_API_KEY"u8))
            {
                reader.Read();
                // Only note presence; the key itself is never materialized.
                hasApiKey = reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 0;
                reader.Skip();
            }
            else { reader.Read(); reader.Skip(); }
        }
        if (string.IsNullOrEmpty(access)) return (hasApiKey ? CodexCredentialKind.ApiKeyOnly : CodexCredentialKind.Missing, null);
        NativeHttp.ValidateBearerToken(access);
        accountId = NativeParse.NonEmpty(accountId) ?? NativeJwt.ReadChatGptAccountId(idToken) ?? NativeJwt.ReadChatGptAccountId(access);
        return (CodexCredentialKind.ChatGpt, new CodexCredentials(access, accountId, NativeJwt.ReadExpiry(access), lastRefresh));

        static string? String(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.String ? r.GetString() : null;
    }

    /// <summary>Maps wham/usage like upstream: windows by length (300 = session, 10080 = weekly), extra limits as extra windows.</summary>
    public static NativeUsageResult? Map(CodexUsageResponse usage, DateTimeOffset now)
    {
        var first = Window(usage.RateLimit?.PrimaryWindow);
        var second = Window(usage.RateLimit?.SecondaryWindow);
        var (primary, secondary) = Normalize(first, second);
        var extras = ExtraWindows(usage.AdditionalRateLimits);
        if (primary == null && secondary == null && extras.Count == 0) return null;
        return new NativeUsageResult
        {
            Provider = "codex",
            Source = "oauth",
            Status = NativeUsageStatus.Ok,
            // Raw plan type ("team", "plus"), as the CLI route the app uses today reports it.
            LoginMethod = NativeParse.NonEmpty(usage.PlanType),
            Primary = primary,
            Secondary = secondary,
            ExtraWindows = extras,
            UpdatedAt = now
        };
    }

    /// <summary>Port of upstream CodexRateWindowNormalizer.</summary>
    public static (NativeUsageWindow? Primary, NativeUsageWindow? Secondary) Normalize(NativeUsageWindow? first, NativeUsageWindow? second)
    {
        static int Role(NativeUsageWindow w) => w.WindowMinutes switch { 300 => 1, 10080 => 2, _ => 0 }; // 1 session, 2 weekly
        return (first, second) switch
        {
            ({ } a, { } b) => (Role(a), Role(b)) switch
            {
                (2, 1) or (2, 0) => (b, a),
                _ => (a, b)
            },
            ({ } a, null) => Role(a) == 2 ? (null, a) : (a, null),
            (null, { } b) => Role(b) == 2 ? (null, b) : (b, null),
            _ => (null, null)
        };
    }

    private static NativeUsageWindow? Window(CodexWindow? window)
    {
        if (window?.UsedPercent is not { } used || !double.IsFinite(used)) return null;
        var minutes = window.LimitWindowSeconds is long seconds and > 0 ? (int)Math.Min(seconds / 60, int.MaxValue) : 0;
        DateTimeOffset? resets = window.ResetAt is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(window.ResetAt.Value) : null;
        return new(used, minutes, resets, null);
    }

    /// <summary>Port of upstream CodexAdditionalRateLimitMapper (Spark windows get stable ids and titles).</summary>
    private static List<NativeExtraWindow> ExtraWindows(List<CodexAdditionalRateLimit?>? limits)
    {
        var result = new List<NativeExtraWindow>();
        if (limits == null) return result;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in limits)
        {
            if (entry == null) continue;
            var name = NativeParse.NonEmpty(entry.LimitName);
            var feature = NativeParse.NonEmpty(entry.MeteredFeature);
            var isSpark = (name ?? string.Empty).Contains("spark", StringComparison.OrdinalIgnoreCase) ||
                          (feature ?? string.Empty).Contains("spark", StringComparison.OrdinalIgnoreCase);
            if (isSpark)
            {
                foreach (var (snapshot, fallbackWeekly) in new[] { (entry.RateLimit?.PrimaryWindow, false), (entry.RateLimit?.SecondaryWindow, true) })
                {
                    if (Window(snapshot) is not { } w) continue;
                    var weekly = w.WindowMinutes is > 0 and <= 360 ? false : w.WindowMinutes >= 6 * 24 * 60 || fallbackWeekly;
                    var (id, title) = weekly ? ("codex-spark-weekly", "Codex Spark Weekly") : ("codex-spark", "Codex Spark 5-hour");
                    if (used.Add(id)) result.Add(new(id, title, w));
                }
                continue;
            }
            var window = Window(entry.RateLimit?.PrimaryWindow) ?? Window(entry.RateLimit?.SecondaryWindow);
            var source = feature ?? name;
            if (window == null || source == null) continue;
            var slug = NativeParse.Slug(source);
            if (slug.Length == 0 || !used.Add("codex-" + slug)) continue;
            result.Add(new("codex-" + slug, name ?? feature ?? "Codex extra limit", window));
        }
        return result;
    }

    private static NativeUsageResult Fail(NativeUsageStatus status, int? http, string message) =>
        new() { Provider = "codex", Source = "oauth", Status = status, HttpStatus = http, Message = message };
}
