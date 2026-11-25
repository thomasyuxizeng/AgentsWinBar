using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services.Native;

/// <summary>A refreshed access token held in memory only.</summary>
public sealed record AntigravityRefreshedToken(string AccessToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"AntigravityRefreshedToken {{ ExpiresAt = {ExpiresAt:o} }}";
}

/// <summary>
/// Upstream's Antigravity refresh path, kept in memory: exchanges agy's refresh token for a new access token at
/// Google's token endpoint with the OAuth client the installed Antigravity binary carries. Nothing is persisted:
/// Credential Manager is never written and the refresh token is reused unchanged. Only used when
/// <see cref="AntigravityNativeOptions.AllowInMemoryRefresh"/> is on (off by default).
/// </summary>
public sealed class AntigravityTokenRefresher
{
    public static readonly Uri TokenUri = new("https://oauth2.googleapis.com/token");

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Func<AntigravityOAuthClient?> _clientResolver;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private Lazy<AntigravityOAuthClient?>? _resolved;

    public AntigravityTokenRefresher(HttpClient http, ILogger logger, Func<AntigravityOAuthClient?>? clientResolver = null, TimeProvider? clock = null)
    {
        _http = http;
        _logger = logger;
        _clientResolver = clientResolver ?? (() => AntigravityOAuthClientDiscovery.Discover());
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Refreshes in memory. Returns null when no client is found or Google refuses the refresh.</summary>
    public async Task<AntigravityRefreshedToken?> RefreshAsync(AntigravityCredentials credentials, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credentials.RefreshToken)) return null;
        var client = credentials is { ClientId: { } id, ClientSecret: { } secret } ? new AntigravityOAuthClient(id, secret) : ResolveClient();
        if (client == null)
        {
            _logger.LogWarning("No Antigravity OAuth client found in the installed binaries; cannot refresh");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri)
        {
            Content = new FormUrlEncodedContent(
            [
                new("client_id", client.ClientId),
                new("client_secret", client.ClientSecret),
                new("refresh_token", credentials.RefreshToken),
                new("grant_type", "refresh_token")
            ])
        };
        using var deadline = NativeHttp.Deadline(ct, timeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            _logger.LogWarning("Antigravity token refresh answered {Status}", (int)response.StatusCode);
            return null;
        }
        GoogleTokenResponse? body;
        await using (var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            body = await JsonSerializer.DeserializeAsync(stream, NativeJsonContext.Default.GoogleTokenResponse, deadline.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body?.AccessToken)) return null;
        var lifetime = body.ExpiresIn is > 0 and < 86400 * 7 ? TimeSpan.FromSeconds(body.ExpiresIn.Value) : TimeSpan.FromMinutes(55);
        return new(body.AccessToken.Trim(), _clock.GetUtcNow() + lifetime);
    }

    private AntigravityOAuthClient? ResolveClient()
    {
        Lazy<AntigravityOAuthClient?> resolved;
        lock (_gate) resolved = _resolved ??= new(_clientResolver);
        return resolved.Value;
    }
}
