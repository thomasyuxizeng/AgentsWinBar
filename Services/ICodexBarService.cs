using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

/// <summary>
/// Interface for CodexBar CLI service.
/// </summary>
public interface ICodexBarService
{
    /// <summary>Cached cost scan attempts; never starts or waits for a WSL scan.</summary>
    IReadOnlyList<DashboardCostCacheEntry> GetCostCacheInfo();

    /// <summary>Scans local logs at most every 30 minutes, unless manually refreshed.</summary>
    Task<CostData?> GetCostAsync(string provider, bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>Scans due providers in one WSL batch, yielding each cached or completed result.</summary>
    IAsyncEnumerable<(string Provider, CostData? Cost)> GetCostsAsync(IReadOnlyList<string> providers,
        bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>
    /// Gets usage data for a specific provider.
    /// </summary>
    Task<UsageData?> GetUsageAsync(string provider, CancellationToken ct = default);

    /// <summary>
    /// Gets usage data for all enabled providers.
    /// </summary>
    Task<IReadOnlyList<UsageData>> GetAllUsageAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets usage data for all enabled providers as a stream (completed items yielded as they finish).
    /// </summary>
    IAsyncEnumerable<UsageData> GetAllUsageStreamAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets usage data for the given providers only (each must also be enabled) as a stream, so a per-provider
    /// schedule can read just the providers that are due. The default filters the full stream (test doubles).
    /// </summary>
    IAsyncEnumerable<UsageData> GetUsageStreamAsync(IReadOnlyCollection<string> providers, CancellationToken ct = default) =>
        UsageStreams.Only(GetAllUsageStreamAsync(ct), providers, ct);

    /// <summary>
    /// When the provider's native reader stops holding back after an HTTP 429 (UTC), or null when it would call now
    /// (or reads through WSL). A hint for the refresh schedule; the reader enforces the cooldown itself.
    /// </summary>
    DateTime? GetRateLimitedUntil(string provider) => null;

    /// <summary>
    /// Gets the version of codexbar CLI, or "native" when no enabled provider reads usage through it (no WSL call).
    /// </summary>
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if codexbar CLI is available; true without a WSL call when no enabled provider needs it.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>Helpers for <see cref="ICodexBarService"/> usage streams.</summary>
public static class UsageStreams
{
    /// <summary>The items of <paramref name="source"/> whose provider is in <paramref name="providers"/> (any case).</summary>
    public static async IAsyncEnumerable<UsageData> Only(IAsyncEnumerable<UsageData> source, IReadOnlyCollection<string> providers,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var wanted = new HashSet<string>(providers, StringComparer.OrdinalIgnoreCase);
        await foreach (var data in source.WithCancellation(ct))
        {
            if (wanted.Contains(data.Provider)) yield return data;
        }
    }
}
