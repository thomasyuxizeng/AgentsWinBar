using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

public interface IDashboardService
{
    DashboardSnapshot GetSnapshot(IReadOnlyList<UsageData> providers, DateTime? lastRefreshUtc,
        DateTime? nextRefreshUtc = null, TimeSpan? effectiveRefreshInterval = null);
    Task RefreshDiagnosticsAsync(CancellationToken ct = default);
    Task RefreshApiCreditsAsync(bool force, CancellationToken ct = default);
}
