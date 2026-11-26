using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

/// <summary>
/// Tests, saves and removes the Anthropic Admin key (see <see cref="IAdminKeyService"/>). Storage stays the Windows user
/// environment variable that <see cref="ApiBillingService"/> already reads; nothing is written anywhere else.
/// Logs and results never carry the key: exceptions are logged by type only.
/// </summary>
public sealed class AdminKeyService : IAdminKeyService
{
    /// <summary>
    /// Anthropic's key family. Working Admin keys come in more forms than the docs show (sk-ant-admin01-, sk-ant-usr-),
    /// so the subtype is left to the one Admin API request, which rejects a key that cannot read the organization.
    /// </summary>
    internal const string KeyPrefix = "sk-ant-";
    internal const int MinKeyLength = 32;
    internal const int MaxKeyLength = 512;
    internal const string TestUrl = "https://api.anthropic.com/v1/organizations/api_keys?limit=1";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly string UserAgent = "AgentsWinBar/" + (typeof(AdminKeyService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AdminKeyService).Assembly.GetName().Version?.ToString() ?? "1.0");

    private readonly ILogger<AdminKeyService> _logger;
    private readonly Func<string, EnvironmentVariableTarget, string?> _readEnvironment;
    private readonly Action<string, string?, EnvironmentVariableTarget> _writeEnvironment;
    private readonly HttpMessageHandler? _handler;

    public event EventHandler? KeyChanged;

    /// <param name="environmentReader">Reads a variable (defaults to <see cref="Environment.GetEnvironmentVariable(string, EnvironmentVariableTarget)"/>).</param>
    /// <param name="environmentWriter">Sets or (with null) deletes a variable (defaults to <see cref="Environment.SetEnvironmentVariable(string, string?, EnvironmentVariableTarget)"/>).</param>
    /// <param name="handler">For tests; by default each check uses its own handler that never follows redirects.</param>
    public AdminKeyService(ILogger<AdminKeyService> logger,
        Func<string, EnvironmentVariableTarget, string?>? environmentReader = null,
        Action<string, string?, EnvironmentVariableTarget>? environmentWriter = null,
        HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _readEnvironment = environmentReader ?? Environment.GetEnvironmentVariable;
        _writeEnvironment = environmentWriter ?? Environment.SetEnvironmentVariable;
        _handler = handler;
    }

    /// <summary>The trimmed key when it looks like an Anthropic key (prefix, printable ASCII without spaces, sane length); otherwise null.</summary>
    internal static string? Normalize(string? candidate)
    {
        var key = candidate?.Trim();
        if (key == null || key.Length is < MinKeyLength or > MaxKeyLength ||
            !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return null;
        foreach (var c in key)
            if (c is < '!' or > '~') return null;
        return key;
    }

    /// <summary>The last 4 characters, only for a key long enough that they reveal little and only when they are plain characters.</summary>
    internal static string? LastFour(string key)
    {
        if (key.Length < MinKeyLength) return null;
        var tail = key[^4..];
        foreach (var c in tail)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) return null;
        return tail;
    }

    public AdminKeyStatus GetStatus()
    {
        var key = ReadKey();
        return key == null ? new AdminKeyStatus(false, null) : new AdminKeyStatus(true, LastFour(key));
    }

    public Task<AdminKeyCheck> TestAsync(string? candidate, CancellationToken ct = default)
    {
        var key = Normalize(candidate);
        return key == null ? Task.FromResult(AdminKeyCheck.BadFormat) : SendTestAsync(key, ct);
    }

    public Task<AdminKeyCheck> TestSavedAsync(CancellationToken ct = default) => TestAsync(ReadKey(), ct);

    public async Task<bool> SaveAsync(string? candidate)
    {
        // "Save anyway" skips only the network check, never the format check.
        var key = Normalize(candidate);
        if (key == null) return false;
        if (!await WriteAsync(key).ConfigureAwait(false)) return false;
        _logger.LogInformation("Anthropic Admin key saved");
        RaiseKeyChanged();
        return true;
    }

    public async Task<bool> RemoveAsync()
    {
        if (!await WriteAsync(null).ConfigureAwait(false)) return false;
        _logger.LogInformation("Anthropic Admin key removed");
        RaiseKeyChanged();
        return true;
    }

    /// <summary>Process first, then the user environment: the order <see cref="ApiBillingService"/> reads them in.</summary>
    private string? ReadKey()
    {
        try
        {
            var key = _readEnvironment(ApiBillingService.AdminKeyEnvironmentVariable, EnvironmentVariableTarget.Process);
            if (string.IsNullOrWhiteSpace(key))
                key = _readEnvironment(ApiBillingService.AdminKeyEnvironmentVariable, EnvironmentVariableTarget.User);
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Reading the Anthropic Admin key variable failed ({Type})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>One read-only Admin API request with the same conventions as <see cref="ApiBillingService"/>.</summary>
    private async Task<AdminKeyCheck> SendTestAsync(string key, CancellationToken ct)
    {
        try
        {
            // Never forward an admin credential to a redirect destination.
            using var http = new HttpClient(_handler ?? new HttpClientHandler { AllowAutoRedirect = false },
                disposeHandler: _handler == null) { Timeout = RequestTimeout };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, TestUrl);
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            // Only the status matters; the body is untrusted and never read.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return AdminKeyCheck.Valid;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogInformation("Anthropic rejected the Admin key (HTTP {Status})", status);
                return AdminKeyCheck.Rejected;
            }
            _logger.LogInformation("Anthropic Admin key check got no answer (HTTP {Status})", status);
            return AdminKeyCheck.Unreachable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Transport exceptions can carry request details. Only the type is logged.
            _logger.LogWarning("Anthropic Admin key check failed or timed out ({Type})", ex.GetType().Name);
            return AdminKeyCheck.Unreachable;
        }
    }

    /// <summary>
    /// The user variable keeps the key across restarts; the process copy lets this running app use it at once
    /// (<see cref="ApiBillingService"/> reads the process first). Setting a user variable broadcasts WM_SETTINGCHANGE,
    /// which can take seconds, so both writes run on the thread pool, never on the caller's (UI) thread.
    /// </summary>
    private Task<bool> WriteAsync(string? value) => Task.Run(() =>
    {
        try
        {
            _writeEnvironment(ApiBillingService.AdminKeyEnvironmentVariable, value, EnvironmentVariableTarget.User);
            _writeEnvironment(ApiBillingService.AdminKeyEnvironmentVariable, value, EnvironmentVariableTarget.Process);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Writing the Anthropic Admin key variable failed ({Type})", ex.GetType().Name);
            return false;
        }
    });

    private void RaiseKeyChanged()
    {
        try { KeyChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogWarning("Admin key change handler failed ({Type})", ex.GetType().Name); }
    }
}
