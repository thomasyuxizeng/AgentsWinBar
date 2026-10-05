using CodexBarWin.Models;

namespace CodexBarWin.Services;

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
    /// Gets the version of codexbar CLI.
    /// </summary>
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if codexbar CLI is available.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
