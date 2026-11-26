using System.Text.Json;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services.Native;

/// <summary>Reads usage natively and returns it as the JSON the CodexBar CLI would have printed.</summary>
public interface INativeUsageService
{
    /// <summary>
    /// Reads one provider and returns the CLI's array shape (<c>[{"provider": ...}]</c>) for it. Provider, network
    /// and sign-in failures come back inside the JSON as the CLI's error object, never as exceptions.
    /// </summary>
    /// <exception cref="ArgumentException">The provider id is not an allowed provider.</exception>
    Task<string> FetchUsageJsonAsync(string provider, CancellationToken ct = default);

    /// <summary>Reads the given providers in parallel; keyed by normalized provider id.</summary>
    Task<IReadOnlyDictionary<string, string>> FetchAllUsageJsonAsync(IReadOnlyList<string> providers, CancellationToken ct = default);
}

/// <summary>Direct typed DTO route avoiding JSON serialize-then-deserialize roundtrips in-process.</summary>
public interface INativeTypedUsageService : INativeUsageService
{
    Task<UsageDataDto> FetchUsageDtoAsync(string provider, CancellationToken ct = default);
}

/// <summary>
/// Owns the shared <see cref="HttpClient"/> from <see cref="NativeHttp"/> and the three native readers
/// ("gemini" is read through the Antigravity client).
/// </summary>
public sealed class NativeUsageService : INativeTypedUsageService, IDisposable
{
    /// <summary>
    /// Claude's last good reading is served for this long. At a 300 s refresh interval the rate-limited endpoint
    /// is then called every second refresh (every 10 minutes, like the WSL shim) instead of every third.
    /// </summary>
    public static readonly TimeSpan ClaudeCacheLifetime = TimeSpan.FromMinutes(9.5);

    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();
    private readonly HttpClient? _ownedHttp;
    private readonly IReadOnlyDictionary<string, INativeUsageClient> _clients;
    private readonly ILogger<NativeUsageService> _logger;

    /// <summary>
    /// Production constructor: one pooled HttpClient and the three Windows-sign-in readers.
    /// <paramref name="renewClaudeSignIn"/> is asked whenever Claude's sign-in is found expired (the RenewClaudeSignIn
    /// setting); null never renews. <paramref name="renewGeminiSignIn"/> does the same for agy's sign-in (the
    /// RenewGeminiSignIn setting). <paramref name="testOverrides"/> (<see cref="NativeTestOverrides.Current"/>, always
    /// null in a Release build) points the readers at a fake profile, credential and server.
    /// </summary>
    public NativeUsageService(ILoggerFactory loggerFactory, Func<bool>? renewClaudeSignIn = null,
        NativeTestOverrides? testOverrides = null, Func<bool>? renewGeminiSignIn = null, CancellationToken shutdownToken = default)
    {
        _logger = loggerFactory.CreateLogger<NativeUsageService>();
        _ownedHttp = testOverrides?.CreateHttpClient() ?? NativeHttp.CreateClient();
        var claude = CreateClaudeOptions() with { RenewExpiredSignIn = renewClaudeSignIn, ShutdownToken = shutdownToken };
        var codex = new CodexNativeOptions();
        var antigravity = new AntigravityNativeOptions { RenewExpiredSignIn = renewGeminiSignIn, ShutdownToken = shutdownToken };
        if (testOverrides is not null)
            (claude, codex, antigravity) = (testOverrides.Apply(claude), testOverrides.Apply(codex), testOverrides.Apply(antigravity));
        _clients = Index([
            new ClaudeNativeClient(_ownedHttp, loggerFactory.CreateLogger<ClaudeNativeClient>(), claude,
                cooldownStore: new ClaudeCooldownFile(loggerFactory.CreateLogger<ClaudeCooldownFile>(), testOverrides?.CooldownFilePath),
                // A fake profile never starts the real Claude Code, which would renew the real sign-in.
                renewer: testOverrides?.ProfileRoot is null ? new ClaudeAuthStatusRunner() : null),
            new CodexNativeClient(_ownedHttp, loggerFactory.CreateLogger<CodexNativeClient>(), codex),
            new AntigravityNativeClient(_ownedHttp, loggerFactory.CreateLogger<AntigravityNativeClient>(),
                testOverrides?.CreateAntigravitySource(), antigravity,
                // A fake credential never starts the real agy, which would renew the real sign-in.
                renewer: testOverrides?.AntigravityCredentialFile is null ? new AgyModelsRunner() : null)
        ]);
        if (testOverrides is not null)
            _logger.LogWarning("Native usage readers are using the Debug test overrides (fake profile, credential or server)");
    }

    /// <summary>Test constructor: the given readers, keyed by their provider id.</summary>
    public NativeUsageService(IEnumerable<INativeUsageClient> clients, ILogger<NativeUsageService> logger)
    {
        _logger = logger;
        _clients = Index(clients);
    }

    /// <summary>Options the app constructs the Claude reader with.</summary>
    public static ClaudeNativeOptions CreateClaudeOptions() => new() { CacheLifetime = ClaudeCacheLifetime };

    public async Task<UsageDataDto> FetchUsageDtoAsync(string provider, CancellationToken ct = default)
    {
        var normalized = ProviderConstants.ValidateAndNormalize(provider);
        NativeUsageResult result;
        if (!_clients.TryGetValue(normalized, out var client))
        {
            result = Failed(normalized, "Native usage reading is not available for this provider.");
        }
        else
        {
            try
            {
                result = await client.FetchAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // The readers report failures as statuses; this only guards against a bug in one of them.
                _logger.LogWarning("Native usage read for {Provider} failed unexpectedly ({Type})", normalized, ex.GetType().Name);
                result = Failed(normalized, "Native usage read failed.");
            }
        }
        return NativeUsageDtoMapper.ToDto(result with { Provider = normalized });
    }

    public async Task<string> FetchUsageJsonAsync(string provider, CancellationToken ct = default) =>
        SerializeDto(await FetchUsageDtoAsync(provider, ct).ConfigureAwait(false));

    public async Task<IReadOnlyDictionary<string, string>> FetchAllUsageJsonAsync(IReadOnlyList<string> providers, CancellationToken ct = default)
    {
        var normalized = providers.Select(ProviderConstants.ValidateAndNormalize).Distinct(StringComparer.Ordinal).ToArray();
        var texts = await Task.WhenAll(normalized.Select(p => FetchUsageJsonAsync(p, ct))).ConfigureAwait(false);
        return normalized.Zip(texts).ToDictionary(x => x.First, x => x.Second, StringComparer.Ordinal);
    }

    /// <summary>Serializes the mapped DTO in the CLI's array shape with the app's JSON context.</summary>
    public static string Serialize(NativeUsageResult result) =>
        SerializeDto(NativeUsageDtoMapper.ToDto(result));

    private static string SerializeDto(UsageDataDto dto)
    {
        // A default JsonElement cannot be written; "error": null reads back exactly like a missing error.
        if (dto.Error.ValueKind == JsonValueKind.Undefined) dto = dto with { Error = NullElement };
        return JsonSerializer.Serialize(new List<UsageDataDto> { dto }, AppJsonSerializerContext.Default.ListUsageDataDto);
    }

    public void Dispose() => _ownedHttp?.Dispose();

    private static IReadOnlyDictionary<string, INativeUsageClient> Index(IEnumerable<INativeUsageClient> clients) =>
        clients.ToDictionary(c => ProviderConstants.ValidateAndNormalize(c.ProviderId), StringComparer.Ordinal);

    private static NativeUsageResult Failed(string provider, string message) =>
        new() { Provider = provider, Source = "oauth", Status = NativeUsageStatus.Failed, Message = message };
}
